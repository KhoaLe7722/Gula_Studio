using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using CharacterConceptAI.Server.AI;
using CharacterConceptAI.Server.Authorization;
using CharacterConceptAI.Server.Data;
using CharacterConceptAI.Server.Data.Entities;
using CharacterConceptAI.Server.Storage;
using CharacterConceptAI.Shared.Contracts.Generations;

namespace CharacterConceptAI.Server.BackgroundJobs;

/// <summary>
/// Event-driven wakeup &amp; in-flight cancellation coordinator between HTTP endpoints and GenerationBackgroundWorker.
/// Cuts job pickup latency to ~0ms and eliminates 300ms idle SQL Server polling.
/// </summary>
public sealed class GenerationJobSignal
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _activeJobTokens = new();

    public void NotifyNewJob() => _channel.Writer.TryWrite(true);

    public async Task WaitForSignalOrTimeoutAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        delayCts.CancelAfter(timeout);
        try
        {
            await _channel.Reader.ReadAsync(delayCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Normal fallback wake
        }
    }

    public CancellationTokenSource RegisterActiveJob(Guid jobId, CancellationToken stoppingToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _activeJobTokens[jobId] = cts;
        return cts;
    }

    public void UnregisterActiveJob(Guid jobId, CancellationTokenSource cts)
    {
        _activeJobTokens.TryRemove(new KeyValuePair<Guid, CancellationTokenSource>(jobId, cts));
        cts.Dispose();
    }

    public void CancelActiveJob(Guid jobId)
    {
        if (_activeJobTokens.TryGetValue(jobId, out var cts))
        {
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }
}

public class GenerationBackgroundWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly GenerationJobSignal _signal;
    private readonly ILogger<GenerationBackgroundWorker> _logger;
    private bool _reconciled;

    public GenerationBackgroundWorker(
        IServiceScopeFactory scopeFactory,
        GenerationJobSignal signal,
        ILogger<GenerationBackgroundWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _signal = signal;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("GenerationBackgroundWorker started.");

        _reconciled = await ReconcileOrphanJobsOnStartupAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_reconciled)
                {
                    _reconciled = await ReconcileOrphanJobsOnStartupAsync(stoppingToken);
                    if (!_reconciled)
                    {
                        await Task.Delay(2000, stoppingToken);
                        continue;
                    }
                }

                var processed = await ProcessNextJobAsync(stoppingToken);
                if (!processed)
                {
                    // Event-driven wakeup on new/retried jobs with 4s safety fallback for multi-node DB inserts
                    await _signal.WaitForSignalOrTimeoutAsync(TimeSpan.FromSeconds(4), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (SqlException ex)
            {
                // UNIT-009: Safe log and backoff if SQL Server connection drops; no Comfy execution, no host crash
                _logger.LogWarning("SQL Server connection issue in worker loop ({Message}). Backing off...", ex.Message);
                await Task.Delay(2000, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in GenerationBackgroundWorker loop.");
                await Task.Delay(2000, stoppingToken);
            }
        }

        _logger.LogInformation("GenerationBackgroundWorker stopped.");
    }

    private async Task<bool> ReconcileOrphanJobsOnStartupAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            await using var gpuLease = await SqlServerGpuLease.TryAcquireAsync(db, ct);
            if (!gpuLease.IsAcquired) return false; // Another live service is still processing.

            var orphans = await db.GenerationJobs
                .Where(j => j.Status == GenerationStatus.Running || j.Status == GenerationStatus.Interrupted)
                .ToListAsync(ct);

            if (orphans.Count > 0)
            {
                foreach (var orphan in orphans)
                {
                    orphan.Status = GenerationStatus.Interrupted;
                    orphan.ErrorCode = "RECONCILIATION_REQUIRED";
                    orphan.ErrorSummary = "Máy chủ khởi động lại khi tác vụ chưa kết thúc. Cần kiểm tra ComfyUI trước khi thử lại để tránh tạo ảnh trùng.";
                    orphan.LeaseUntilUtc = null;
                }
                await db.SaveChangesAsync(ct);
                _logger.LogInformation("Marked {Count} orphan jobs Interrupted for reconciliation; automatic GPU replay is disabled.", orphans.Count);
            }
            return true;
        }
        catch (Exception ex)
        {
            // UNIT-009: Log warning without crash if SQL is temporarily down at startup; loop will retry
            _logger.LogWarning("Could not reconcile orphan jobs at startup ({Message}). Will retry during normal loop.", ex.Message);
            return false;
        }
    }

    private async Task<bool> ProcessNextJobAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Fast read-only check avoids acquiring SQL GPU applock while completely idle (prevents spurious 409 on Upscale)
        var hasPendingOrExpired = await db.GenerationJobs.AsNoTracking()
            .AnyAsync(j => j.Status == GenerationStatus.Pending ||
                (j.Status == GenerationStatus.Running && j.LeaseUntilUtc != null && j.LeaseUntilUtc < DateTimeOffset.UtcNow), ct);
        if (!hasPendingOrExpired) return false;

        var aiClient = scope.ServiceProvider.GetRequiredService<IImageGenerationClient>();
        var mediaStore = scope.ServiceProvider.GetRequiredService<IMediaStore>();

        await using var gpuLease = await SqlServerGpuLease.TryAcquireAsync(db, ct);
        if (!gpuLease.IsAcquired) return false;

        Guid? claimedJobId = null;

        // Atomic Claim from SQL Server (UPDLOCK, ROWLOCK, READPAST) or Transaction (JOB-001)
        if (db.Database.IsSqlServer())
        {
            var sql = """
                SET NOCOUNT ON;
                BEGIN TRANSACTION;
                DECLARE @claimLock int;
                EXEC @claimLock = sys.sp_getapplock @Resource = 'CharacterConceptAI.GenerationClaim',
                    @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 0;
                IF @claimLock < 0
                BEGIN
                    COMMIT TRANSACTION;
                    SELECT TOP(0) [Id] AS [Value] FROM [GenerationJobs];
                    RETURN;
                END;
                -- Reclaim any crashed/stale Running jobs whose lease expired (we already hold the exclusive GPU lease)
                UPDATE [GenerationJobs]
                SET [Status] = 5, -- Interrupted
                    [ErrorCode] = 'LEASE_EXPIRED',
                    [ErrorSummary] = N'Tác vụ trước đó bị gián đoạn quá thời hạn giữ khóa GPU. Hãy bấm Thử lại nếu cần.',
                    [LeaseUntilUtc] = NULL
                WHERE [Status] = 1 AND [LeaseUntilUtc] IS NOT NULL AND [LeaseUntilUtc] < SYSUTCDATETIME();
                IF EXISTS (SELECT 1 FROM [GenerationJobs] WHERE [Status] = 1)
                BEGIN
                    COMMIT TRANSACTION;
                    SELECT TOP(0) [Id] AS [Value] FROM [GenerationJobs];
                    RETURN;
                END;
                WITH NextJob AS (
                    SELECT TOP(1) *
                    FROM [GenerationJobs] WITH (UPDLOCK, ROWLOCK, READPAST)
                    WHERE [Status] = 0 -- Pending
                    ORDER BY [RequestedAt] ASC
                )
                UPDATE NextJob
                SET [Status] = 1, -- Running
                    [StartedAt] = SYSUTCDATETIME(),
                    [AttemptCount] = [AttemptCount] + 1,
                    [LeaseUntilUtc] = DATEADD(minute, 10, SYSUTCDATETIME())
                OUTPUT INSERTED.[Id] AS [Value];
                COMMIT TRANSACTION;
                """;

            var ids = await db.Database.SqlQueryRaw<Guid>(sql).ToListAsync(ct);
            if (ids.Count > 0)
            {
                claimedJobId = ids[0];
            }
        }
        else
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var jobCandidate = await db.GenerationJobs
                .Where(j => j.Status == GenerationStatus.Pending)
                .OrderBy(j => j.RequestedAt)
                .FirstOrDefaultAsync(ct);

            if (jobCandidate != null)
            {
                jobCandidate.Status = GenerationStatus.Running;
                jobCandidate.StartedAt = DateTimeOffset.UtcNow;
                jobCandidate.AttemptCount++;
                jobCandidate.LeaseUntilUtc = DateTimeOffset.UtcNow.AddMinutes(10);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                claimedJobId = jobCandidate.Id;
            }
        }

        if (claimedJobId is null) return false;

        var job = await db.GenerationJobs
            .Include(j => j.OutputAssets)
            .FirstOrDefaultAsync(j => j.Id == claimedJobId.Value, ct);

        if (job is null) return false;

        _logger.LogInformation("Claimed and processing GenerationJob {JobId} (RequestedCount: {Count})", job.Id, job.RequestedCount);

        var jobCts = _signal.RegisterActiveJob(job.Id, ct);
        var jobToken = jobCts.Token;
        try
        {
            var auth = scope.ServiceProvider.GetRequiredService<IProjectAuthorizationService>();
            var access = await auth.AuthorizeAsync(job.RequestedByUserId, job.ProjectId, "Designer", ct: jobToken);
            if (!access.IsAuthorized || !await db.Characters.AsNoTracking().AnyAsync(c => c.Id == job.CharacterId && c.Status != "Archived", jobToken))
                throw new InvalidOperationException("Tác vụ không còn quyền sinh ảnh hoặc nhân vật đã được lưu trữ.");

            var countToGenerate = Math.Clamp(job.RequestedCount, 1, 4);

            byte[]? refImageBytes = null;
            string? resolvedInputSha = job.InputAssetSha256;
            if (job.ReferenceConceptVersionId.HasValue)
            {
                var refVersion = await db.ConceptVersions
                    .AsNoTracking()
                    .Include(v => v.ImageAsset)
                    .FirstOrDefaultAsync(v => v.Id == job.ReferenceConceptVersionId.Value, jobToken);

                if (refVersion?.ImageAsset != null)
                {
                    resolvedInputSha ??= refVersion.ImageAsset.Sha256;
                    await using var readStream = await mediaStore.OpenReadAsync(refVersion.ImageAsset.StorageKey, jobToken);
                    if (readStream != null)
                    {
                        using var ms = new MemoryStream();
                        await readStream.CopyToAsync(ms, jobToken);
                        refImageBytes = ms.ToArray();
                    }
                }
                if (refImageBytes is null)
                    throw new InvalidOperationException("Không thể đọc ảnh tham chiếu. Tác vụ Img2Img không được chuyển thành Text-to-Image.");
            }

            var extras = GenerationExtrasSnapshot.Read(job);
            byte[]? depthPoseBytes = null;
            if (extras.DepthPose is { } depthExtra && !aiClient.IsSimulated)
            {
                var poseGallery = scope.ServiceProvider.GetRequiredService<IDepthPoseGalleryService>();
                depthPoseBytes = await poseGallery.TryReadDepthImageBytesAsync(depthExtra.PoseId, jobToken);
                if (depthPoseBytes is null || depthPoseBytes.Length == 0)
                    throw new InvalidOperationException($"Không thể đọc ảnh bản đồ sâu (Depth) cho tư thế '{depthExtra.PoseId}'.");
            }

            byte[]? faceRefBytes = null;
            List<byte[]>? additionalFaceRefs = null;
            byte[]? secondaryFaceRefBytes = null;
            List<byte[]>? secondaryAdditionalFaceRefs = null;
            if (extras.FaceIdentity is not null || extras.SecondaryFaceIdentity is not null)
            {
                var allFaceIds = new HashSet<Guid>();
                var primaryExtraIds = extras.FaceIdentity?.AdditionalReferenceConceptVersionIds?.Take(4).ToList() ?? [];
                var secondaryExtraIds = extras.SecondaryFaceIdentity?.AdditionalReferenceConceptVersionIds?.Take(4).ToList() ?? [];

                if (extras.FaceIdentity is { } pf)
                {
                    allFaceIds.Add(pf.ReferenceConceptVersionId);
                    foreach (var id in primaryExtraIds) allFaceIds.Add(id);
                }
                if (extras.SecondaryFaceIdentity is { } sf)
                {
                    allFaceIds.Add(sf.ReferenceConceptVersionId);
                    foreach (var id in secondaryExtraIds) allFaceIds.Add(id);
                }

                var faceVersionMap = await db.ConceptVersions
                    .AsNoTracking()
                    .Include(v => v.ImageAsset)
                    .Where(v => allFaceIds.Contains(v.Id))
                    .ToDictionaryAsync(v => v.Id, jobToken);

                async Task<byte[]?> ReadFaceBytesAsync(Guid verId)
                {
                    if (!faceVersionMap.TryGetValue(verId, out var ver) || ver.ImageAsset == null) return null;
                    await using var s = await mediaStore.OpenReadAsync(ver.ImageAsset.StorageKey, jobToken);
                    if (s == null) return null;
                    using var ms = new MemoryStream();
                    await s.CopyToAsync(ms, jobToken);
                    return ms.Length > 0 ? ms.ToArray() : null;
                }

                if (extras.FaceIdentity is { } faceExtra)
                {
                    faceRefBytes = await ReadFaceBytesAsync(faceExtra.ReferenceConceptVersionId);
                    if (faceRefBytes is null && !aiClient.IsSimulated)
                        throw new InvalidOperationException("Không thể đọc ảnh gương mặt gốc của nhân vật để đồng nhất khuôn mặt.");

                    if (primaryExtraIds.Count > 0)
                    {
                        additionalFaceRefs = [];
                        foreach (var extraVerId in primaryExtraIds)
                        {
                            var bytes = await ReadFaceBytesAsync(extraVerId);
                            if (bytes is { Length: > 0 }) additionalFaceRefs.Add(bytes);
                        }
                    }
                }

                if (extras.SecondaryFaceIdentity is { } secExtra)
                {
                    secondaryFaceRefBytes = await ReadFaceBytesAsync(secExtra.ReferenceConceptVersionId);
                    if (secondaryFaceRefBytes is null && !aiClient.IsSimulated)
                        throw new InvalidOperationException("Không thể đọc ảnh gương mặt nhân vật thứ 2.");

                    if (secondaryExtraIds.Count > 0)
                    {
                        secondaryAdditionalFaceRefs = [];
                        foreach (var extraVerId in secondaryExtraIds)
                        {
                            var bytes = await ReadFaceBytesAsync(extraVerId);
                            if (bytes is { Length: > 0 }) secondaryAdditionalFaceRefs.Add(bytes);
                        }
                    }
                }
            }

            for (int i = job.CompletedCount; i < countToGenerate; i++)
            {
                await db.Entry(job).ReloadAsync(jobToken);
                if (job.Status == GenerationStatus.Cancelled)
                {
                    _logger.LogInformation("GenerationJob {JobId} was cancelled by user mid-execution.", job.Id);
                    return true;
                }

                // Renew lease for each image iteration so multi-image Full 2x runs never look expired
                job.LeaseUntilUtc = DateTimeOffset.UtcNow.AddMinutes(10);
                if (resolvedInputSha is not null && job.InputAssetSha256 is null)
                    job.InputAssetSha256 = resolvedInputSha;

                if (ct.IsCancellationRequested)
                {
                    job.Status = GenerationStatus.Interrupted;
                    job.ErrorSummary = "Bị ngắt do tắt máy chủ";
                    job.LeaseUntilUtc = null;
                    await SaveTerminalStateSafeAsync(db, job);
                    return true;
                }

                var seedForIteration = job.Seed + i;
                var genResult = await aiClient.GenerateAsync(new GenerationExecutionRequest(
                    JobId: job.Id,
                    Prompt: job.FinalPrompt,
                    Width: job.Width,
                    Height: job.Height,
                    Seed: seedForIteration,
                    Denoise: job.Denoise,
                    ReferenceImageBytes: refImageBytes,
                    FullQuality: ComfyUiClient.IsFullQualityWorkflow(job.WorkflowVersion),
                    RefineFace: job.WorkflowVersion == ComfyUiClient.FullFaceWorkflowVersion,
                    DepthPoseImageBytes: depthPoseBytes,
                    DepthStrength: extras.DepthPose?.Strength,
                    FaceReferenceImageBytes: faceRefBytes,
                    FaceSwapStrength: extras.FaceIdentity?.Strength,
                    FaceRestoreStrength: extras.FaceIdentity?.RestoreStrength,
                    AdditionalFaceReferenceImageBytes: additionalFaceRefs is { Count: > 0 } ? additionalFaceRefs : null,
                    SecondaryFaceReferenceImageBytes: secondaryFaceRefBytes,
                    SecondaryFaceSwapStrength: extras.SecondaryFaceIdentity?.Strength,
                    SecondaryFaceRestoreStrength: extras.SecondaryFaceIdentity?.RestoreStrength,
                    SecondaryAdditionalFaceReferenceImageBytes: secondaryAdditionalFaceRefs is { Count: > 0 } ? secondaryAdditionalFaceRefs : null,
                    Loras: extras.Loras
                ), jobToken);

                await db.Entry(job).ReloadAsync(CancellationToken.None);
                if (job.Status == GenerationStatus.Cancelled) return true;

                if (!genResult.Success)
                {
                    job.Status = GenerationStatus.Failed;
                    job.ErrorCode = "AI_EXECUTION_FAILED";
                    job.ErrorSummary = genResult.ErrorMessage ?? "Lỗi khi gọi provider sinh ảnh";
                    job.CompletedAt = DateTimeOffset.UtcNow;
                    job.LeaseUntilUtc = null;
                    await SaveTerminalStateSafeAsync(db, job);
                    return true;
                }

                job.ComfyPromptId = genResult.ProviderPromptId;
                job.IsSimulated = aiClient.IsSimulated;
                if (resolvedInputSha is not null && job.InputAssetSha256 is null)
                    job.InputAssetSha256 = resolvedInputSha;
                if (genResult.Outputs.Count == 0)
                    throw new InvalidOperationException("Provider không trả về ảnh đầu ra.");

                var savedKeys = new List<string>(genResult.Outputs.Count);
                try
                {
                    foreach (var output in genResult.Outputs)
                    {
                        using var stream = new MemoryStream(output.ImageBytes);
                        var stored = await mediaStore.SaveAsync(job.ProjectId, stream, output.Extension, jobToken);
                        savedKeys.Add(stored.StorageKey);

                        var asset = new ImageAsset
                        {
                            Id = Guid.NewGuid(),
                            ProjectId = job.ProjectId,
                            GenerationJobId = job.Id,
                            StorageKey = stored.StorageKey,
                            ContentType = stored.ContentType,
                            SizeBytes = stored.SizeBytes,
                            Sha256 = stored.Sha256,
                            Width = stored.Width,
                            Height = stored.Height,
                            CreatedAt = DateTimeOffset.UtcNow
                        };

                        db.ImageAssets.Add(asset);
                        job.CompletedCount++;
                    }

                    await db.SaveChangesAsync(jobToken);
                }
                catch
                {
                    // Clean up newly written media files if DB commit failed (e.g. concurrent cancellation)
                    foreach (var key in savedKeys)
                        await mediaStore.DeleteAsync(key);
                    throw;
                }
            }

            await db.Entry(job).ReloadAsync(CancellationToken.None);
            if (job.Status == GenerationStatus.Cancelled) return true;
            job.Status = GenerationStatus.Completed;
            job.CompletedAt = DateTimeOffset.UtcNow;
            job.LeaseUntilUtc = null;
            if (resolvedInputSha is not null && job.InputAssetSha256 is null)
                job.InputAssetSha256 = resolvedInputSha;
            await db.SaveChangesAsync(CancellationToken.None);

            _logger.LogInformation("GenerationJob {JobId} successfully completed with {Count} images.", job.Id, job.CompletedCount);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            job.Status = GenerationStatus.Interrupted;
            job.CompletedAt = DateTimeOffset.UtcNow;
            job.LeaseUntilUtc = null;
            job.ErrorSummary = "Bị ngắt do máy chủ tắt";
            await SaveTerminalStateSafeAsync(db, job);
            return true;
        }
        catch (OperationCanceledException)
        {
            await db.Entry(job).ReloadAsync(CancellationToken.None);
            if (job.Status != GenerationStatus.Cancelled)
            {
                job.Status = GenerationStatus.Cancelled;
                job.CompletedAt = DateTimeOffset.UtcNow;
                job.LeaseUntilUtc = null;
                job.ErrorSummary = "Đã hủy theo yêu cầu";
                await SaveTerminalStateSafeAsync(db, job);
            }
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            await db.Entry(job).ReloadAsync(CancellationToken.None);
            _logger.LogInformation("GenerationJob {JobId} changed concurrently; retained its stored state {Status}.", job.Id, job.Status);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute generation job {JobId}", job.Id);
            job.Status = GenerationStatus.Failed;
            job.ErrorCode = "INTERNAL_WORKER_ERROR";
            job.ErrorSummary = "Không thể hoàn tất tác vụ. Kiểm tra cấu hình provider, quyền truy cập và lưu trữ trên máy chủ.";
            job.CompletedAt = DateTimeOffset.UtcNow;
            job.LeaseUntilUtc = null;
            await SaveTerminalStateSafeAsync(db, job);
            return true;
        }
        finally
        {
            _signal.UnregisterActiveJob(job.Id, jobCts);
        }
    }

    private async Task SaveTerminalStateSafeAsync(AppDbContext db, GenerationJob job)
    {
        try
        {
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (DbUpdateConcurrencyException)
        {
            await db.Entry(job).ReloadAsync(CancellationToken.None);
            _logger.LogInformation("GenerationJob {JobId} changed concurrently while saving terminal state; retained {Status}.", job.Id, job.Status);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Primary SaveChangesAsync failed for job {JobId}; retrying with fresh scope to prevent queue deadlock.", job.Id);
            try
            {
                using var fallbackScope = _scopeFactory.CreateScope();
                var fallbackDb = fallbackScope.ServiceProvider.GetRequiredService<AppDbContext>();
                var tracked = await fallbackDb.GenerationJobs.FirstOrDefaultAsync(j => j.Id == job.Id, CancellationToken.None);
                if (tracked is not null && tracked.Status == GenerationStatus.Running)
                {
                    tracked.Status = job.Status;
                    tracked.ErrorCode = job.ErrorCode;
                    tracked.ErrorSummary = job.ErrorSummary;
                    tracked.CompletedAt = job.CompletedAt;
                    tracked.LeaseUntilUtc = null;
                    await fallbackDb.SaveChangesAsync(CancellationToken.None);
                }
            }
            catch (Exception fallbackEx)
            {
                _logger.LogError(fallbackEx, "Fallback terminal update failed for job {JobId}.", job.Id);
            }
        }
    }
}

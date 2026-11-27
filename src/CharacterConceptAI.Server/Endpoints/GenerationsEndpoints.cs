using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using CharacterConceptAI.Server.AI;
using CharacterConceptAI.Server.Authorization;
using CharacterConceptAI.Server.BackgroundJobs;
using CharacterConceptAI.Server.Data;
using CharacterConceptAI.Server.Data.Entities;
using CharacterConceptAI.Shared.Contracts.Common;
using CharacterConceptAI.Shared.Contracts.Generations;

namespace CharacterConceptAI.Server.Endpoints;

public static class GenerationsEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static RouteGroupBuilder MapGenerationsEndpoints(this RouteGroupBuilder group)
    {
        var genGroup = group.RequireAuthorization();

        // 1. PROMPT PREVIEW (U05)
        genGroup.MapPost("/{projectId:guid}/characters/{characterId:guid}/prompt-preview", async (
            Guid projectId,
            Guid characterId,
            PromptPreviewRequest req,
            ClaimsPrincipal principal,
            IProjectAuthorizationService auth,
            IPromptBuilderService promptBuilder,
            AppDbContext db) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null) return Results.Unauthorized();

            var authResult = await auth.AuthorizeAsync(userId, projectId, "Designer", allowArchived: false);
            if (!authResult.IsAuthorized)
            {
                return authResult.Status switch
                {
                    ProjectAccessStatus.NotFound => Results.NotFound(),
                    ProjectAccessStatus.Archived => Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status422UnprocessableEntity),
                    _ => Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status403Forbidden)
                };
            }

            var character = await db.Characters
                .AsNoTrackingWithIdentityResolution()
                .Where(c => c.ProjectId == projectId && c.Id == characterId)
                .Include(c => c.AttributeValues)
                    .ThenInclude(av => av.Definition)
                .Include(c => c.Variants)
                    .ThenInclude(v => v.AttributeValues)
                        .ThenInclude(vav => vav.Definition)
                .FirstOrDefaultAsync();

            if (character is null) return Results.NotFound();

            if (req.VariantId.HasValue && !character.Variants.Any(v => v.Id == req.VariantId.Value))
            {
                return Results.Problem("Biến thể không thuộc về nhân vật này.", statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            var result = promptBuilder.BuildPrompt(character, req.VariantId, req.RawPrompt);
            var snapshotJson = CreateCanonicalSnapshot(character, db);

            return Results.Ok(new PromptPreviewDto(
                RawPrompt: result.RawPrompt,
                StructuredPrompt: result.StructuredPrompt,
                HybridPrompt: result.HybridPrompt,
                Warnings: result.Warnings,
                TemplateVersion: result.TemplateVersion,
                SnapshotJson: snapshotJson
            ));
        });

        // 2. CREATE GENERATION JOB (202 Accepted, Quota limit, Idempotency key, Snapshot)
        genGroup.MapPost("/{projectId:guid}/generations", async (
            Guid projectId,
            CreateGenerationRequest req,
            ClaimsPrincipal principal,
            IProjectAuthorizationService auth,
            IPromptBuilderService promptBuilder,
            IImageGenerationClient provider,
            IDepthPoseGalleryService poseGallery,
            IOutfitGalleryService outfitGallery,
            ILoraCatalogService loraCatalog,
            GenerationJobSignal jobSignal,
            IConfiguration configuration,
            AppDbContext db,
            CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null) return Results.Unauthorized();

            var authResult = await auth.AuthorizeAsync(userId, projectId, "Designer", allowArchived: false, ct: ct);
            if (!authResult.IsAuthorized)
            {
                return authResult.Status switch
                {
                    ProjectAccessStatus.NotFound => Results.NotFound(),
                    ProjectAccessStatus.Archived => Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status422UnprocessableEntity),
                    _ => Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status403Forbidden)
                };
            }

            var character = await db.Characters
                .AsNoTrackingWithIdentityResolution()
                .AsSplitQuery()
                .Where(c => c.ProjectId == projectId && c.Id == req.CharacterId)
                .Include(c => c.AttributeValues)
                    .ThenInclude(av => av.Definition)
                .Include(c => c.Variants)
                    .ThenInclude(v => v.AttributeValues)
                        .ThenInclude(vav => vav.Definition)
                .FirstOrDefaultAsync(ct);

            if (character is null) return Results.NotFound();

            if (req.VariantId.HasValue && !character.Variants.Any(v => v.Id == req.VariantId.Value))
            {
                return Results.Problem("Biến thể không tồn tại hoặc không thuộc về nhân vật này.", statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            if (character.Status == "Archived" || character.Variants.Any(v => v.Id == req.VariantId && v.Status == "Archived"))
                return Results.Problem("Nhân vật hoặc biến thể đã được lưu trữ.", statusCode: 422);
            if (!Enum.IsDefined(req.PromptMode) || req.Width < 256 || req.Width > 1536 || req.Width % 64 != 0 ||
                req.Height < 256 || req.Height > 1536 || req.Height % 64 != 0 || req.RequestedCount < 1 || req.RequestedCount > 4 ||
                req.RawPrompt?.Length > 12000 || req.FinalPrompt?.Length > 12000 || req.IdempotencyKey?.Length > 100 || req.PromptTemplateVersion?.Length > 50)
                return Results.Problem("Thông số sinh ảnh không hợp lệ. Kích thước phải là bội số 64 trong khoảng 256–1536, số ảnh từ 1–4.", statusCode: 422);

            // Idempotency is checked before quotas: a replay never creates work.
            var key = string.IsNullOrWhiteSpace(req.IdempotencyKey) ? null : req.IdempotencyKey.Trim();
            if (key is not null)
            {
                var replay = await db.GenerationJobs.FirstOrDefaultAsync(j => j.RequestedByUserId == userId && j.IdempotencyKey == key);
                if (replay is not null)
                {
                    if (replay.ProjectId != projectId)
                        return Results.Problem("Mã yêu cầu đã được sử dụng.", statusCode: 409);
                    return Results.Accepted($"/api/v1/projects/{projectId}/generations/{replay.Id}", new { id = replay.Id, status = replay.Status.ToString() });
                }
            }

            // Bound the user's queue across projects (JOB-011).
            var activeCount = await db.GenerationJobs.CountAsync(j =>
                j.RequestedByUserId == userId &&
                (j.Status == GenerationStatus.Pending || j.Status == GenerationStatus.Running));

            if (activeCount >= 5)
            {
                return Results.Problem(
                    "Vượt quá giới hạn hàng đợi cho phép (Tối đa 5 tác vụ đang chờ xử lý).",
                    statusCode: StatusCodes.Status429TooManyRequests);
            }

            if (req.FullQuality && !provider.IsSimulated && !configuration.GetValue<bool>("ComfyUI:FullQualityQualified"))
                return Results.Problem("Máy chủ chưa bật chế độ Full.", statusCode: 422);

            if (req.FullQuality && req.ReferenceConceptVersionId.HasValue)
                return Results.Problem("Chế độ Full dùng với tạo ảnh từ prompt.", statusCode: 422);

            DepthPoseExtra? depthPoseExtra = null;
            if (!string.IsNullOrWhiteSpace(req.DepthPoseId))
            {
                if (req.ReferenceConceptVersionId.HasValue)
                    return Results.Problem("Dáng 3D không thể kết hợp đồng thời với Img2Img.", statusCode: 422);
                if (!provider.IsSimulated && !poseGallery.PoseExists(req.DepthPoseId))
                    return Results.Problem($"Không tìm thấy dáng 3D '{req.DepthPoseId}' trong kho cục bộ.", statusCode: 422);
                depthPoseExtra = new DepthPoseExtra(req.DepthPoseId.Trim(), Math.Clamp(req.DepthStrength ?? 0.92, 0.35, 1.20));
            }

            FaceIdentityExtra? faceIdentityExtra = null;
            if (req.FaceReferenceConceptVersionId.HasValue)
            {
                var faceRefVersion = await db.ConceptVersions
                    .AsNoTracking()
                    .Include(cv => cv.Concept)
                    .Include(cv => cv.ImageAsset)
                    .FirstOrDefaultAsync(cv => cv.Id == req.FaceReferenceConceptVersionId.Value);
                if (faceRefVersion == null || faceRefVersion.Concept.ProjectId != projectId)
                    return Results.NotFound();

                List<Guid>? validatedExtraFaceIds = null;
                if (req.AdditionalFaceReferenceConceptVersionIds is { Count: > 0 })
                {
                    var candidateExtraIds = req.AdditionalFaceReferenceConceptVersionIds
                        .Where(id => id != Guid.Empty && id != faceRefVersion.Id)
                        .Distinct()
                        .Take(4)
                        .ToList();
                    if (candidateExtraIds.Count > 0)
                    {
                        var foundExtras = await db.ConceptVersions
                            .AsNoTracking()
                            .Include(cv => cv.Concept)
                            .Where(cv => candidateExtraIds.Contains(cv.Id) && cv.Concept.ProjectId == projectId)
                            .Select(cv => cv.Id)
                            .ToListAsync();
                        if (foundExtras.Count != candidateExtraIds.Count)
                            return Results.NotFound();
                        // Preserve requested slot order
                        validatedExtraFaceIds = candidateExtraIds.Where(id => foundExtras.Contains(id)).ToList();
                    }
                }

                faceIdentityExtra = new FaceIdentityExtra(
                    faceRefVersion.Id,
                    Math.Clamp(req.FaceSwapStrength ?? 0.90, 0.0, 1.0),
                    Math.Clamp(req.FaceRestoreStrength ?? 0.40, 0.0, 1.0),
                    validatedExtraFaceIds);
            }

            FaceIdentityExtra? secondaryFaceIdentityExtra = null;
            if (req.SecondaryFaceReferenceConceptVersionId.HasValue)
            {
                var secFaceRef = await db.ConceptVersions
                    .AsNoTracking()
                    .Include(cv => cv.Concept)
                    .Include(cv => cv.ImageAsset)
                    .FirstOrDefaultAsync(cv => cv.Id == req.SecondaryFaceReferenceConceptVersionId.Value, ct);
                if (secFaceRef == null || secFaceRef.Concept.ProjectId != projectId)
                    return Results.NotFound();

                List<Guid>? validatedSecExtras = null;
                if (req.SecondaryAdditionalFaceReferenceConceptVersionIds is { Count: > 0 })
                {
                    var candIds = req.SecondaryAdditionalFaceReferenceConceptVersionIds
                        .Where(id => id != Guid.Empty && id != secFaceRef.Id)
                        .Distinct()
                        .Take(4)
                        .ToList();
                    if (candIds.Count > 0)
                    {
                        var found = await db.ConceptVersions
                            .AsNoTracking()
                            .Include(cv => cv.Concept)
                            .Where(cv => candIds.Contains(cv.Id) && cv.Concept.ProjectId == projectId)
                            .Select(cv => cv.Id)
                            .ToListAsync(ct);
                        if (found.Count != candIds.Count)
                            return Results.NotFound();
                        validatedSecExtras = candIds.Where(id => found.Contains(id)).ToList();
                    }
                }

                secondaryFaceIdentityExtra = new FaceIdentityExtra(
                    secFaceRef.Id,
                    Math.Clamp(req.SecondaryFaceSwapStrength ?? 0.90, 0.0, 1.0),
                    Math.Clamp(req.SecondaryFaceRestoreStrength ?? 0.40, 0.0, 1.0),
                    validatedSecExtras);
            }

            List<LoraSelectionDto>? validatedLoras = null;
            if (req.Loras is { Count: > 0 })
            {
                validatedLoras = [];
                foreach (var lora in req.Loras.Take(3))
                {
                    if (string.IsNullOrWhiteSpace(lora.FileName)) continue;
                    if (!provider.IsSimulated && !loraCatalog.TryValidateLora(lora.FileName, out var normalized))
                        return Results.Problem($"Không tìm thấy tệp LoRA '{lora.FileName}' trong thư mục ComfyUI/models/loras.", statusCode: 422);
                    var finalName = provider.IsSimulated ? Path.GetFileName(lora.FileName.Trim()) : lora.FileName.Trim();
                    if (validatedLoras.All(x => !x.FileName.Equals(finalName, StringComparison.OrdinalIgnoreCase)))
                        validatedLoras.Add(new LoraSelectionDto(finalName, Math.Clamp(lora.Strength, 0.05, 1.50)));
                }
                if (validatedLoras.Count == 0) validatedLoras = null;
            }

            // Record the exact depth / face-identity / dual-face / LoRA metadata inside the immutable profile snapshot.
            var snapshotJson = CreateCanonicalSnapshot(character, db, depthPoseExtra, faceIdentityExtra, secondaryFaceIdentityExtra, validatedLoras);

            // Compute deterministic final prompt if not explicitly overridden
            string finalPrompt = req.FinalPrompt?.Trim() ?? string.Empty;
            var templateVersion = string.IsNullOrWhiteSpace(req.PromptTemplateVersion)
                ? PromptBuilderService.CurrentVersion : req.PromptTemplateVersion;
            if (string.IsNullOrWhiteSpace(finalPrompt))
            {
                var built = promptBuilder.BuildPrompt(character, req.VariantId, req.RawPrompt);
                templateVersion = built.TemplateVersion;
                finalPrompt = req.PromptMode switch
                {
                    PromptMode.Raw => string.IsNullOrWhiteSpace(req.RawPrompt) ? built.RawPrompt : req.RawPrompt.Trim(),
                    PromptMode.Structured => built.StructuredPrompt,
                    PromptMode.Hybrid => built.HybridPrompt,
                    _ => built.HybridPrompt
                };
            }

            if (!string.IsNullOrWhiteSpace(req.OutfitId) && !provider.IsSimulated && !outfitGallery.OutfitExists(req.OutfitId))
                return Results.Problem($"Không tìm thấy trang phục '{req.OutfitId}' trong kho cục bộ.", statusCode: 422);

            if (!string.IsNullOrWhiteSpace(req.OutfitId))
                finalPrompt = outfitGallery.BuildSynchronizedPrompt(req.OutfitId, finalPrompt);

            if (depthPoseExtra != null)
                finalPrompt = poseGallery.BuildSynchronizedPrompt(depthPoseExtra.PoseId, finalPrompt);

            // Validate Img2Img reference version if supplied (U08)
            string workflowKey = depthPoseExtra != null ? "depth_pose_union21" : "t2i_default";
            string? inputAssetSha = null;
            double? denoise = null;

            if (req.ReferenceConceptVersionId.HasValue)
            {
                if (!provider.IsSimulated && !configuration.GetValue<bool>("ComfyUI:Img2ImgQualified"))
                    return Results.Problem("Img2Img chưa được nghiệm thu trên máy chủ. Vui lòng dùng Text-to-Image.", title: "IMG2IMG_UNQUALIFIED", statusCode: 422);
                var refVersion = await db.ConceptVersions
                    .AsNoTracking()
                    .Include(cv => cv.Concept)
                    .Include(cv => cv.ImageAsset)
                    .FirstOrDefaultAsync(cv => cv.Id == req.ReferenceConceptVersionId.Value);

                if (refVersion == null || refVersion.Concept.ProjectId != projectId)
                {
                    // IDOR concealment: return 404 if version does not exist or belongs to another project
                    return Results.NotFound();
                }

                if (refVersion.Concept.CharacterId != req.CharacterId)
                {
                    return Results.Problem("Ảnh mẫu tham chiếu không thuộc về nhân vật này.", statusCode: StatusCodes.Status422UnprocessableEntity);
                }

                workflowKey = "i2i_reference";
                inputAssetSha = refVersion.ImageAsset?.Sha256;
                denoise = Math.Clamp(req.Denoise ?? 0.6, 0.05, 1.0);
            }

            var job = new GenerationJob
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                CharacterId = req.CharacterId,
                VariantId = req.VariantId,
                RequestedByUserId = userId,
                Status = GenerationStatus.Pending,
                PromptMode = req.PromptMode,
                RawPrompt = req.RawPrompt?.Trim(),
                FinalPrompt = finalPrompt,
                ProfileSnapshotJson = snapshotJson,
                PromptTemplateVersion = templateVersion,
                WorkflowKey = workflowKey,
                WorkflowVersion = !req.FullQuality ? "1.0"
                    : configuration.GetValue<bool>("ComfyUI:FullFaceQualified") ? ComfyUiClient.FullFaceWorkflowVersion
                    : ComfyUiClient.FullQualityWorkflowVersion,
                ReferenceConceptVersionId = req.ReferenceConceptVersionId,
                Denoise = denoise,
                InputAssetSha256 = inputAssetSha,
                Width = req.Width > 0 ? req.Width : 512,
                Height = req.Height > 0 ? req.Height : 512,
                RequestedCount = Math.Clamp(req.RequestedCount, 1, 4),
                CompletedCount = 0,
                Seed = req.Seed ?? Random.Shared.Next(1, 999999),
                IdempotencyKey = string.IsNullOrWhiteSpace(req.IdempotencyKey) ? null : req.IdempotencyKey.Trim(),
                IsSimulated = provider.IsSimulated,
                RequestedAt = DateTimeOffset.UtcNow,
                AttemptCount = 0
            };

            db.GenerationJobs.Add(job);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException) when (!string.IsNullOrWhiteSpace(job.IdempotencyKey))
            {
                // Concurrent idempotent retry hit unique index — replay existing job cleanly as 202 Accepted
                var existing = await db.GenerationJobs.AsNoTracking()
                    .FirstOrDefaultAsync(j => j.RequestedByUserId == userId && j.IdempotencyKey == job.IdempotencyKey, ct);
                if (existing is not null)
                {
                    return Results.Accepted($"/api/v1/projects/{projectId}/generations/{existing.Id}", new
                    {
                        id = existing.Id,
                        status = existing.Status.ToString(),
                        statusUrl = $"/api/v1/projects/{projectId}/generations/{existing.Id}",
                        replayed = true
                    });
                }
                throw;
            }
            jobSignal.NotifyNewJob();

            return Results.Accepted($"/api/v1/projects/{projectId}/generations/{job.Id}", new
            {
                id = job.Id,
                status = job.Status.ToString(),
                statusUrl = $"/api/v1/projects/{projectId}/generations/{job.Id}"
            });
        });

        // 3. LIST GENERATION JOBS (Paged & Filtered)
        genGroup.MapGet("/{projectId:guid}/generations", async (
            Guid projectId,
            string? status,
            int? page,
            int? pageSize,
            ClaimsPrincipal principal,
            IProjectAuthorizationService auth,
            AppDbContext db,
            CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null) return Results.Unauthorized();

            var authResult = await auth.AuthorizeAsync(userId, projectId, "Viewer", allowArchived: true, ct: ct);
            if (!authResult.IsAuthorized)
            {
                return authResult.Status == ProjectAccessStatus.NotFound
                    ? Results.NotFound()
                    : Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status403Forbidden);
            }

            var pageNumber = Math.Max(1, page ?? 1);
            var size = Math.Clamp(pageSize ?? 20, 1, 100);
            if ((long)(pageNumber - 1) * size > int.MaxValue)
                return Results.BadRequest(new { error = "Trang yêu cầu vượt giới hạn hỗ trợ." });

            var query = db.GenerationJobs
                .AsNoTracking()
                .Where(j => j.ProjectId == projectId)
                .Include(j => j.OutputAssets)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<GenerationStatus>(status, true, out var parsedStatus))
            {
                query = query.Where(j => j.Status == parsedStatus);
            }

            var totalCount = await query.CountAsync(ct);
            var items = await query
                .OrderByDescending(j => j.RequestedAt)
                .ThenByDescending(j => j.Id)
                .Skip((pageNumber - 1) * size)
                .Take(size)
                .ToListAsync(ct);

            var dtos = items.Select(MapToJobDto).ToList();
            return Results.Ok(new PagedResult<GenerationJobDto>
            {
                Items = dtos,
                TotalCount = totalCount,
                Page = pageNumber,
                PageSize = size
            });
        });

        // 4. GET SINGLE GENERATION JOB
        genGroup.MapGet("/{projectId:guid}/generations/{jobId:guid}", async (
            Guid projectId,
            Guid jobId,
            ClaimsPrincipal principal,
            IProjectAuthorizationService auth,
            AppDbContext db,
            CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null) return Results.Unauthorized();

            var authResult = await auth.AuthorizeAsync(userId, projectId, "Viewer", allowArchived: true, ct: ct);
            if (!authResult.IsAuthorized)
            {
                return authResult.Status == ProjectAccessStatus.NotFound
                    ? Results.NotFound()
                    : Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status403Forbidden);
            }

            var job = await db.GenerationJobs
                .AsNoTracking()
                .Include(j => j.OutputAssets)
                .FirstOrDefaultAsync(j => j.Id == jobId && j.ProjectId == projectId, ct);

            if (job is null) return Results.NotFound();

            return Results.Ok(MapToJobDto(job));
        });

        // 5. CANCEL GENERATION JOB (JOB-003, JOB-004, UNIT-010)
        genGroup.MapPost("/{projectId:guid}/generations/{jobId:guid}/cancel", async (
            Guid projectId,
            Guid jobId,
            ClaimsPrincipal principal,
            IProjectAuthorizationService auth,
            GenerationJobSignal jobSignal,
            AppDbContext db,
            CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null) return Results.Unauthorized();

            var authResult = await auth.AuthorizeAsync(userId, projectId, "Designer", allowArchived: false, ct: ct);
            if (!authResult.IsAuthorized)
            {
                return authResult.Status switch
                {
                    ProjectAccessStatus.NotFound => Results.NotFound(),
                    ProjectAccessStatus.Archived => Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status422UnprocessableEntity),
                    _ => Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status403Forbidden)
                };
            }

            var job = await db.GenerationJobs
                .FirstOrDefaultAsync(j => j.Id == jobId && j.ProjectId == projectId, ct);

            if (job is null) return Results.NotFound();

            var isOwner = string.Equals(authResult.UserRole, "Owner", StringComparison.OrdinalIgnoreCase);
            if (job.RequestedByUserId != userId && !isOwner)
            {
                return Results.Problem("Chỉ người tạo tác vụ hoặc Chủ dự án mới có quyền hủy tác vụ.", statusCode: StatusCodes.Status403Forbidden);
            }

            GenerationStatus previousStatus = default;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (attempt > 0)
                {
                    await db.Entry(job).ReloadAsync(ct);
                }

                if (job.Status == GenerationStatus.Completed ||
                    job.Status == GenerationStatus.Failed ||
                    job.Status == GenerationStatus.Cancelled)
                {
                    return Results.Problem(
                        $"Không thể hủy tác vụ đã ở trạng thái kết thúc ({job.Status}).",
                        statusCode: StatusCodes.Status409Conflict);
                }

                previousStatus = job.Status;
                job.Status = GenerationStatus.Cancelled;
                job.CompletedAt = DateTimeOffset.UtcNow;
                job.LeaseUntilUtc = null;
                job.ErrorSummary = $"Đã hủy bởi người dùng (Trạng thái trước: {previousStatus})";

                try
                {
                    await db.SaveChangesAsync(ct);
                    break;
                }
                catch (DbUpdateConcurrencyException) when (attempt < 2)
                {
                    // Worker likely transitioned Pending -> Running concurrently; reload and cancel Running job.
                }
                catch (DbUpdateConcurrencyException)
                {
                    return Results.Problem("Trạng thái tác vụ vừa thay đổi đồng thời. Vui lòng kiểm tra lại.", statusCode: StatusCodes.Status409Conflict);
                }
            }

            // Immediately abort any active worker/ComfyUI wait for this job
            jobSignal.CancelActiveJob(job.Id);

            return Results.Ok(new CancelJobResponse(
                job.Id,
                job.Status,
                $"Tác vụ đã được hủy thành công (từ {previousStatus})."
            ));
        });

        // 6. RETRY FAILED/INTERRUPTED/CANCELLED GENERATION JOB (JOB-005, UNIT-010)
        genGroup.MapPost("/{projectId:guid}/generations/{jobId:guid}/retry", async (
            Guid projectId,
            Guid jobId,
            ClaimsPrincipal principal,
            IProjectAuthorizationService auth,
            GenerationJobSignal jobSignal,
            AppDbContext db,
            CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null) return Results.Unauthorized();

            var authResult = await auth.AuthorizeAsync(userId, projectId, "Designer", allowArchived: false, ct: ct);
            if (!authResult.IsAuthorized)
            {
                return authResult.Status switch
                {
                    ProjectAccessStatus.NotFound => Results.NotFound(),
                    ProjectAccessStatus.Archived => Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status422UnprocessableEntity),
                    _ => Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status403Forbidden)
                };
            }

            var job = await db.GenerationJobs
                .FirstOrDefaultAsync(j => j.Id == jobId && j.ProjectId == projectId, ct);

            if (job is null) return Results.NotFound();

            var isOwner = string.Equals(authResult.UserRole, "Owner", StringComparison.OrdinalIgnoreCase);
            if (job.RequestedByUserId != userId && !isOwner)
            {
                return Results.Problem("Chỉ người tạo hoặc Chủ dự án mới có thể yêu cầu thử lại.", statusCode: StatusCodes.Status403Forbidden);
            }

            if (job.Status == GenerationStatus.Pending || job.Status == GenerationStatus.Running)
            {
                return Results.Problem(
                    $"Không thể thử lại tác vụ đang trong hàng đợi hoặc đang chạy ({job.Status}).",
                    statusCode: StatusCodes.Status409Conflict);
            }

            if (job.Status == GenerationStatus.Completed)
            {
                return Results.Problem(
                    "Không thể thử lại tác vụ đã hoàn thành thành công.",
                    statusCode: StatusCodes.Status409Conflict);
            }

            // Reset job to Pending (worker increments AttemptCount on claim — avoids double-counting!)
            job.Status = GenerationStatus.Pending;
            job.RequestedAt = DateTimeOffset.UtcNow;
            job.StartedAt = null;
            job.CompletedAt = null;
            job.ErrorCode = null;
            job.ErrorSummary = null;
            job.LeaseUntilUtc = null;

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Problem("Trạng thái tác vụ vừa thay đổi đồng thời. Vui lòng thử lại.", statusCode: StatusCodes.Status409Conflict);
            }
            jobSignal.NotifyNewJob();

            return Results.Accepted($"/api/v1/projects/{projectId}/generations/{job.Id}", new RetryJobResponse(
                job.Id,
                job.Status,
                $"Tác vụ đã được đưa lại vào hàng đợi xử lý (Lần thử {job.AttemptCount + 1})."
            ));
        });

        return group;
    }

    private static GenerationJobDto MapToJobDto(GenerationJob job)
    {
        var extras = GenerationExtrasSnapshot.Read(job);
        return new(
            job.Id,
            job.ProjectId,
            job.CharacterId,
            job.VariantId,
            job.RequestedByUserId,
            job.Status,
            job.PromptMode,
            job.FinalPrompt,
            job.Width,
            job.Height,
            job.RequestedCount,
            job.CompletedCount,
            job.Seed,
            job.ComfyPromptId,
            job.IsSimulated,
            job.RequestedAt,
            job.StartedAt,
            job.CompletedAt,
            job.ErrorSummary,
            job.OutputAssets.Select(a => a.Id).ToList(),
            job.RawPrompt,
            job.ErrorCode,
            job.PromptTemplateVersion,
            job.ReferenceConceptVersionId,
            job.Denoise,
            job.InputAssetSha256,
            job.WorkflowKey,
            ComfyUiClient.IsFullQualityWorkflow(job.WorkflowVersion),
            extras.DepthPose?.PoseId,
            extras.FaceIdentity?.ReferenceConceptVersionId,
            extras.FaceIdentity?.AdditionalReferenceConceptVersionIds
        );
    }

    private static string CreateCanonicalSnapshot(
        Character character,
        AppDbContext db,
        DepthPoseExtra? depthPose = null,
        FaceIdentityExtra? faceIdentity = null,
        FaceIdentityExtra? secondaryFaceIdentity = null,
        IReadOnlyList<LoraSelectionDto>? loras = null)
    {
        var missingDefIds = character.AttributeValues
            .Where(av => av.Definition == null)
            .Select(av => av.DefinitionId)
            .Concat(character.Variants.SelectMany(v => v.AttributeValues).Where(vav => vav.Definition == null).Select(vav => vav.DefinitionId))
            .Distinct()
            .ToList();

        Dictionary<Guid, string>? resolvedCodes = null;
        if (missingDefIds.Count > 0)
        {
            resolvedCodes = db.CharacterAttributeDefinitions
                .Where(d => missingDefIds.Contains(d.Id))
                .ToDictionary(d => d.Id, d => d.Code);
        }

        string? GetCode(Guid defId, CharacterAttributeDefinition? def)
        {
            if (def != null && !string.IsNullOrEmpty(def.Code)) return def.Code;
            if (resolvedCodes != null && resolvedCodes.TryGetValue(defId, out var code)) return code;
            return null;
        }

        var baseAttrs = character.AttributeValues
            .Select(av => new { Code = GetCode(av.DefinitionId, av.Definition), av.ValueJson })
            .Where(x => !string.IsNullOrEmpty(x.Code))
            .OrderBy(x => x.Code)
            .ToDictionary(x => x.Code!, x => x.ValueJson);

        var variants = character.Variants
            .OrderBy(v => v.Name)
            .Select(v => new
            {
                v.Id,
                v.Name,
                v.Description,
                v.IsDefault,
                v.Status,
                Overrides = v.AttributeValues
                    .Select(vav => new { Code = GetCode(vav.DefinitionId, vav.Definition), vav.ValueJson })
                    .Where(x => !string.IsNullOrEmpty(x.Code))
                    .OrderBy(x => x.Code)
                    .ToDictionary(x => x.Code!, x => x.ValueJson)
            }).ToList();

        var snapshotObj = new
        {
            CharacterId = character.Id,
            character.ProjectId,
            character.Name,
            character.Role,
            character.Description,
            character.Status,
            BaseAttributes = baseAttrs,
            Variants = variants,
            SnapshotAt = DateTimeOffset.UtcNow
        };

        if (depthPose == null && faceIdentity == null && secondaryFaceIdentity == null && (loras == null || loras.Count == 0))
            return JsonSerializer.Serialize(snapshotObj, JsonOpts);
        var withExtras = JsonSerializer.SerializeToNode(snapshotObj, JsonOpts)!.AsObject();
        GenerationExtrasSnapshot.Attach(withExtras, depthPose, faceIdentity, secondaryFaceIdentity, loras);
        return withExtras.ToJsonString(JsonOpts);
    }
}

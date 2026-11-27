using System.Security.Claims;
using System.Text.Json;
using CharacterConceptAI.Server.AI;
using CharacterConceptAI.Server.Authorization;
using CharacterConceptAI.Server.BackgroundJobs;
using CharacterConceptAI.Server.Data;
using CharacterConceptAI.Server.Storage;
using Microsoft.EntityFrameworkCore;

namespace CharacterConceptAI.Server.Endpoints;

internal static class UpscaleEndpoints
{
    public static void MapUpscaleEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/{projectId:guid}/media/{assetId:guid}/upscale", async (
            Guid projectId, Guid assetId, ClaimsPrincipal principal, AppDbContext db,
            IProjectAuthorizationService authorization, IMediaStore media,
            ComfyUiClient comfy, IConfiguration configuration, ILoggerFactory logs,
            HttpContext context, CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId == null) return Results.Unauthorized();
            var access = await authorization.AuthorizeAsync(userId, projectId, "Designer", allowArchived: false, ct: ct);
            if (!access.IsAuthorized) return Results.NotFound();

            var asset = await db.ImageAssets.AsNoTracking().SingleOrDefaultAsync(
                image => image.Id == assetId && image.ProjectId == projectId, ct);
            if (asset == null) return Results.NotFound();
            if (!configuration.GetValue<bool>("ComfyUI:UpscaleQualified"))
                return Results.Problem("Máy chủ chưa bật làm nét ảnh.", statusCode: 422);
            if (asset.Width is <= 0 or > 1536 || asset.Height is <= 0 or > 1536 || asset.SizeBytes is <= 0 or > 10_000_000)
                return Results.Problem("Ảnh nguồn cần có mỗi chiều tối đa 1536 px và dung lượng tối đa 10 MB.", statusCode: 422);

            string sourceFullPath;
            try { sourceFullPath = media.GetFullPath(asset.StorageKey); }
            catch (ArgumentException) { return Results.NotFound(); }
            if (!File.Exists(sourceFullPath)) return Results.NotFound();
            var sourceBytes = await File.ReadAllBytesAsync(sourceFullPath, ct);

            await using var lease = await SqlServerGpuLease.TryAcquireAsync(db, ct, lockTimeoutMs: 250);
            if (!lease.IsAcquired)
                return Results.Problem("GPU đang bận. Hãy thử làm nét sau khi tác vụ hiện tại kết thúc.", statusCode: 409);
            try
            {
                var bytes = await comfy.UpscaleAsync(sourceBytes, asset.Width, asset.Height, ct);
                context.Response.Headers.CacheControl = "no-store";
                return Results.File(bytes, "image/png");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return Results.StatusCode(499);
            }
            catch (Exception error) when (error is HttpRequestException or OperationCanceledException or InvalidOperationException or JsonException)
            {
                logs.CreateLogger(nameof(UpscaleEndpoints)).LogWarning(error, "Upscale failed for asset {AssetId}", assetId);
                return Results.Problem("ComfyUI không làm nét được ảnh. Hãy thử lại.", statusCode: 502);
            }
        }).RequireAuthorization();
    }
}

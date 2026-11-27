using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using CharacterConceptAI.Server.AI;
using CharacterConceptAI.Server.Authorization;
using CharacterConceptAI.Server.BackgroundJobs;
using CharacterConceptAI.Server.Data;
using CharacterConceptAI.Server.Data.Entities;
using CharacterConceptAI.Server.Storage;
using CharacterConceptAI.Shared.Contracts.Concepts;
using CharacterConceptAI.Shared.Contracts.Generations;
using Microsoft.EntityFrameworkCore;

namespace CharacterConceptAI.Server.Endpoints;

internal static class StudioEnhancementEndpoints
{
    public static void MapStudioEnhancementEndpoints(this RouteGroupBuilder api, RouteGroupBuilder projectGroup)
    {
        // 1. Magic Prompt Enhancer (Tongyi-MAI Z-Image pe.py + Z-Captioner)
        api.MapPost("/studio/enhance-prompt", async (
            EnhancePromptRequest req,
            IPromptEnhancerService enhancer,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Prompt))
                return Results.Problem("Nhập ý tưởng trước khi nâng cấp prompt.", statusCode: 422);
            if (req.Prompt.Length > 8000)
                return Results.Problem("Prompt vượt quá độ dài tối đa 8000 ký tự.", statusCode: 422);

            var result = await enhancer.EnhanceAsync(req, ct);
            return Results.Ok(result);
        }).RequireAuthorization();

        // 2. Direct Local Reference Image Upload (enables Drag-and-Drop Img2Img & Face Identity Lock without manual Concepts setup)
        projectGroup.MapPost("/{projectId:guid}/studio/upload-reference", async (
            Guid projectId,
            Guid characterId,
            HttpRequest httpRequest,
            ClaimsPrincipal principal,
            IProjectAuthorizationService auth,
            IMediaStore mediaStore,
            AppDbContext db,
            CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null) return Results.Unauthorized();

            var access = await auth.AuthorizeAsync(userId, projectId, "Designer", allowArchived: false, ct: ct);
            if (!access.IsAuthorized)
            {
                return access.Status switch
                {
                    ProjectAccessStatus.NotFound => Results.NotFound(),
                    ProjectAccessStatus.Archived => Results.Problem(access.ErrorMessage, statusCode: 422),
                    _ => Results.Problem(access.ErrorMessage, statusCode: 403)
                };
            }

            var characterExists = await db.Characters.AnyAsync(
                c => c.Id == characterId && c.ProjectId == projectId && c.Status != "Archived", ct);
            if (!characterExists) return Results.NotFound();

            if (!httpRequest.HasFormContentType)
                return Results.Problem("Yêu cầu tải ảnh dạng multipart/form-data.", statusCode: 400);

            var form = await httpRequest.ReadFormAsync(ct);
            var file = form.Files.GetFile("image") ?? form.Files.FirstOrDefault();
            if (file is null || file.Length <= 0)
                return Results.Problem("Chưa chọn tệp ảnh hợp lệ.", statusCode: 422);
            if (file.Length > 10_000_000)
                return Results.Problem("Ảnh tham chiếu không được vượt quá 10 MB.", statusCode: 422);

            var ext = Path.GetExtension(file.FileName).TrimStart('.').ToLowerInvariant();
            if (ext is not ("png" or "jpg" or "jpeg"))
                return Results.Problem("Chỉ hỗ trợ ảnh định dạng PNG hoặc JPG/JPEG.", statusCode: 422);

            StoredMediaResult stored;
            try
            {
                await using var stream = file.OpenReadStream();
                stored = await mediaStore.SaveAsync(projectId, stream, ext, ct);
            }
            catch (InvalidDataException ex)
            {
                return Results.Problem($"Tệp ảnh không hợp lệ: {ex.Message}", statusCode: 422);
            }

            const string refBucketTitle = "Ảnh tham chiếu Gula Studio";
            const string legacyRefBucketTitle = "Ảnh tham chiếu Dreamina Studio";
            var asset = new ImageAsset
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                StorageKey = stored.StorageKey,
                ContentType = stored.ContentType,
                SizeBytes = stored.SizeBytes,
                Sha256 = stored.Sha256,
                Width = stored.Width,
                Height = stored.Height,
                CreatedAt = DateTimeOffset.UtcNow
            };

            ConceptVersion? createdVersion = null;
            try
            {
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    db.ChangeTracker.Clear();
                    db.ImageAssets.Add(asset);

                    var concept = await db.CharacterConcepts
                        .FirstOrDefaultAsync(c => c.ProjectId == projectId && c.CharacterId == characterId &&
                            (c.Title == refBucketTitle || c.Title == legacyRefBucketTitle), ct);

                    int nextVer;
                    if (concept is null)
                    {
                        concept = new CharacterConcept
                        {
                            Id = Guid.NewGuid(),
                            ProjectId = projectId,
                            CharacterId = characterId,
                            Title = refBucketTitle,
                            Description = "Ảnh mẫu người dùng tải lên trực tiếp trong khung chat.",
                            Status = ConceptStatus.Draft,
                            CreatedByUserId = userId,
                            CreatedAt = DateTimeOffset.UtcNow,
                            UpdatedAt = DateTimeOffset.UtcNow
                        };
                        db.CharacterConcepts.Add(concept);
                        nextVer = 1;
                    }
                    else
                    {
                        nextVer = (await db.ConceptVersions.AsNoTracking()
                            .Where(v => v.ConceptId == concept.Id)
                            .MaxAsync(v => (int?)v.VersionNumber, ct) ?? 0) + 1;
                    }

                    var version = new ConceptVersion
                    {
                        Id = Guid.NewGuid(),
                        ConceptId = concept.Id,
                        VersionNumber = nextVer,
                        ImageAssetId = asset.Id,
                        FinalPromptSnapshot = "Uploaded local reference image",
                        Status = ConceptVersionStatus.Draft,
                        CreatedByUserId = userId,
                        CreatedAt = DateTimeOffset.UtcNow
                    };
                    db.ConceptVersions.Add(version);
                    concept.UpdatedAt = DateTimeOffset.UtcNow;

                    try
                    {
                        await db.SaveChangesAsync(ct);
                        createdVersion = version;
                        break;
                    }
                    catch (DbUpdateException) when (attempt < 2)
                    {
                        // Concurrent multi-image reference upload hit unique VersionNumber; retry cleanly
                    }
                }
            }
            catch
            {
                await mediaStore.DeleteAsync(stored.StorageKey);
                throw;
            }

            return Results.Ok(new UploadReferenceResponse(asset.Id, createdVersion!.Id, stored.Width, stored.Height));
        }).RequireAuthorization();

        // 3. Depth Pose Gallery Catalog (1,366 curated poses x 5 camera angles, Vietnamese localized)
        api.MapGet("/studio/pose-gallery", (
            bool? includeNsfw,
            string? system,
            string? category,
            string? angle,
            string? q,
            int? page,
            int? pageSize,
            IDepthPoseGalleryService gallery) =>
        {
            var result = gallery.Query(
                includeNsfw: includeNsfw ?? false,
                system: system,
                category: category,
                angle: angle,
                query: q,
                page: page ?? 1,
                pageSize: pageSize ?? 24);
            return Results.Ok(result);
        }).RequireAuthorization();

        // 4. Depth Pose Gallery Safe Image Stream (mannequin or depth map, thumb or full)
        api.MapGet("/studio/pose-gallery/image/{poseId}", (
            string poseId,
            string? kind,
            string? mode,
            IDepthPoseGalleryService gallery,
            HttpContext ctx) =>
        {
            if (!gallery.TryResolveImageFile(poseId, kind ?? "thumb", mode ?? "mannequin", out var fullPath, out var contentType))
                return Results.NotFound();

            ctx.Response.Headers.CacheControl = "private, max-age=86400";
            ctx.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.File(fullPath, contentType);
        }).RequireAuthorization();

        // 5. Outfit Gallery Catalog (1,841 curated local outfits across Male/Female/Unisex, ranked by verified accuracy)
        api.MapGet("/studio/outfit-gallery", (
            bool? includeNsfw,
            string? gender,
            string? collection,
            string? category,
            string? q,
            int? page,
            int? pageSize,
            IOutfitGalleryService outfitGallery) =>
        {
            var result = outfitGallery.Query(
                includeNsfw: includeNsfw ?? false,
                gender: gender,
                collection: collection,
                category: category,
                query: q,
                page: page ?? 1,
                pageSize: pageSize ?? 24);
            return Results.Ok(result);
        }).RequireAuthorization();

        // 6. Outfit Gallery Safe Image Stream (thumb or full, optional front/diag/side/back view)
        api.MapGet("/studio/outfit-gallery/image/{outfitId}", (
            string outfitId,
            string? kind,
            string? view,
            IOutfitGalleryService outfitGallery,
            HttpContext ctx) =>
        {
            if (!outfitGallery.TryResolveImageFile(outfitId, kind ?? "thumb", view ?? "front", out var fullPath, out var contentType))
                return Results.NotFound();

            ctx.Response.Headers.CacheControl = "private, max-age=86400";
            ctx.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.File(fullPath, contentType);
        }).RequireAuthorization();

        // 7. Curated & Dynamic Local LoRA Catalog (ComfyUI/models/loras)
        api.MapGet("/studio/loras", (ILoraCatalogService loraCatalog) =>
            Results.Ok(loraCatalog.ListAvailable())).RequireAuthorization();

        // 7b. GPU Engine Pre-Warmup (runs during Gula Studio Splash Screen so first generation is near-instant!)
        api.MapPost("/studio/warmup", async (
            IImageGenerationClient provider,
            AppDbContext db,
            ComfyUiClient comfy,
            ILoggerFactory logs,
            CancellationToken ct) =>
        {
            if (provider.IsSimulated)
                return Results.Ok(new { warmedUp = true, status = "simulated", elapsedSeconds = 0.0 });

            await using var lease = await SqlServerGpuLease.TryAcquireAsync(db, ct, lockTimeoutMs: 150);
            if (!lease.IsAcquired)
                return Results.Ok(new { warmedUp = false, status = "busy", elapsedSeconds = 0.0 });

            try
            {
                var (warmedUp, status, elapsed) = await comfy.WarmupModelsAsync(ct);
                return Results.Ok(new { warmedUp, status, elapsedSeconds = elapsed });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return Results.Ok(new { warmedUp = false, status = "cancelled", elapsedSeconds = 0.0 });
            }
            catch (Exception ex)
            {
                logs.CreateLogger(nameof(StudioEnhancementEndpoints)).LogWarning(ex, "GPU warmup skipped");
                return Results.Ok(new { warmedUp = false, status = "deferred", elapsedSeconds = 0.0 });
            }
        }).RequireAuthorization();

        // 8. 1-Click Local Background Removal (Bria RMBG-1.4 ONNX CPU, 0 VRAM -> RGBA Transparent PNG)
        projectGroup.MapPost("/{projectId:guid}/studio/remove-background", async (
            Guid projectId,
            HttpRequest httpRequest,
            ClaimsPrincipal principal,
            IProjectAuthorizationService auth,
            ComfyUiClient comfy,
            ILoggerFactory logs,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null) return Results.Unauthorized();
            var access = await auth.AuthorizeAsync(userId, projectId, "Designer", allowArchived: false, ct: ct);
            if (!access.IsAuthorized) return Results.NotFound();

            if (!httpRequest.HasFormContentType)
                return Results.Problem("Yêu cầu gửi ảnh dạng multipart/form-data.", statusCode: 400);
            var form = await httpRequest.ReadFormAsync(ct);
            var file = form.Files.GetFile("image") ?? form.Files.FirstOrDefault();
            if (file is null || file.Length <= 0 || file.Length > 25_000_000)
                return Results.Problem("Ảnh nguồn không hợp lệ hoặc vượt quá 25 MB.", statusCode: 422);

            var imgBytes = await ReadFormFileBytesAsync(file, ct);
            try
            {
                var resultBytes = await comfy.RemoveBackgroundAsync(imgBytes, ct: ct);
                ctx.Response.Headers.CacheControl = "no-store";
                return Results.File(resultBytes, "image/png");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Results.StatusCode(499); }
            catch (Exception ex)
            {
                logs.CreateLogger(nameof(StudioEnhancementEndpoints)).LogWarning(ex, "RemoveBackground failed");
                return Results.Problem($"Không thể tách nền ảnh: {ex.Message}", statusCode: 502);
            }
        }).RequireAuthorization();

        // 9. Tiled 2K Latent Hires-Fix & Smart Supersampled 2x Upscale (4x_NMKD-Siax / 4x-UltraSharp + Z-Image Turbo Tiled Pass)
        projectGroup.MapPost("/{projectId:guid}/studio/hires-upscale", async (
            Guid projectId,
            HttpRequest httpRequest,
            ClaimsPrincipal principal,
            AppDbContext db,
            IProjectAuthorizationService auth,
            ILoraCatalogService loraCatalog,
            ComfyUiClient comfy,
            ILoggerFactory logs,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null) return Results.Unauthorized();
            var access = await auth.AuthorizeAsync(userId, projectId, "Designer", allowArchived: false, ct: ct);
            if (!access.IsAuthorized) return Results.NotFound();

            if (!httpRequest.HasFormContentType)
                return Results.Problem("Yêu cầu gửi ảnh dạng multipart/form-data.", statusCode: 400);
            var form = await httpRequest.ReadFormAsync(ct);
            var file = form.Files.GetFile("image") ?? form.Files.FirstOrDefault();
            if (file is null || file.Length <= 0 || file.Length > 25_000_000)
                return Results.Problem("Ảnh nguồn không hợp lệ hoặc vượt quá 25 MB.", statusCode: 422);

            var prompt = form["prompt"].ToString();
            var useTiledHires = !bool.TryParse(form["useTiledHires"].ToString(), out var tiledVal) || tiledVal;
            var denoise = double.TryParse(form["denoise"].ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var dVal) ? dVal : 0.24;
            var upscaler = form["upscaler"].ToString();
            var loras = ParseValidatedLoras(form["lorasJson"].ToString(), loraCatalog);
            if (useTiledHires && (loras == null || loras.Count == 0) && loraCatalog.TryValidateLora("z_image_detailer.safetensors", out var detailer))
                loras = [new LoraSelectionDto(detailer, 0.35)];

            var imgBytes = await ReadFormFileBytesAsync(file, ct);

            await using var lease = await SqlServerGpuLease.TryAcquireAsync(db, ct, lockTimeoutMs: 250);
            if (!lease.IsAcquired)
                return Results.Problem("GPU đang bận. Hãy thử lại sau khi tác vụ hiện tại kết thúc.", statusCode: 409);

            try
            {
                var resultBytes = await comfy.HiresUpscale2KAsync(imgBytes, prompt, useTiledHires, denoise, upscaler, loras, ct: ct);
                ctx.Response.Headers.CacheControl = "no-store";
                return Results.File(resultBytes, "image/png");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Results.StatusCode(499); }
            catch (Exception ex)
            {
                logs.CreateLogger(nameof(StudioEnhancementEndpoints)).LogWarning(ex, "HiresUpscale2K failed");
                return Results.Problem($"Không thể siêu nét 2K cho ảnh: {ex.Message}", statusCode: 502);
            }
        }).RequireAuthorization();

        // 10. Magic Brush Inpainting (Z-Image-Turbo-Fun-Controlnet-Union-2.1)
        projectGroup.MapPost("/{projectId:guid}/studio/inpaint", async (
            Guid projectId,
            HttpRequest httpRequest,
            ClaimsPrincipal principal,
            AppDbContext db,
            IProjectAuthorizationService auth,
            ILoraCatalogService loraCatalog,
            ComfyUiClient comfy,
            ILoggerFactory logs,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null) return Results.Unauthorized();
            var access = await auth.AuthorizeAsync(userId, projectId, "Designer", allowArchived: false, ct: ct);
            if (!access.IsAuthorized) return Results.NotFound();

            if (!httpRequest.HasFormContentType)
                return Results.Problem("Yêu cầu gửi ảnh dạng multipart/form-data.", statusCode: 400);
            var form = await httpRequest.ReadFormAsync(ct);
            var imageFile = form.Files.GetFile("image");
            var maskFile = form.Files.GetFile("mask");
            if (imageFile is null || maskFile is null || imageFile.Length <= 0 || maskFile.Length <= 0 ||
                imageFile.Length > 25_000_000 || maskFile.Length > 25_000_000)
                return Results.Problem("Thiếu ảnh nguồn hoặc mặt nạ vùng sửa (mask).", statusCode: 422);

            var prompt = form["prompt"].ToString();
            var strength = double.TryParse(form["controlStrength"].ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var sVal) ? sVal : 0.90;
            var dilate = int.TryParse(form["maskDilate"].ToString(), out var dilVal) ? dilVal : 6;
            var feather = int.TryParse(form["featherPx"].ToString(), out var fVal) ? fVal : 14;
            long? seed = long.TryParse(form["seed"].ToString(), out var seedVal) ? seedVal : null;
            var loras = ParseValidatedLoras(form["lorasJson"].ToString(), loraCatalog);

            var srcBytes = await ReadFormFileBytesAsync(imageFile, ct);
            var maskBytes = await ReadFormFileBytesAsync(maskFile, ct);

            await using var lease = await SqlServerGpuLease.TryAcquireAsync(db, ct, lockTimeoutMs: 250);
            if (!lease.IsAcquired)
                return Results.Problem("GPU đang bận. Hãy thử lại sau khi tác vụ hiện tại kết thúc.", statusCode: 409);

            try
            {
                var resultBytes = await comfy.InpaintAsync(srcBytes, maskBytes, prompt, strength, dilate, feather, seed, loras, ct);
                ctx.Response.Headers.CacheControl = "no-store";
                return Results.File(resultBytes, "image/png");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Results.StatusCode(499); }
            catch (Exception ex)
            {
                logs.CreateLogger(nameof(StudioEnhancementEndpoints)).LogWarning(ex, "Inpaint failed");
                return Results.Problem($"Không thể sửa vùng chọn (Inpaint): {ex.Message}", statusCode: 502);
            }
        }).RequireAuthorization();

        // 11. Canvas Outpainting (Expand 3:4 -> 16:9 / 1:1 / +25% / Custom Borders using Union 2.1)
        projectGroup.MapPost("/{projectId:guid}/studio/outpaint", async (
            Guid projectId,
            HttpRequest httpRequest,
            ClaimsPrincipal principal,
            AppDbContext db,
            IProjectAuthorizationService auth,
            ILoraCatalogService loraCatalog,
            ComfyUiClient comfy,
            ILoggerFactory logs,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null) return Results.Unauthorized();
            var access = await auth.AuthorizeAsync(userId, projectId, "Designer", allowArchived: false, ct: ct);
            if (!access.IsAuthorized) return Results.NotFound();

            if (!httpRequest.HasFormContentType)
                return Results.Problem("Yêu cầu gửi ảnh dạng multipart/form-data.", statusCode: 400);
            var form = await httpRequest.ReadFormAsync(ct);
            var imageFile = form.Files.GetFile("image") ?? form.Files.FirstOrDefault();
            if (imageFile is null || imageFile.Length <= 0 || imageFile.Length > 25_000_000)
                return Results.Problem("Ảnh nguồn không hợp lệ hoặc vượt quá 25 MB.", statusCode: 422);

            var prompt = form["prompt"].ToString();
            var targetPreset = form["targetPreset"].ToString();
            var padLeft = int.TryParse(form["padLeft"].ToString(), out var pl) ? pl : 0;
            var padRight = int.TryParse(form["padRight"].ToString(), out var pr) ? pr : 0;
            var padTop = int.TryParse(form["padTop"].ToString(), out var pt) ? pt : 0;
            var padBottom = int.TryParse(form["padBottom"].ToString(), out var pb) ? pb : 0;
            var strength = double.TryParse(form["controlStrength"].ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var sVal) ? sVal : 0.90;
            long? seed = long.TryParse(form["seed"].ToString(), out var seedVal) ? seedVal : null;
            var loras = ParseValidatedLoras(form["lorasJson"].ToString(), loraCatalog);

            var srcBytes = await ReadFormFileBytesAsync(imageFile, ct);

            await using var lease = await SqlServerGpuLease.TryAcquireAsync(db, ct, lockTimeoutMs: 250);
            if (!lease.IsAcquired)
                return Results.Problem("GPU đang bận. Hãy thử lại sau khi tác vụ hiện tại kết thúc.", statusCode: 409);

            try
            {
                var resultBytes = await comfy.OutpaintAsync(srcBytes, prompt, targetPreset, padLeft, padRight, padTop, padBottom, strength, seed, loras, ct);
                ctx.Response.Headers.CacheControl = "no-store";
                return Results.File(resultBytes, "image/png");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Results.StatusCode(499); }
            catch (Exception ex)
            {
                logs.CreateLogger(nameof(StudioEnhancementEndpoints)).LogWarning(ex, "Outpaint failed");
                return Results.Problem($"Không thể mở rộng khung hình (Outpaint): {ex.Message}", statusCode: 502);
            }
        }).RequireAuthorization();
    }

    private static async Task<byte[]> ReadFormFileBytesAsync(IFormFile file, CancellationToken ct)
    {
        using var ms = new MemoryStream((int)Math.Min(file.Length, 25_000_000));
        await using var stream = file.OpenReadStream();
        await stream.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    private static List<LoraSelectionDto>? ParseValidatedLoras(string? json, ILoraCatalogService loraCatalog)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var raw = JsonSerializer.Deserialize<List<LoraSelectionDto>>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (raw is null || raw.Count == 0) return null;
            var valid = new List<LoraSelectionDto>();
            foreach (var item in raw.Take(3))
            {
                if (loraCatalog.TryValidateLora(item.FileName, out var normalized) &&
                    valid.All(v => !v.FileName.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
                {
                    valid.Add(new LoraSelectionDto(normalized, Math.Clamp(item.Strength, 0.05, 1.50)));
                }
            }
            return valid.Count > 0 ? valid : null;
        }
        catch { return null; }
    }
}

using CharacterConceptAI.Server.AI;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using CharacterConceptAI.Server.Authorization;
using CharacterConceptAI.Server.Data;
using CharacterConceptAI.Server.Data.Entities;
using CharacterConceptAI.Shared.Contracts.Concepts;
using CharacterConceptAI.Shared.Contracts.Common;

namespace CharacterConceptAI.Server.Endpoints;

public static class ConceptsEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static RouteGroupBuilder MapConceptsEndpoints(this RouteGroupBuilder group)
    {
        var conceptGroup = group.RequireAuthorization();

        // Project gallery/review queue does not require a selected character.
        conceptGroup.MapGet("/{projectId:guid}/concepts", async (
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
            var access = await auth.AuthorizeAsync(userId, projectId, "Viewer", allowArchived: true, ct: ct);
            if (!access.IsAuthorized)
                return access.Status == ProjectAccessStatus.NotFound ? Results.NotFound() : Results.Problem(access.ErrorMessage, statusCode: 403);
            var pageNumber = Math.Max(1, page ?? 1);
            var size = Math.Clamp(pageSize ?? 20, 1, 100);
            if ((long)(pageNumber - 1) * size > int.MaxValue)
                return Results.BadRequest(new { error = "Trang yêu cầu vượt giới hạn hỗ trợ." });
            var q = db.CharacterConcepts.AsNoTracking().Where(c => c.ProjectId == projectId);
            if (!string.IsNullOrWhiteSpace(status))
            {
                if (status.Equals("PendingReview", StringComparison.OrdinalIgnoreCase))
                    q = q.Where(c => c.Status != ConceptStatus.Archived &&
                        (c.Status == ConceptStatus.UnderReview || c.Versions.Any(v => v.Status == ConceptVersionStatus.Submitted)));
                else if (Enum.TryParse<ConceptStatus>(status, true, out var parsed) && Enum.IsDefined(parsed))
                    q = q.Where(c => c.Status == parsed);
                else return Results.BadRequest(new { error = "Trạng thái concept không hợp lệ." });
            }
            var total = await q.CountAsync(ct);
            var items = await q.OrderByDescending(c => c.UpdatedAt ?? c.CreatedAt).ThenBy(c => c.Id)
                .Skip((pageNumber - 1) * size).Take(size)
                .Select(c => new CharacterConceptDto(
                    c.Id, c.ProjectId, c.CharacterId, c.Character.Name, c.VariantId,
                    c.Variant == null ? null : c.Variant.Name, c.Title, c.Description, c.Status,
                    c.RepresentativeVersionId,
                    c.RepresentativeVersion == null ? null : c.RepresentativeVersion.ImageAssetId,
                    c.RepresentativeVersion == null ? null : "/api/v1/projects/" + projectId + "/media/" + c.RepresentativeVersion.ImageAssetId,
                    c.Versions.Count, c.CreatedAt, c.UpdatedAt,
                    c.RowVersion == null ? null : Convert.ToBase64String(c.RowVersion)))
                .ToListAsync(ct);
            return Results.Ok(new PagedResult<CharacterConceptDto> { Items = items, Page = pageNumber, PageSize = size, TotalCount = total });
        });

        // 1. GET Character Concepts Gallery
        conceptGroup.MapGet("/{projectId:guid}/characters/{characterId:guid}/concepts", async (
            Guid projectId,
            Guid characterId,
            ClaimsPrincipal principal,
            IProjectAuthorizationService auth,
            AppDbContext db) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null) return Results.Unauthorized();

            var authResult = await auth.AuthorizeAsync(userId, projectId, "Viewer", allowArchived: true);
            if (!authResult.IsAuthorized)
            {
                return authResult.Status == ProjectAccessStatus.NotFound
                    ? Results.NotFound()
                    : Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status403Forbidden);
            }

            var characterExists = await db.Characters.AnyAsync(c => c.Id == characterId && c.ProjectId == projectId);
            if (!characterExists) return Results.NotFound();

            var concepts = await db.CharacterConcepts
                .AsNoTracking()
                .Where(cc => cc.ProjectId == projectId && cc.CharacterId == characterId)
                .Include(cc => cc.Character)
                .Include(cc => cc.Variant)
                .Include(cc => cc.Versions)
                .Include(cc => cc.RepresentativeVersion)
                    .ThenInclude(rv => rv!.ImageAsset)
                .OrderByDescending(cc => cc.CreatedAt)
                .ToListAsync();

            var dtos = concepts.Select(c => MapToConceptDto(c, projectId)).ToList();
            return Results.Ok(dtos);
        });

        // 2. POST Create Concept (from completed generation job + selected image asset)
        conceptGroup.MapPost("/{projectId:guid}/characters/{characterId:guid}/concepts", async (
            Guid projectId,
            Guid characterId,
            CreateConceptRequest req,
            ClaimsPrincipal principal,
            IProjectAuthorizationService auth,
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

            if (string.IsNullOrWhiteSpace(req.Title))
            {
                return Results.Problem("Tiêu đề concept không được để trống.", statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            var character = await db.Characters
                .Include(c => c.Variants)
                .FirstOrDefaultAsync(c => c.Id == characterId && c.ProjectId == projectId);
            if (character is null) return Results.NotFound();

            if (req.VariantId.HasValue && !character.Variants.Any(v => v.Id == req.VariantId.Value))
            {
                return Results.Problem("Biến thể không thuộc về nhân vật này.", statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            // Verify generation job belongs to this project and character
            var job = await db.GenerationJobs
                .Include(j => j.OutputAssets)
                .FirstOrDefaultAsync(j => j.Id == req.GenerationJobId && j.ProjectId == projectId);

            if (job is null)
            {
                return Results.Problem("Tác vụ sinh ảnh không tồn tại trong dự án này.", statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            if (job.CharacterId != characterId)
            {
                return Results.Problem("Tác vụ sinh ảnh không thuộc về nhân vật này.", statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            if (job.Status != CharacterConceptAI.Shared.Contracts.Generations.GenerationStatus.Completed)
                return Results.Problem("Chỉ có thể lưu concept từ tác vụ đã hoàn tất.", statusCode: 409);
            if (req.VariantId.HasValue && req.VariantId != job.VariantId)
                return Results.Problem("Biến thể concept phải khớp với biến thể của tác vụ sinh ảnh.", statusCode: 422);

            // Verify ImageAsset belongs to this job and project
            var asset = job.OutputAssets.FirstOrDefault(a => a.Id == req.ImageAssetId && a.ProjectId == projectId);
            if (asset is null)
            {
                return Results.Problem("Tài sản ảnh không thuộc về tác vụ sinh ảnh này hoặc sai dự án.", statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            var conceptId = Guid.NewGuid();
            var version1Id = Guid.NewGuid();

            var parametersSnapshot = JsonSerializer.Serialize(new
            {
                job.Width,
                job.Height,
                job.Seed,
                job.PromptMode,
                job.PromptTemplateVersion,
                job.WorkflowKey,
                job.WorkflowVersion,
                job.ComfyPromptId,
                job.IsSimulated
            }, JsonOpts);

            var version1 = new ConceptVersion
            {
                Id = version1Id,
                ConceptId = conceptId,
                VersionNumber = 1,
                ParentVersionId = null,
                GenerationJobId = job.Id,
                ImageAssetId = asset.Id,
                FinalPromptSnapshot = job.FinalPrompt,
                ParametersSnapshotJson = parametersSnapshot,
                Status = ConceptVersionStatus.Draft,
                CreatedByUserId = userId,
                CreatedAt = DateTimeOffset.UtcNow
            };

            var concept = new CharacterConcept
            {
                Id = conceptId,
                ProjectId = projectId,
                CharacterId = characterId,
                VariantId = req.VariantId ?? job.VariantId,
                Title = req.Title.Trim(),
                Description = req.Description?.Trim(),
                RepresentativeVersionId = null,
                Status = ConceptStatus.Draft,
                CreatedByUserId = userId,
                CreatedAt = DateTimeOffset.UtcNow
            };

            concept.Versions.Add(version1);

            db.CharacterConcepts.Add(concept);
            await db.SaveChangesAsync();

            // Safely point to version1 after it exists in database
            concept.RepresentativeVersionId = version1.Id;
            await db.SaveChangesAsync();

            // Reload with relations to map full DTO
            var created = await db.CharacterConcepts
                .Include(cc => cc.Character)
                .Include(cc => cc.Variant)
                .Include(cc => cc.Versions)
                .Include(cc => cc.RepresentativeVersion)
                    .ThenInclude(rv => rv!.ImageAsset)
                .FirstAsync(cc => cc.Id == conceptId);

            return Results.Created($"/api/v1/projects/{projectId}/concepts/{concept.Id}", MapToConceptDto(created, projectId));
        });

        // 3. GET Concept Detail (with version history & approvals)
        conceptGroup.MapGet("/{projectId:guid}/concepts/{conceptId:guid}", async (
            Guid projectId,
            Guid conceptId,
            ClaimsPrincipal principal,
            IProjectAuthorizationService auth,
            AppDbContext db) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null) return Results.Unauthorized();

            var authResult = await auth.AuthorizeAsync(userId, projectId, "Viewer", allowArchived: true);
            if (!authResult.IsAuthorized)
            {
                return authResult.Status == ProjectAccessStatus.NotFound
                    ? Results.NotFound()
                    : Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status403Forbidden);
            }

            var concept = await db.CharacterConcepts
                .AsNoTrackingWithIdentityResolution()
                .Where(cc => cc.Id == conceptId && cc.ProjectId == projectId)
                .Include(cc => cc.Character)
                .Include(cc => cc.Variant)
                .Include(cc => cc.RepresentativeVersion)
                    .ThenInclude(rv => rv!.ImageAsset)
                .Include(cc => cc.Versions)
                    .ThenInclude(v => v.ImageAsset)
                .Include(cc => cc.Versions)
                    .ThenInclude(v => v.Approvals)
                        .ThenInclude(a => a.ReviewerUser)
                .FirstOrDefaultAsync();

            if (concept is null) return Results.NotFound();

            var versionDtos = concept.Versions
                .OrderBy(v => v.VersionNumber)
                .Select(v => MapToVersionDto(v, projectId))
                .ToList();

            var conceptDto = MapToConceptDto(concept, projectId);

            return Results.Ok(new ConceptDetailDto(conceptDto, versionDtos));
        });

        // 4. POST Create New Version (immutable linear extension)
        conceptGroup.MapPost("/{projectId:guid}/concepts/{conceptId:guid}/versions", async (
            Guid projectId,
            Guid conceptId,
            CreateConceptVersionRequest req,
            ClaimsPrincipal principal,
            IProjectAuthorizationService auth,
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

            var concept = await db.CharacterConcepts
                .Include(cc => cc.Versions)
                .FirstOrDefaultAsync(cc => cc.Id == conceptId && cc.ProjectId == projectId);

            if (concept is null) return Results.NotFound();

            // Verify generation job belongs to this project and character
            var job = await db.GenerationJobs
                .Include(j => j.OutputAssets)
                .FirstOrDefaultAsync(j => j.Id == req.GenerationJobId && j.ProjectId == projectId);

            if (job is null)
            {
                return Results.Problem("Tác vụ sinh ảnh không tồn tại trong dự án này.", statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            if (job.CharacterId != concept.CharacterId)
            {
                return Results.Problem("Tác vụ sinh ảnh không thuộc về nhân vật của concept này.", statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            if (job.Status != CharacterConceptAI.Shared.Contracts.Generations.GenerationStatus.Completed)
                return Results.Problem("Chỉ có thể tạo phiên bản từ tác vụ đã hoàn tất.", statusCode: 409);
            if (job.VariantId != concept.VariantId)
                return Results.Problem("Biến thể của tác vụ không khớp với concept.", statusCode: 422);

            var asset = job.OutputAssets.FirstOrDefault(a => a.Id == req.ImageAssetId && a.ProjectId == projectId);
            if (asset is null)
            {
                return Results.Problem("Tài sản ảnh không thuộc về tác vụ sinh ảnh này.", statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            if (req.ParentVersionId.HasValue && !concept.Versions.Any(v => v.Id == req.ParentVersionId.Value))
            {
                return Results.Problem("Phiên bản cha không thuộc về concept này.", statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            var nextVersionNumber = concept.Versions.Count > 0
                ? concept.Versions.Max(v => v.VersionNumber) + 1
                : 1;

            var parametersSnapshot = JsonSerializer.Serialize(new
            {
                job.Width,
                job.Height,
                job.Seed,
                job.PromptMode,
                job.PromptTemplateVersion,
                job.WorkflowKey,
                job.WorkflowVersion,
                job.ComfyPromptId,
                job.IsSimulated,
                Note = req.Note
            }, JsonOpts);

            var newVersion = new ConceptVersion
            {
                Id = Guid.NewGuid(),
                ConceptId = concept.Id,
                VersionNumber = nextVersionNumber,
                ParentVersionId = req.ParentVersionId ?? concept.Versions.OrderBy(v => v.VersionNumber).LastOrDefault()?.Id,
                GenerationJobId = job.Id,
                ImageAssetId = asset.Id,
                FinalPromptSnapshot = job.FinalPrompt,
                ParametersSnapshotJson = parametersSnapshot,
                Status = ConceptVersionStatus.Draft,
                CreatedByUserId = userId,
                CreatedAt = DateTimeOffset.UtcNow
            };

            concept.UpdatedAt = DateTimeOffset.UtcNow;
            db.ConceptVersions.Add(newVersion);
            await db.SaveChangesAsync();

            return Results.Created($"/api/v1/projects/{projectId}/concepts/{concept.Id}/versions/{newVersion.Id}", MapToVersionDto(newVersion, projectId));
        });

        // 5. POST Submit Version for Review
        conceptGroup.MapPost("/{projectId:guid}/concepts/{conceptId:guid}/versions/{versionId:guid}/submit", async (
            Guid projectId,
            Guid conceptId,
            Guid versionId,
            ClaimsPrincipal principal,
            IProjectAuthorizationService auth,
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

            var concept = await db.CharacterConcepts
                .Include(cc => cc.Versions)
                .FirstOrDefaultAsync(cc => cc.Id == conceptId && cc.ProjectId == projectId);

            if (concept is null) return Results.NotFound();

            var version = concept.Versions.FirstOrDefault(v => v.Id == versionId);
            if (version is null) return Results.NotFound();

            if (version.Status == ConceptVersionStatus.Approved)
                return Results.Problem("Phiên bản đã duyệt là bất biến. Hãy tạo phiên bản mới.", statusCode: 409);

            version.Status = ConceptVersionStatus.Submitted;
            concept.Status = ConceptStatus.UnderReview;
            concept.UpdatedAt = DateTimeOffset.UtcNow;

            await db.SaveChangesAsync();

            return Results.Ok(new
            {
                versionId = version.Id,
                versionStatus = version.Status.ToString(),
                conceptStatus = concept.Status.ToString(),
                message = $"Phiên bản v{version.VersionNumber} đã được gửi phê duyệt."
            });
        });

        // 6. POST Review Version (Owner-Only Decision with Recorded Reason and Audit)
        conceptGroup.MapPost("/{projectId:guid}/concepts/{conceptId:guid}/versions/{versionId:guid}/review", async (
            Guid projectId,
            Guid conceptId,
            Guid versionId,
            ReviewConceptVersionRequest req,
            ClaimsPrincipal principal,
            IProjectAuthorizationService auth,
            AppDbContext db) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null) return Results.Unauthorized();

            // STRICT: Owner only! Designer or Viewer must be denied with 403 Forbidden (API-005)
            var authResult = await auth.AuthorizeAsync(userId, projectId, "Owner", allowArchived: false);
            if (!authResult.IsAuthorized)
            {
                return authResult.Status switch
                {
                    ProjectAccessStatus.NotFound => Results.NotFound(),
                    ProjectAccessStatus.Archived => Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status422UnprocessableEntity),
                    _ => Results.Problem("Chỉ có Owner của dự án mới có quyền phê duyệt concept art.", statusCode: StatusCodes.Status403Forbidden)
                };
            }

            var concept = await db.CharacterConcepts
                .Include(cc => cc.Versions)
                .FirstOrDefaultAsync(cc => cc.Id == conceptId && cc.ProjectId == projectId);

            if (concept is null) return Results.NotFound();

            var version = concept.Versions.FirstOrDefault(v => v.Id == versionId);
            if (version is null) return Results.NotFound();

            // Immutable history protection: once Approved, cannot alter or undo approved bytes (UNIT-012)
            if (version.Status == ConceptVersionStatus.Approved)
            {
                return Results.Problem("Phiên bản này đã được phê duyệt trước đó và là bất biến.", statusCode: StatusCodes.Status409Conflict);
            }

            if (!Enum.IsDefined(req.Decision))
                return Results.Problem("Quyết định duyệt không hợp lệ.", statusCode: 422);

            var approval = new ConceptApproval
            {
                Id = Guid.NewGuid(),
                ConceptVersionId = version.Id,
                ReviewerUserId = userId,
                Decision = req.Decision,
                Comment = req.Comment?.Trim(),
                ReviewedAt = DateTimeOffset.UtcNow
            };

            db.ConceptApprovals.Add(approval);

            if (req.Decision == ConceptApprovalDecision.Approved)
            {
                version.Status = ConceptVersionStatus.Approved;
                concept.Status = ConceptStatus.Approved;
                // Automatically set as representative if none chosen or previous was not approved
                if (concept.RepresentativeVersionId == null)
                {
                    concept.RepresentativeVersionId = version.Id;
                }
            }
            else
            {
                version.Status = ConceptVersionStatus.Rejected;
                concept.Status = ConceptStatus.Rejected;
            }

            concept.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();

            return Results.Ok(new
            {
                approvalId = approval.Id,
                decision = approval.Decision.ToString(),
                versionStatus = version.Status.ToString(),
                conceptStatus = concept.Status.ToString(),
                reviewerUserId = userId,
                reviewedAt = approval.ReviewedAt
            });
        });

        // 7. PATCH Set Representative Version
        conceptGroup.MapPatch("/{projectId:guid}/concepts/{conceptId:guid}/representative", async (
            Guid projectId,
            Guid conceptId,
            SetRepresentativeVersionRequest req,
            ClaimsPrincipal principal,
            IProjectAuthorizationService auth,
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

            var concept = await db.CharacterConcepts
                .Include(cc => cc.Versions)
                .FirstOrDefaultAsync(cc => cc.Id == conceptId && cc.ProjectId == projectId);

            if (concept is null) return Results.NotFound();

            var version = concept.Versions.FirstOrDefault(v => v.Id == req.VersionId);
            if (version is null)
            {
                return Results.Problem("Phiên bản được chỉ định không thuộc về concept này.", statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            concept.RepresentativeVersionId = version.Id;
            concept.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();

            return Results.Ok(new
            {
                conceptId = concept.Id,
                representativeVersionId = version.Id,
                versionNumber = version.VersionNumber
            });
        });

        // 8. GET Compare Two Versions Side-by-Side
        conceptGroup.MapGet("/{projectId:guid}/concepts/{conceptId:guid}/versions/compare", async (
            Guid projectId,
            Guid conceptId,
            Guid left,
            Guid right,
            ClaimsPrincipal principal,
            IProjectAuthorizationService auth,
            AppDbContext db) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null) return Results.Unauthorized();

            var authResult = await auth.AuthorizeAsync(userId, projectId, "Viewer", allowArchived: true);
            if (!authResult.IsAuthorized)
            {
                return authResult.Status == ProjectAccessStatus.NotFound
                    ? Results.NotFound()
                    : Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status403Forbidden);
            }

            var concept = await db.CharacterConcepts
                .AsNoTracking()
                .Where(cc => cc.Id == conceptId && cc.ProjectId == projectId)
                .Select(cc => new { cc.Id, cc.Title })
                .FirstOrDefaultAsync();

            if (concept is null) return Results.NotFound();

            // Fetch ONLY the 2 requested versions in SQL (instead of loading all historical versions!)
            var targetIds = new[] { left, right };
            var versions = await db.ConceptVersions
                .AsNoTrackingWithIdentityResolution()
                .AsSplitQuery()
                .Where(v => v.ConceptId == conceptId && targetIds.Contains(v.Id))
                .Include(v => v.ImageAsset)
                .Include(v => v.Approvals)
                    .ThenInclude(a => a.ReviewerUser)
                .ToListAsync();

            var leftVer = versions.FirstOrDefault(v => v.Id == left);
            var rightVer = versions.FirstOrDefault(v => v.Id == right);

            if (leftVer is null || rightVer is null)
            {
                return Results.Problem("Một hoặc cả hai phiên bản so sánh không tồn tại trong concept này.", statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            var leftItem = MapToComparisonItem(leftVer, projectId);
            var rightItem = MapToComparisonItem(rightVer, projectId);

            return Results.Ok(new ConceptComparisonDto(
                ConceptId: concept.Id,
                ConceptTitle: concept.Title,
                Left: leftItem,
                Right: rightItem
            ));
        });

        // 9. GET Thumbnail Stream
        conceptGroup.MapGet("/{projectId:guid}/media/{assetId:guid}/thumbnail", async (
            Guid projectId,
            Guid assetId,
            ClaimsPrincipal principal,
            IProjectAuthorizationService auth,
            AppDbContext db,
            Storage.IMediaStore mediaStore,
            HttpContext context) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null) return Results.Unauthorized();

            var authResult = await auth.AuthorizeAsync(userId, projectId, "Viewer", allowArchived: true);
            if (!authResult.IsAuthorized)
            {
                return authResult.Status == ProjectAccessStatus.NotFound
                    ? Results.NotFound()
                    : Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status403Forbidden);
            }

            var asset = await db.ImageAssets
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == assetId && a.ProjectId == projectId);
            if (asset is null) return Results.NotFound();

            var etag = !string.IsNullOrWhiteSpace(asset.Sha256)
                ? $"\"{asset.Sha256}\""
                : $"\"{asset.Id:N}-{asset.SizeBytes}\"";

            context.Response.Headers.CacheControl = "private, no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers.ETag = etag;

            if (context.Request.Headers.IfNoneMatch.Any(v => string.Equals(v?.Trim(), etag, StringComparison.Ordinal)))
            {
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }

            string fullPath;
            try { fullPath = mediaStore.GetFullPath(asset.StorageKey); }
            catch (ArgumentException) { return Results.NotFound(); }
            if (!File.Exists(fullPath)) return Results.NotFound();

            return Results.File(fullPath, asset.ContentType, enableRangeProcessing: true);
        });

        return group;
    }

    private static CharacterConceptDto MapToConceptDto(CharacterConcept c, Guid projectId)
    {
        var repAsset = c.RepresentativeVersion?.ImageAsset;
        var repUrl = repAsset != null ? $"/api/v1/projects/{projectId}/media/{repAsset.Id}" : null;

        return new CharacterConceptDto(
            c.Id,
            c.ProjectId,
            c.CharacterId,
            c.Character?.Name ?? string.Empty,
            c.VariantId,
            c.Variant?.Name,
            c.Title,
            c.Description,
            c.Status,
            c.RepresentativeVersionId,
            repAsset?.Id,
            repUrl,
            c.Versions.Count,
            c.CreatedAt,
            c.UpdatedAt,
            c.RowVersion != null ? Convert.ToBase64String(c.RowVersion) : null
        );
    }

    private static ConceptVersionDto MapToVersionDto(ConceptVersion v, Guid projectId)
    {
        var streamUrl = $"/api/v1/projects/{projectId}/media/{v.ImageAssetId}";
        var approvals = v.Approvals.Select(a => new ConceptApprovalDto(
            a.Id,
            a.ConceptVersionId,
            a.ReviewerUserId,
            a.ReviewerUser?.DisplayName ?? a.ReviewerUserId,
            a.Decision,
            a.Comment,
            a.ReviewedAt
        )).ToList();

        return new ConceptVersionDto(
            v.Id,
            v.ConceptId,
            v.VersionNumber,
            v.ParentVersionId,
            v.GenerationJobId,
            v.ImageAssetId,
            streamUrl,
            v.FinalPromptSnapshot,
            v.ParametersSnapshotJson,
            v.Status,
            v.CreatedByUserId,
            v.CreatedAt,
            approvals
        );
    }

    private static ConceptVersionComparisonItemDto MapToComparisonItem(ConceptVersion v, Guid projectId)
    {
        var streamUrl = $"/api/v1/projects/{projectId}/media/{v.ImageAssetId}";
        var approvals = v.Approvals.Select(a => new ConceptApprovalDto(
            a.Id,
            a.ConceptVersionId,
            a.ReviewerUserId,
            a.ReviewerUser?.DisplayName ?? a.ReviewerUserId,
            a.Decision,
            a.Comment,
            a.ReviewedAt
        )).ToList();

        return new ConceptVersionComparisonItemDto(
            VersionId: v.Id,
            VersionNumber: v.VersionNumber,
            ImageAssetId: v.ImageAssetId,
            StreamUrl: streamUrl,
            Width: v.ImageAsset?.Width ?? 512,
            Height: v.ImageAsset?.Height ?? 512,
            SizeBytes: v.ImageAsset?.SizeBytes ?? 0,
            Sha256: v.ImageAsset?.Sha256 ?? string.Empty,
            FinalPrompt: v.FinalPromptSnapshot,
            ParametersJson: v.ParametersSnapshotJson,
            Status: v.Status,
            CreatedAt: v.CreatedAt,
            Approvals: approvals
        );
    }
}

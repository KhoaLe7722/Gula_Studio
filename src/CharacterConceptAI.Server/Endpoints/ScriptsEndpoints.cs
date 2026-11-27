using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using CharacterConceptAI.Server.Authorization;
using CharacterConceptAI.Server.Data;
using CharacterConceptAI.Server.Data.Entities;
using CharacterConceptAI.Shared.Contracts.Scripts;

namespace CharacterConceptAI.Server.Endpoints;

public static class ScriptsEndpoints
{
    public static RouteGroupBuilder MapScriptsEndpoints(this RouteGroupBuilder group)
    {
        var scriptsGroup = group.RequireAuthorization();

        // 1. List scripts in project
        scriptsGroup.MapGet("/{projectId:guid}/scripts", async (
            Guid projectId,
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

            var scripts = await db.Scripts
                .AsNoTracking()
                .Where(s => s.ProjectId == projectId)
                .OrderByDescending(s => s.CreatedAt)
                .Select(s => new ScriptDto(
                    s.Id,
                    s.ProjectId,
                    s.Title,
                    s.Summary,
                    s.Status,
                    s.CreatedAt,
                    s.UpdatedAt,
                    s.RowVersion != null ? Convert.ToBase64String(s.RowVersion) : null,
                    s.Scenes.Count
                ))
                .ToListAsync();

            return Results.Ok(scripts);
        });

        // 2. Create script
        scriptsGroup.MapPost("/{projectId:guid}/scripts", async (
            Guid projectId,
            CreateScriptRequest req,
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
                return Results.BadRequest(new { error = "Tiêu đề kịch bản không được để trống." });
            }

            var script = new Script
            {
                ProjectId = projectId,
                Title = req.Title.Trim(),
                Summary = req.Summary?.Trim(),
                Status = "Active",
                CreatedAt = DateTimeOffset.UtcNow
            };

            db.Scripts.Add(script);
            await db.SaveChangesAsync();

            return Results.Created($"/api/v1/projects/{projectId}/scripts/{script.Id}", new ScriptDto(
                script.Id,
                script.ProjectId,
                script.Title,
                script.Summary,
                script.Status,
                script.CreatedAt,
                script.UpdatedAt,
                script.RowVersion != null ? Convert.ToBase64String(script.RowVersion) : null,
                0
            ));
        });

        // 3. Get script detail
        scriptsGroup.MapGet("/{projectId:guid}/scripts/{scriptId:guid}", async (
            Guid projectId,
            Guid scriptId,
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

            var script = await db.Scripts
                .AsNoTracking()
                .Where(s => s.Id == scriptId && s.ProjectId == projectId)
                .Select(s => new ScriptDto(
                    s.Id,
                    s.ProjectId,
                    s.Title,
                    s.Summary,
                    s.Status,
                    s.CreatedAt,
                    s.UpdatedAt,
                    s.RowVersion != null ? Convert.ToBase64String(s.RowVersion) : null,
                    s.Scenes.Count
                ))
                .FirstOrDefaultAsync();

            if (script is null) return Results.NotFound();

            return Results.Ok(script);
        });

        // 4. Update script (with rowversion concurrency check)
        scriptsGroup.MapPut("/{projectId:guid}/scripts/{scriptId:guid}", async (
            Guid projectId,
            Guid scriptId,
            UpdateScriptRequest req,
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
                return Results.BadRequest(new { error = "Tiêu đề kịch bản không được để trống." });
            }

            var script = await db.Scripts.FirstOrDefaultAsync(s => s.Id == scriptId && s.ProjectId == projectId);
            if (script is null) return Results.NotFound();

            if (string.Equals(script.Status, "Archived", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Problem("Kịch bản đã được lưu trữ (Archived), không thể chỉnh sửa.", statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            if (string.IsNullOrWhiteSpace(req.Version))
                return Results.BadRequest(new { error = "Cần phiên bản dữ liệu hiện tại để lưu thay đổi." });
            if (!string.IsNullOrWhiteSpace(req.Version))
            {
                try
                {
                    var originalRowVersion = Convert.FromBase64String(req.Version);
                    db.Entry(script).OriginalValues[nameof(Script.RowVersion)] = originalRowVersion;
                }
                catch (FormatException)
                {
                    return Results.BadRequest(new { error = "Mã RowVersion không hợp lệ." });
                }
            }

            script.Title = req.Title.Trim();
            script.Summary = req.Summary?.Trim();
            script.UpdatedAt = DateTimeOffset.UtcNow;

            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Problem(
                    detail: "Kịch bản đã bị thay đổi bởi người dùng khác trong khi bạn đang chỉnh sửa.",
                    title: "CONCURRENT_MODIFICATION",
                    statusCode: StatusCodes.Status409Conflict
                );
            }

            var sceneCount = await db.Scenes.CountAsync(sc => sc.ScriptId == scriptId);

            return Results.Ok(new ScriptDto(
                script.Id,
                script.ProjectId,
                script.Title,
                script.Summary,
                script.Status,
                script.CreatedAt,
                script.UpdatedAt,
                script.RowVersion != null ? Convert.ToBase64String(script.RowVersion) : null,
                sceneCount
            ));
        });

        // 5. Archive script
        scriptsGroup.MapPost("/{projectId:guid}/scripts/{scriptId:guid}/archive", async (
            Guid projectId,
            Guid scriptId,
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

            var script = await db.Scripts.FirstOrDefaultAsync(s => s.Id == scriptId && s.ProjectId == projectId);
            if (script is null) return Results.NotFound();

            script.Status = "Archived";
            script.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();

            return Results.Ok(new { message = "Kịch bản đã được lưu trữ (Archived)." });
        });

        // 6. Restore script
        scriptsGroup.MapPost("/{projectId:guid}/scripts/{scriptId:guid}/restore", async (
            Guid projectId,
            Guid scriptId,
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

            var script = await db.Scripts.FirstOrDefaultAsync(s => s.Id == scriptId && s.ProjectId == projectId);
            if (script is null) return Results.NotFound();

            script.Status = "Active";
            script.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();

            return Results.Ok(new { message = "Kịch bản đã được kích hoạt lại (Active)." });
        });

        // 7. List scenes in script (ordered)
        scriptsGroup.MapGet("/{projectId:guid}/scripts/{scriptId:guid}/scenes", async (
            Guid projectId,
            Guid scriptId,
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

            var scriptExists = await db.Scripts.AnyAsync(s => s.Id == scriptId && s.ProjectId == projectId);
            if (!scriptExists) return Results.NotFound();

            var scenes = await db.Scenes
                .AsNoTracking()
                .Where(sc => sc.ScriptId == scriptId)
                .OrderBy(sc => sc.OrderIndex)
                .Include(sc => sc.SceneCharacters)
                .Select(sc => new SceneDto(
                    sc.Id,
                    sc.ScriptId,
                    sc.OrderIndex,
                    sc.Title,
                    sc.Setting,
                    sc.Content,
                    sc.SceneCharacters.Select(c => c.CharacterId).ToList(),
                    sc.CreatedAt,
                    sc.UpdatedAt,
                    sc.RowVersion != null ? Convert.ToBase64String(sc.RowVersion) : null
                ))
                .ToListAsync();

            return Results.Ok(scenes);
        });

        // 8. Create scene (with cross-project character link guard)
        scriptsGroup.MapPost("/{projectId:guid}/scripts/{scriptId:guid}/scenes", async (
            Guid projectId,
            Guid scriptId,
            CreateSceneRequest req,
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

            var script = await db.Scripts.FirstOrDefaultAsync(s => s.Id == scriptId && s.ProjectId == projectId);
            if (script is null) return Results.NotFound();

            if (string.Equals(script.Status, "Archived", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Problem("Kịch bản đã được lưu trữ (Archived), không thể thêm phân cảnh mới.", statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            if (string.IsNullOrWhiteSpace(req.Title))
            {
                return Results.BadRequest(new { error = "Tiêu đề phân cảnh không được để trống." });
            }

            // CRITICAL INVARIANT: Verify all linked characters belong to the same project
            var characterIds = req.CharacterIds?.Distinct().ToList() ?? [];
            if (characterIds.Count > 0)
            {
                var validCount = await db.Characters
                    .CountAsync(c => characterIds.Contains(c.Id) && c.ProjectId == projectId);

                if (validCount != characterIds.Count)
                {
                    return Results.Problem(
                        detail: "Một hoặc nhiều nhân vật được liên kết không tồn tại hoặc không thuộc cùng dự án này.",
                        title: "CROSS_PROJECT_RELATIONSHIP_DENIED",
                        statusCode: StatusCodes.Status422UnprocessableEntity
                    );
                }
            }

            // Calculate OrderIndex if not specified
            int targetOrder;
            if (req.OrderIndex.HasValue && req.OrderIndex.Value > 0)
            {
                targetOrder = req.OrderIndex.Value;
            }
            else
            {
                var maxOrder = await db.Scenes
                    .Where(sc => sc.ScriptId == scriptId)
                    .Select(sc => (int?)sc.OrderIndex)
                    .MaxAsync();
                targetOrder = (maxOrder ?? 0) + 1;
            }

            var scene = new Scene
            {
                ScriptId = scriptId,
                Title = req.Title.Trim(),
                Setting = req.Setting?.Trim(),
                Content = req.Content?.Trim(),
                OrderIndex = targetOrder,
                CreatedAt = DateTimeOffset.UtcNow
            };

            foreach (var charId in characterIds)
            {
                scene.SceneCharacters.Add(new SceneCharacter
                {
                    SceneId = scene.Id,
                    CharacterId = charId
                });
            }

            db.Scenes.Add(scene);
            await db.SaveChangesAsync();

            return Results.Created($"/api/v1/projects/{projectId}/scripts/{scriptId}/scenes/{scene.Id}", new SceneDto(
                scene.Id,
                scene.ScriptId,
                scene.OrderIndex,
                scene.Title,
                scene.Setting,
                scene.Content,
                characterIds,
                scene.CreatedAt,
                scene.UpdatedAt,
                scene.RowVersion != null ? Convert.ToBase64String(scene.RowVersion) : null
            ));
        });

        // 9. Get scene detail
        scriptsGroup.MapGet("/{projectId:guid}/scripts/{scriptId:guid}/scenes/{sceneId:guid}", async (
            Guid projectId,
            Guid scriptId,
            Guid sceneId,
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

            var scene = await db.Scenes
                .AsNoTracking()
                .Include(sc => sc.Script)
                .Include(sc => sc.SceneCharacters)
                .FirstOrDefaultAsync(sc => sc.Id == sceneId && sc.ScriptId == scriptId && sc.Script.ProjectId == projectId);

            if (scene is null) return Results.NotFound();

            return Results.Ok(new SceneDto(
                scene.Id,
                scene.ScriptId,
                scene.OrderIndex,
                scene.Title,
                scene.Setting,
                scene.Content,
                scene.SceneCharacters.Select(c => c.CharacterId).ToList(),
                scene.CreatedAt,
                scene.UpdatedAt,
                scene.RowVersion != null ? Convert.ToBase64String(scene.RowVersion) : null
            ));
        });

        // 10. Update scene (with rowversion concurrency check)
        scriptsGroup.MapPut("/{projectId:guid}/scripts/{scriptId:guid}/scenes/{sceneId:guid}", async (
            Guid projectId,
            Guid scriptId,
            Guid sceneId,
            UpdateSceneRequest req,
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

            var scene = await db.Scenes
                .Include(sc => sc.Script)
                .Include(sc => sc.SceneCharacters)
                .FirstOrDefaultAsync(sc => sc.Id == sceneId && sc.ScriptId == scriptId && sc.Script.ProjectId == projectId);

            if (scene is null) return Results.NotFound();

            if (string.Equals(scene.Script.Status, "Archived", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Problem("Kịch bản đã được lưu trữ (Archived), không thể chỉnh sửa phân cảnh.", statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            if (string.IsNullOrWhiteSpace(req.Title))
            {
                return Results.BadRequest(new { error = "Tiêu đề phân cảnh không được để trống." });
            }

            if (string.IsNullOrWhiteSpace(req.Version))
                return Results.BadRequest(new { error = "Cần phiên bản dữ liệu hiện tại để lưu thay đổi." });
            if (!string.IsNullOrWhiteSpace(req.Version))
            {
                try
                {
                    var originalRowVersion = Convert.FromBase64String(req.Version);
                    db.Entry(scene).OriginalValues[nameof(Scene.RowVersion)] = originalRowVersion;
                }
                catch (FormatException)
                {
                    return Results.BadRequest(new { error = "Mã RowVersion không hợp lệ." });
                }
            }

            scene.Title = req.Title.Trim();
            scene.Setting = req.Setting?.Trim();
            scene.Content = req.Content?.Trim();
            scene.UpdatedAt = DateTimeOffset.UtcNow;

            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Problem(
                    detail: "Phân cảnh đã bị thay đổi bởi người dùng khác trong khi bạn đang chỉnh sửa.",
                    title: "CONCURRENT_MODIFICATION",
                    statusCode: StatusCodes.Status409Conflict
                );
            }

            return Results.Ok(new SceneDto(
                scene.Id,
                scene.ScriptId,
                scene.OrderIndex,
                scene.Title,
                scene.Setting,
                scene.Content,
                scene.SceneCharacters.Select(c => c.CharacterId).ToList(),
                scene.CreatedAt,
                scene.UpdatedAt,
                scene.RowVersion != null ? Convert.ToBase64String(scene.RowVersion) : null
            ));
        });

        // 11. Delete scene
        scriptsGroup.MapDelete("/{projectId:guid}/scripts/{scriptId:guid}/scenes/{sceneId:guid}", async (
            Guid projectId,
            Guid scriptId,
            Guid sceneId,
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

            var scene = await db.Scenes
                .Include(sc => sc.Script)
                .FirstOrDefaultAsync(sc => sc.Id == sceneId && sc.ScriptId == scriptId && sc.Script.ProjectId == projectId);

            if (scene is null) return Results.NotFound();

            if (string.Equals(scene.Script.Status, "Archived", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Problem("Kịch bản đã được lưu trữ (Archived), không thể xóa phân cảnh.", statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            db.Scenes.Remove(scene);
            await db.SaveChangesAsync();

            return Results.NoContent();
        });

        // 12. Reorder scenes (Atomic transaction)
        scriptsGroup.MapPost("/{projectId:guid}/scripts/{scriptId:guid}/scenes/reorder", async (
            Guid projectId,
            Guid scriptId,
            ReorderScenesRequest req,
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

            var script = await db.Scripts.FirstOrDefaultAsync(s => s.Id == scriptId && s.ProjectId == projectId);
            if (script is null) return Results.NotFound();

            if (string.Equals(script.Status, "Archived", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Problem("Kịch bản đã được lưu trữ (Archived), không thể sắp xếp lại phân cảnh.", statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            if (req.Items == null || req.Items.Count == 0)
            {
                return Results.BadRequest(new { error = "Danh sách sắp xếp không được để trống." });
            }

            var sceneIds = req.Items.Select(i => i.SceneId).ToList();
            var scenes = await db.Scenes
                .Where(sc => sc.ScriptId == scriptId && sceneIds.Contains(sc.Id))
                .ToListAsync();

            if (scenes.Count != sceneIds.Count)
            {
                return Results.BadRequest(new { error = "Một hoặc nhiều phân cảnh không thuộc kịch bản này." });
            }

            // Single SaveChangesAsync already runs inside an atomic SQL transaction and stays compatible with EnableRetryOnFailure
            foreach (var item in req.Items)
            {
                var sc = scenes.First(s => s.Id == item.SceneId);
                sc.OrderIndex = item.OrderIndex;
                sc.UpdatedAt = DateTimeOffset.UtcNow;
            }

            await db.SaveChangesAsync();
            return Results.Ok(new { message = "Thứ tự phân cảnh đã được cập nhật thành công." });
        });

        // 13. Update linked characters for a scene (Cross-project enforcement)
        scriptsGroup.MapPut("/{projectId:guid}/scripts/{scriptId:guid}/scenes/{sceneId:guid}/characters", async (
            Guid projectId,
            Guid scriptId,
            Guid sceneId,
            UpdateSceneCharactersRequest req,
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

            var scene = await db.Scenes
                .Include(sc => sc.Script)
                .Include(sc => sc.SceneCharacters)
                .FirstOrDefaultAsync(sc => sc.Id == sceneId && sc.ScriptId == scriptId && sc.Script.ProjectId == projectId);

            if (scene is null) return Results.NotFound();

            if (string.Equals(scene.Script.Status, "Archived", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Problem("Kịch bản đã được lưu trữ (Archived), không thể chỉnh sửa liên kết nhân vật.", statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            var characterIds = req.CharacterIds?.Distinct().ToList() ?? [];
            if (characterIds.Count > 0)
            {
                var validCount = await db.Characters
                    .CountAsync(c => characterIds.Contains(c.Id) && c.ProjectId == projectId);

                if (validCount != characterIds.Count)
                {
                    return Results.Problem(
                        detail: "Một hoặc nhiều nhân vật được chọn không thuộc cùng dự án này.",
                        title: "CROSS_PROJECT_RELATIONSHIP_DENIED",
                        statusCode: StatusCodes.Status422UnprocessableEntity
                    );
                }
            }

            // Replace scene characters
            db.SceneCharacters.RemoveRange(scene.SceneCharacters);

            foreach (var charId in characterIds)
            {
                db.SceneCharacters.Add(new SceneCharacter
                {
                    SceneId = sceneId,
                    CharacterId = charId
                });
            }

            scene.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();

            return Results.Ok(new
            {
                sceneId,
                linkedCharacterIds = characterIds
            });
        });

        return group;
    }
}

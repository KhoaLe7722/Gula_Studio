using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using CharacterConceptAI.Server.Authorization;
using CharacterConceptAI.Server.Data;
using CharacterConceptAI.Server.Data.Entities;
using CharacterConceptAI.Shared.Contracts.Common;
using CharacterConceptAI.Shared.Contracts.Projects;

namespace CharacterConceptAI.Server.Endpoints;

public static class ProjectsEndpoints
{
    private static readonly HashSet<string> AllowedRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Owner",
        "Designer",
        "Viewer"
    };

    public static RouteGroupBuilder MapProjectsEndpoints(this RouteGroupBuilder group)
    {
        var projectGroup = group.RequireAuthorization();

        // 1. List projects of current user (with pagination and filtering)
        projectGroup.MapGet("", async (
            int? page,
            int? pageSize,
            string? query,
            string? status,
            string? sort,
            ClaimsPrincipal principal,
            AppDbContext db) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null) return Results.Unauthorized();

            var p = page.GetValueOrDefault(1);
            p = p < 1 ? 1 : p;
            var ps = pageSize.GetValueOrDefault(20);
            ps = ps < 1 || ps > 100 ? 20 : ps;

            IQueryable<Project> q = db.ProjectMembers
                    .AsNoTracking()
                    .Where(pm => pm.UserId == userId)
                    .Select(pm => pm.Project);

            if (!string.IsNullOrWhiteSpace(query))
            {
                q = q.Where(prj => prj.Name.Contains(query) || (prj.Description != null && prj.Description.Contains(query)));
            }

            if (!string.IsNullOrWhiteSpace(status))
            {
                q = q.Where(prj => prj.Status == status);
            }

            var order = sort?.Trim().ToLowerInvariant();
            if (order is not (null or "" or "name" or "updated"))
                return Results.BadRequest(new { error = "Chỉ hỗ trợ sắp xếp theo name hoặc updated." });
            var ordered = order switch
            {
                "name" => q.OrderBy(prj => prj.Name).ThenBy(prj => prj.Id),
                "updated" => q.OrderByDescending(prj => prj.UpdatedAt ?? prj.CreatedAt).ThenBy(prj => prj.Id),
                _ => q.OrderByDescending(prj => prj.CreatedAt).ThenBy(prj => prj.Id)
            };
            if ((long)(p - 1) * ps > int.MaxValue)
                return Results.BadRequest(new { error = "Trang yêu cầu vượt giới hạn hỗ trợ." });

            var total = await q.CountAsync();
            var projects = await ordered
                .Skip((p - 1) * ps)
                .Take(ps)
                .Select(prj => new ProjectDto(
                    prj.Id,
                    prj.Name,
                    prj.ProjectType,
                    prj.Description,
                    prj.Status,
                    prj.CreatedByUserId,
                    prj.CreatedAt,
                    prj.UpdatedAt,
                    prj.RowVersion != null ? Convert.ToBase64String(prj.RowVersion) : null
                ))
                .ToListAsync();

            return Results.Ok(new PagedResult<ProjectDto>
            {
                Items = projects,
                Page = p,
                PageSize = ps,
                TotalCount = total
            });
        });

        // 2. Create Project
        projectGroup.MapPost("", async (
            CreateProjectRequest req,
            ClaimsPrincipal principal,
            AppDbContext db) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null) return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(req.Name))
            {
                return Results.BadRequest(new { error = "Tên dự án không được để trống." });
            }

            var user = await db.Users.FindAsync(userId);
            if (user is null || !user.IsActive)
            {
                return Results.Problem("Tài khoản không hợp lệ hoặc đã bị vô hiệu hóa.", statusCode: StatusCodes.Status403Forbidden);
            }

            var project = new Project
            {
                Name = req.Name.Trim(),
                ProjectType = string.IsNullOrWhiteSpace(req.ProjectType) ? "Game" : req.ProjectType.Trim(),
                Description = req.Description?.Trim(),
                CreatedByUserId = userId,
                Status = "Active"
            };
            db.Projects.Add(project);

            // Creator becomes Owner atomically
            db.ProjectMembers.Add(new ProjectMember
            {
                ProjectId = project.Id,
                UserId = userId,
                Role = "Owner"
            });

            await db.SaveChangesAsync();

            return Results.Created($"/api/v1/projects/{project.Id}", new ProjectDto(
                project.Id,
                project.Name,
                project.ProjectType,
                project.Description,
                project.Status,
                project.CreatedByUserId,
                project.CreatedAt,
                project.UpdatedAt,
                project.RowVersion != null ? Convert.ToBase64String(project.RowVersion) : null
            ));
        });

        // 3. Get Project Detail
        projectGroup.MapGet("/{projectId:guid}", async (
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

            var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId);
            if (project is null) return Results.NotFound();

            return Results.Ok(new ProjectDto(
                project.Id,
                project.Name,
                project.ProjectType,
                project.Description,
                project.Status,
                project.CreatedByUserId,
                project.CreatedAt,
                project.UpdatedAt,
                project.RowVersion != null ? Convert.ToBase64String(project.RowVersion) : null
            ));
        });

        // 4. Update Project (with optimistic concurrency check)
        projectGroup.MapPut("/{projectId:guid}", async (
            Guid projectId,
            UpdateProjectRequest req,
            ClaimsPrincipal principal,
            IProjectAuthorizationService auth,
            AppDbContext db) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null) return Results.Unauthorized();

            var authResult = await auth.AuthorizeAsync(userId, projectId, "Owner", allowArchived: false);
            if (!authResult.IsAuthorized)
            {
                return authResult.Status switch
                {
                    ProjectAccessStatus.NotFound => Results.NotFound(),
                    ProjectAccessStatus.Archived => Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status422UnprocessableEntity),
                    _ => Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status403Forbidden)
                };
            }

            if (string.IsNullOrWhiteSpace(req.Name))
            {
                return Results.BadRequest(new { error = "Tên dự án không được để trống." });
            }

            var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId);
            if (project is null) return Results.NotFound();

            // Set original rowversion for concurrency check
            if (string.IsNullOrWhiteSpace(req.Version))
                return Results.BadRequest(new { error = "Cần phiên bản dữ liệu hiện tại để lưu thay đổi." });
            if (!string.IsNullOrWhiteSpace(req.Version))
            {
                try
                {
                    var originalRowVersion = Convert.FromBase64String(req.Version);
                    db.Entry(project).OriginalValues[nameof(Project.RowVersion)] = originalRowVersion;
                }
                catch (FormatException)
                {
                    return Results.BadRequest(new { error = "Phiên bản RowVersion không hợp lệ." });
                }
            }

            project.Name = req.Name.Trim();
            project.ProjectType = req.ProjectType.Trim();
            project.Description = req.Description?.Trim();
            project.UpdatedAt = DateTimeOffset.UtcNow;

            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Problem(
                    detail: "Dữ liệu dự án đã bị thay đổi bởi người dùng khác trong khi bạn đang chỉnh sửa.",
                    title: "CONCURRENT_MODIFICATION",
                    statusCode: StatusCodes.Status409Conflict
                );
            }

            return Results.Ok(new ProjectDto(
                project.Id,
                project.Name,
                project.ProjectType,
                project.Description,
                project.Status,
                project.CreatedByUserId,
                project.CreatedAt,
                project.UpdatedAt,
                project.RowVersion != null ? Convert.ToBase64String(project.RowVersion) : null
            ));
        });

        // 5. Archive Project
        projectGroup.MapPost("/{projectId:guid}/archive", async (
            Guid projectId,
            ClaimsPrincipal principal,
            IProjectAuthorizationService auth,
            AppDbContext db) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null) return Results.Unauthorized();

            var authResult = await auth.AuthorizeAsync(userId, projectId, "Owner", allowArchived: true);
            if (!authResult.IsAuthorized)
            {
                return authResult.Status == ProjectAccessStatus.NotFound
                    ? Results.NotFound()
                    : Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status403Forbidden);
            }

            var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId);
            if (project is null) return Results.NotFound();

            project.Status = "Archived";
            project.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();

            return Results.Ok(new { message = "Dự án đã được lưu trữ (Archived) thành công." });
        });

        // 6. Restore Project
        projectGroup.MapPost("/{projectId:guid}/restore", async (
            Guid projectId,
            ClaimsPrincipal principal,
            IProjectAuthorizationService auth,
            AppDbContext db) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null) return Results.Unauthorized();

            var authResult = await auth.AuthorizeAsync(userId, projectId, "Owner", allowArchived: true);
            if (!authResult.IsAuthorized)
            {
                return authResult.Status == ProjectAccessStatus.NotFound
                    ? Results.NotFound()
                    : Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status403Forbidden);
            }

            var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId);
            if (project is null) return Results.NotFound();

            project.Status = "Active";
            project.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();

            return Results.Ok(new { message = "Dự án đã được kích hoạt lại thành công." });
        });

        // 7. List Project Members
        projectGroup.MapGet("/{projectId:guid}/members", async (
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

            var members = await db.ProjectMembers
                .AsNoTracking()
                .Where(pm => pm.ProjectId == projectId)
                .Include(pm => pm.User)
                .OrderBy(pm => pm.Role)
                .ThenBy(pm => pm.JoinedAt)
                .Select(pm => new ProjectMemberDto(
                    pm.ProjectId,
                    pm.UserId,
                    pm.User.DisplayName,
                    pm.User.Email ?? "",
                    pm.Role,
                    pm.JoinedAt
                ))
                .ToListAsync();

            return Results.Ok(members);
        });

        // 8. Add Project Member
        projectGroup.MapPost("/{projectId:guid}/members", async (
            Guid projectId,
            AddProjectMemberRequest req,
            ClaimsPrincipal principal,
            IProjectAuthorizationService auth,
            AppDbContext db) =>
        {
            var actorUserId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (actorUserId is null) return Results.Unauthorized();

            var authResult = await auth.AuthorizeAsync(actorUserId, projectId, "Owner", allowArchived: false);
            if (!authResult.IsAuthorized)
            {
                return authResult.Status switch
                {
                    ProjectAccessStatus.NotFound => Results.NotFound(),
                    ProjectAccessStatus.Archived => Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status422UnprocessableEntity),
                    _ => Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status403Forbidden)
                };
            }

            if (string.IsNullOrWhiteSpace(req.Email))
            {
                return Results.BadRequest(new { error = "Email người dùng không được để trống." });
            }

            var targetRole = string.IsNullOrWhiteSpace(req.Role) ? "Designer" : req.Role.Trim();
            if (!AllowedRoles.Contains(targetRole))
            {
                return Results.BadRequest(new { error = $"Vai trò '{targetRole}' không hợp lệ. Chỉ chấp nhận Owner, Designer, Viewer." });
            }

            var targetUser = await db.Users.FirstOrDefaultAsync(u => u.Email == req.Email);
            if (targetUser is null)
            {
                return Results.NotFound(new { error = $"Không tìm thấy người dùng có email '{req.Email}'." });
            }

            var existingMember = await db.ProjectMembers
                .FirstOrDefaultAsync(pm => pm.ProjectId == projectId && pm.UserId == targetUser.Id);
            if (existingMember is not null)
            {
                return Results.Conflict(new { error = "Người dùng này đã là thành viên của dự án." });
            }

            var newMember = new ProjectMember
            {
                ProjectId = projectId,
                UserId = targetUser.Id,
                Role = targetRole,
                JoinedAt = DateTimeOffset.UtcNow
            };
            db.ProjectMembers.Add(newMember);
            await db.SaveChangesAsync();

            return Results.Created($"/api/v1/projects/{projectId}/members/{targetUser.Id}", new ProjectMemberDto(
                projectId,
                targetUser.Id,
                targetUser.DisplayName,
                targetUser.Email ?? "",
                newMember.Role,
                newMember.JoinedAt
            ));
        });

        // 9. Update Project Member Role (with Last Owner Invariant guard)
        projectGroup.MapPatch("/{projectId:guid}/members/{targetUserId}", async (
            Guid projectId,
            string targetUserId,
            UpdateProjectMemberRoleRequest req,
            ClaimsPrincipal principal,
            IProjectAuthorizationService auth,
            AppDbContext db) =>
        {
            var actorUserId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (actorUserId is null) return Results.Unauthorized();

            var authResult = await auth.AuthorizeAsync(actorUserId, projectId, "Owner", allowArchived: false);
            if (!authResult.IsAuthorized)
            {
                return authResult.Status switch
                {
                    ProjectAccessStatus.NotFound => Results.NotFound(),
                    ProjectAccessStatus.Archived => Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status422UnprocessableEntity),
                    _ => Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status403Forbidden)
                };
            }

            var targetRole = string.IsNullOrWhiteSpace(req.Role) ? "Designer" : req.Role.Trim();
            if (!AllowedRoles.Contains(targetRole))
            {
                return Results.BadRequest(new { error = $"Vai trò '{targetRole}' không hợp lệ. Chỉ chấp nhận Owner, Designer, Viewer." });
            }

            var targetMember = await db.ProjectMembers.FirstOrDefaultAsync(pm => pm.ProjectId == projectId && pm.UserId == targetUserId);
            if (targetMember is null) return Results.NotFound();

            // Last Owner Invariant: Cannot demote the last owner
            if (string.Equals(targetMember.Role, "Owner", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(targetRole, "Owner", StringComparison.OrdinalIgnoreCase))
            {
                var ownerCount = await db.ProjectMembers
                    .CountAsync(pm => pm.ProjectId == projectId && pm.Role == "Owner");
                if (ownerCount <= 1)
                {
                    return Results.Conflict(new { error = "Không thể hạ quyền Owner của người sở hữu duy nhất trong dự án." });
                }
            }

            targetMember.Role = targetRole;
            await db.SaveChangesAsync();

            return Results.Ok(new { message = $"Vai trò của thành viên đã được cập nhật thành '{targetRole}'." });
        });

        // 10. Remove Project Member (with Last Owner Invariant guard)
        projectGroup.MapDelete("/{projectId:guid}/members/{targetUserId}", async (
            Guid projectId,
            string targetUserId,
            ClaimsPrincipal principal,
            IProjectAuthorizationService auth,
            AppDbContext db) =>
        {
            var actorUserId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (actorUserId is null) return Results.Unauthorized();

            var authResult = await auth.AuthorizeAsync(actorUserId, projectId, "Owner", allowArchived: false);
            if (!authResult.IsAuthorized)
            {
                return authResult.Status switch
                {
                    ProjectAccessStatus.NotFound => Results.NotFound(),
                    ProjectAccessStatus.Archived => Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status422UnprocessableEntity),
                    _ => Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status403Forbidden)
                };
            }

            var targetMember = await db.ProjectMembers.FirstOrDefaultAsync(pm => pm.ProjectId == projectId && pm.UserId == targetUserId);
            if (targetMember is null) return Results.NotFound();

            // Last Owner Invariant: Cannot remove the last owner
            if (string.Equals(targetMember.Role, "Owner", StringComparison.OrdinalIgnoreCase))
            {
                var ownerCount = await db.ProjectMembers
                    .CountAsync(pm => pm.ProjectId == projectId && pm.Role == "Owner");
                if (ownerCount <= 1)
                {
                    return Results.Conflict(new { error = "Không thể xóa người sở hữu (Owner) duy nhất khỏi dự án." });
                }
            }

            db.ProjectMembers.Remove(targetMember);
            await db.SaveChangesAsync();

            return Results.NoContent();
        });

        // 11. Transfer Ownership (Transactional)
        projectGroup.MapPost("/{projectId:guid}/transfer-ownership", async (
            Guid projectId,
            TransferOwnershipRequest req,
            ClaimsPrincipal principal,
            IProjectAuthorizationService auth,
            AppDbContext db) =>
        {
            var actorUserId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (actorUserId is null) return Results.Unauthorized();

            var authResult = await auth.AuthorizeAsync(actorUserId, projectId, "Owner", allowArchived: false);
            if (!authResult.IsAuthorized)
            {
                return authResult.Status switch
                {
                    ProjectAccessStatus.NotFound => Results.NotFound(),
                    ProjectAccessStatus.Archived => Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status422UnprocessableEntity),
                    _ => Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status403Forbidden)
                };
            }

            if (string.IsNullOrWhiteSpace(req.NewOwnerUserId))
            {
                return Results.BadRequest(new { error = "Mã người dùng sở hữu mới không được để trống." });
            }

            if (req.NewOwnerUserId == actorUserId)
            {
                return Results.BadRequest(new { error = "Bạn đã là người sở hữu dự án này." });
            }

            var targetUser = await db.Users.FindAsync(req.NewOwnerUserId);
            if (targetUser is null || !targetUser.IsActive)
            {
                return Results.NotFound(new { error = "Người nhận quyền sở hữu không tồn tại hoặc đã bị vô hiệu hóa." });
            }

            // Perform ownership transfer atomically via ExecutionStrategy (required with SQL Server EnableRetryOnFailure)
            var strategy = db.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await db.Database.BeginTransactionAsync();
                try
                {
                    var actorMember = await db.ProjectMembers
                        .FirstOrDefaultAsync(pm => pm.ProjectId == projectId && pm.UserId == actorUserId);
                    if (actorMember is null)
                    {
                        await tx.RollbackAsync();
                        return Results.NotFound();
                    }

                    var targetMember = await db.ProjectMembers
                        .FirstOrDefaultAsync(pm => pm.ProjectId == projectId && pm.UserId == req.NewOwnerUserId);

                    if (targetMember is null)
                    {
                        targetMember = new ProjectMember
                        {
                            ProjectId = projectId,
                            UserId = req.NewOwnerUserId,
                            Role = "Owner",
                            JoinedAt = DateTimeOffset.UtcNow
                        };
                        db.ProjectMembers.Add(targetMember);
                    }
                    else
                    {
                        targetMember.Role = "Owner";
                    }

                    // Demote actor to Designer
                    actorMember.Role = "Designer";

                    await db.SaveChangesAsync();
                    await tx.CommitAsync();

                    return Results.Ok(new { message = $"Quyền sở hữu dự án đã được chuyển giao thành công cho {targetUser.DisplayName}." });
                }
                catch
                {
                    await tx.RollbackAsync();
                    throw;
                }
            });
        });

        return group;
    }
}

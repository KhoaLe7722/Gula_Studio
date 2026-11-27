using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using CharacterConceptAI.Server.Authentication;
using CharacterConceptAI.Server.Data;
using CharacterConceptAI.Server.Data.Entities;
using CharacterConceptAI.Shared.Contracts.Auth;
using CharacterConceptAI.Shared.Contracts.Common;

namespace CharacterConceptAI.Server.Endpoints;

public static class AdminEndpoints
{
    public static RouteGroupBuilder MapAdminEndpoints(this RouteGroupBuilder group)
    {
        var adminGroup = group.RequireAuthorization(policy => policy.RequireRole(SecureAdminBootstrap.AdminRole));

        // Create user
        adminGroup.MapPost("/accounts", async (
            CreateAccountRequest req,
            UserManager<ApplicationUser> userManager,
            RoleManager<IdentityRole> roleManager) =>
        {
            if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password))
            {
                return Results.BadRequest(new { error = "Email và mật khẩu không được để trống." });
            }

            var existing = await userManager.FindByEmailAsync(req.Email);
            if (existing is not null)
            {
                return Results.Conflict(new { error = "Email này đã được sử dụng." });
            }

            var user = new ApplicationUser
            {
                UserName = req.Email.Trim(),
                Email = req.Email.Trim(),
                DisplayName = string.IsNullOrWhiteSpace(req.DisplayName) ? req.Email.Trim() : req.DisplayName.Trim(),
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow
            };

            var createResult = await userManager.CreateAsync(user, req.Password);
            if (!createResult.Succeeded)
            {
                return Results.BadRequest(new { errors = createResult.Errors.Select(e => e.Description) });
            }

            if (req.Roles is { Count: > 0 })
            {
                var requestedRoles = req.Roles.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                foreach (var role in requestedRoles)
                {
                    if (!await roleManager.RoleExistsAsync(role))
                    {
                        var roleCreate = await roleManager.CreateAsync(new IdentityRole(role));
                        if (!roleCreate.Succeeded)
                            return Results.BadRequest(new { errors = roleCreate.Errors.Select(e => e.Description) });
                    }
                }
                if (requestedRoles.Count > 0)
                {
                    var addRoleResult = await userManager.AddToRolesAsync(user, requestedRoles);
                    if (!addRoleResult.Succeeded)
                        return Results.BadRequest(new { errors = addRoleResult.Errors.Select(e => e.Description) });
                }
            }

            var roles = await userManager.GetRolesAsync(user);

            return Results.Created($"/api/v1/admin/accounts/{user.Id}", new AccountDto(
                Id: user.Id,
                Email: user.Email,
                DisplayName: user.DisplayName,
                IsActive: user.IsActive,
                CreatedAt: user.CreatedAt,
                Roles: roles.ToList()
            ));
        });

        // List users (paginated, single batch role query — eliminates N+1 GetRolesAsync loop)
        adminGroup.MapGet("/accounts", async (
            int? page,
            int? pageSize,
            string? query,
            AppDbContext db,
            CancellationToken ct) =>
        {
            var p = page.GetValueOrDefault(1);
            p = p < 1 ? 1 : p;
            var ps = pageSize.GetValueOrDefault(20);
            ps = ps < 1 || ps > 100 ? 20 : ps;
            if ((long)(p - 1) * ps > int.MaxValue)
                return Results.BadRequest(new { error = "Trang yêu cầu vượt giới hạn hỗ trợ." });

            var q = db.Users.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(query))
            {
                q = q.Where(u => u.Email!.Contains(query) || u.DisplayName.Contains(query));
            }

            var total = await q.CountAsync(ct);
            var users = await q
                .OrderByDescending(u => u.CreatedAt)
                .ThenBy(u => u.Id)
                .Skip((p - 1) * ps)
                .Take(ps)
                .Select(u => new { u.Id, u.Email, u.DisplayName, u.IsActive, u.CreatedAt })
                .ToListAsync(ct);

            var userIds = users.Select(u => u.Id).ToList();
            var roleRows = userIds.Count == 0
                ? []
                : await db.UserRoles.AsNoTracking()
                    .Where(ur => userIds.Contains(ur.UserId))
                    .Join(db.Roles.AsNoTracking(), ur => ur.RoleId, r => r.Id, (ur, r) => new { ur.UserId, r.Name })
                    .ToListAsync(ct);
            var roleMap = roleRows
                .GroupBy(x => x.UserId)
                .ToDictionary(g => g.Key, g => g.Where(x => x.Name != null).Select(x => x.Name!).ToList());

            var items = users.Select(u => new AccountDto(
                Id: u.Id,
                Email: u.Email ?? "",
                DisplayName: u.DisplayName,
                IsActive: u.IsActive,
                CreatedAt: u.CreatedAt,
                Roles: roleMap.TryGetValue(u.Id, out var rList) ? rList : []
            )).ToList();

            return Results.Ok(new PagedResult<AccountDto>
            {
                Items = items,
                Page = p,
                PageSize = ps,
                TotalCount = total
            });
        });

        // Toggle user status
        adminGroup.MapPatch("/accounts/{userId}/status", async (
            string userId,
            UpdateAccountStatusRequest req,
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> userManager,
            AuthValidationCache authCache) =>
        {
            var currentUserId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (currentUserId == userId && !req.IsActive)
            {
                return Results.BadRequest(new { error = "Không thể tự vô hiệu hóa tài khoản của chính mình." });
            }

            var user = await userManager.FindByIdAsync(userId);
            if (user is null) return Results.NotFound();

            user.IsActive = req.IsActive;
            var updated = await userManager.UpdateAsync(user);
            if (!updated.Succeeded) return Results.Conflict(new { error = "Tài khoản đã thay đổi. Hãy tải lại." });
            if (!req.IsActive) await userManager.UpdateSecurityStampAsync(user);
            authCache.InvalidateUser(userId);

            return Results.Ok(new { message = $"Tài khoản đã được {(req.IsActive ? "kích hoạt" : "vô hiệu hóa")} thành công." });
        });

        // Reset password
        adminGroup.MapPost("/accounts/{userId}/reset-password", async (
            string userId,
            ResetPasswordRequest req,
            UserManager<ApplicationUser> userManager,
            AuthValidationCache authCache) =>
        {
            if (string.IsNullOrWhiteSpace(req.NewPassword))
            {
                return Results.BadRequest(new { error = "Mật khẩu mới không được để trống." });
            }

            var user = await userManager.FindByIdAsync(userId);
            if (user is null) return Results.NotFound();

            var token = await userManager.GeneratePasswordResetTokenAsync(user);
            var resetResult = await userManager.ResetPasswordAsync(user, token, req.NewPassword);

            if (!resetResult.Succeeded)
            {
                return Results.BadRequest(new { errors = resetResult.Errors.Select(e => e.Description) });
            }
            authCache.InvalidateUser(userId);

            return Results.Ok(new { message = "Mật khẩu đã được đặt lại thành công." });
        });

        return group;
    }
}

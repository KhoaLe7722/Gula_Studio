using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using CharacterConceptAI.Server.Data;
using CharacterConceptAI.Server.Data.Entities;
using CharacterConceptAI.Shared.Contracts.Auth;

namespace CharacterConceptAI.Server.Endpoints;

public static class AuthEndpoints
{
    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/login", async (
            LoginRequest req,
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager) =>
        {
            if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password))
            {
                return Results.Unauthorized();
            }

            var user = await userManager.FindByEmailAsync(req.Email) ?? await userManager.FindByNameAsync(req.Email);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            var signInResult = await signInManager.CheckPasswordSignInAsync(user, req.Password, lockoutOnFailure: true);
            if (signInResult.IsLockedOut)
            {
                return Results.Problem("Tài khoản đã bị tạm khóa do nhập sai mật khẩu quá 5 lần. Vui lòng thử lại sau.", statusCode: StatusCodes.Status423Locked);
            }

            if (!signInResult.Succeeded)
            {
                return Results.Unauthorized();
            }

            if (!user.IsActive)
                return Results.Problem("Tài khoản này đã bị vô hiệu hóa bởi Quản trị viên.", statusCode: StatusCodes.Status403Forbidden);

            var principal = await signInManager.CreateUserPrincipalAsync(user);
            return Results.SignIn(principal, authenticationScheme: IdentityConstants.BearerScheme);
        });

        group.MapPost("/refresh", async (
            RefreshTokenRequest req,
            IOptionsMonitor<BearerTokenOptions> bearerTokenOptions,
            SignInManager<ApplicationUser> signInManager) =>
        {
            var options = bearerTokenOptions.Get(IdentityConstants.BearerScheme);
            var ticket = options.RefreshTokenProtector.Unprotect(req.RefreshToken);

            if (ticket?.Properties.ExpiresUtc is not { } expiresUtc || DateTimeOffset.UtcNow >= expiresUtc)
            {
                return Results.Unauthorized();
            }

            var userId = ticket.Principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null)
            {
                return Results.Unauthorized();
            }

            var user = await signInManager.ValidateSecurityStampAsync(ticket.Principal);
            if (user is null || !user.IsActive)
            {
                return Results.Problem("Tài khoản không tồn tại hoặc đã bị vô hiệu hóa.", statusCode: StatusCodes.Status403Forbidden);
            }

            var principal = await signInManager.CreateUserPrincipalAsync(user);
            return Results.SignIn(principal, authenticationScheme: IdentityConstants.BearerScheme);
        });

        group.MapGet("/me", async (
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> userManager) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null) return Results.Unauthorized();

            var user = await userManager.FindByIdAsync(userId);
            if (user is null) return Results.NotFound();

            if (!user.IsActive)
            {
                return Results.Problem("Tài khoản đã bị vô hiệu hóa.", statusCode: StatusCodes.Status403Forbidden);
            }

            var roles = await userManager.GetRolesAsync(user);

            return Results.Ok(new UserInfoResponse(
                Id: user.Id,
                Email: user.Email ?? "",
                DisplayName: user.DisplayName,
                IsActive: user.IsActive,
                GlobalRoles: roles.ToList()
            ));
        }).RequireAuthorization();

        // Loopback-only automatic session for personal single-PC studio mode (opt-in via Auth:LocalNoAuth=true).
        group.MapPost("/local-session", async (
            HttpContext httpContext,
            IConfiguration config,
            AppDbContext db,
            UserManager<ApplicationUser> userManager,
            RoleManager<IdentityRole> roleManager,
            SignInManager<ApplicationUser> signInManager) =>
        {
            if (!config.GetValue<bool>("Auth:LocalNoAuth"))
                return Results.NotFound();

            var remoteIp = httpContext.Connection.RemoteIpAddress;
            if (remoteIp is not null && !IPAddress.IsLoopback(remoteIp))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            var user = await db.Users.Where(u => u.IsActive).OrderBy(u => u.CreatedAt).FirstOrDefaultAsync();
            if (user is null)
            {
                if (!await roleManager.RoleExistsAsync("SystemAdmin"))
                    await roleManager.CreateAsync(new IdentityRole("SystemAdmin"));

                user = new ApplicationUser
                {
                    UserName = "studio@local",
                    Email = "studio@local",
                    DisplayName = "Gula Studio",
                    EmailConfirmed = true,
                    IsActive = true
                };
                var createRes = await userManager.CreateAsync(user, $"Local!{Guid.NewGuid():N}9A");
                if (!createRes.Succeeded)
                    return Results.Problem("Không khởi tạo được phiên làm việc cục bộ.", statusCode: StatusCodes.Status500InternalServerError);

                await userManager.AddToRoleAsync(user, "SystemAdmin");
            }

            // Ensure seamless local studio workspace (default project + character + Owner access)
            var project = await db.Projects.Where(p => p.Status != "Archived").OrderBy(p => p.CreatedAt).FirstOrDefaultAsync();
            if (project is null)
            {
                project = new Project
                {
                    Name = "Gula Studio",
                    ProjectType = "Concept",
                    Description = "Không gian sáng tạo AI cục bộ",
                    Status = "Active",
                    CreatedByUserId = user.Id,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                db.Projects.Add(project);
                db.ProjectMembers.Add(new ProjectMember
                {
                    ProjectId = project.Id,
                    UserId = user.Id,
                    Role = "Owner",
                    JoinedAt = DateTimeOffset.UtcNow
                });
                await db.SaveChangesAsync();
            }
            else
            {
                var membership = await db.ProjectMembers.FirstOrDefaultAsync(m => m.ProjectId == project.Id && m.UserId == user.Id);
                if (membership is null)
                {
                    db.ProjectMembers.Add(new ProjectMember
                    {
                        ProjectId = project.Id,
                        UserId = user.Id,
                        Role = "Owner",
                        JoinedAt = DateTimeOffset.UtcNow
                    });
                    await db.SaveChangesAsync();
                }
                else if (membership.Role != "Owner")
                {
                    membership.Role = "Owner";
                    await db.SaveChangesAsync();
                }
            }

            var hasCharacter = await db.Characters.AnyAsync(c => c.ProjectId == project.Id && c.Status != "Archived");
            if (!hasCharacter)
            {
                db.Characters.Add(new Character
                {
                    Id = Guid.NewGuid(),
                    ProjectId = project.Id,
                    Name = "Tự do",
                    Role = "Protagonist",
                    Description = "Sáng tạo tự do",
                    Status = "Active"
                });
                await db.SaveChangesAsync();
            }

            var principal = await signInManager.CreateUserPrincipalAsync(user);
            return Results.SignIn(principal, authenticationScheme: IdentityConstants.BearerScheme);
        });

        return group;
    }
}

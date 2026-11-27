using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using CharacterConceptAI.Server.Data;
using CharacterConceptAI.Server.Data.Entities;

namespace CharacterConceptAI.Server.Authentication;

/// <summary>
/// Short-TTL per-user validated principal cache eliminating 5-6 redundant ASP.NET Core Identity SQL queries
/// on every HTTP request (media thumbnails, polling, gallery fetches) while immediately invalidating on admin changes.
/// </summary>
public sealed class AuthValidationCache
{
    private sealed record CacheEntry(string SecurityStamp, ClaimsPrincipal Principal, long ExpiresAtTicks);
    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);
    private static readonly long TtlMs = 15_000;

    public bool TryGetValidPrincipal(string userId, string? tokenSecurityStamp, out ClaimsPrincipal principal)
    {
        if (!string.IsNullOrEmpty(tokenSecurityStamp) &&
            _entries.TryGetValue(userId, out var entry) &&
            Environment.TickCount64 < entry.ExpiresAtTicks &&
            string.Equals(entry.SecurityStamp, tokenSecurityStamp, StringComparison.Ordinal))
        {
            principal = entry.Principal;
            return true;
        }
        principal = null!;
        return false;
    }

    public void Set(string userId, string securityStamp, ClaimsPrincipal principal)
    {
        if (_entries.Count >= 256) _entries.Clear();
        _entries[userId] = new CacheEntry(securityStamp, principal, Environment.TickCount64 + TtlMs);
    }

    public void InvalidateUser(string userId)
    {
        if (!string.IsNullOrEmpty(userId))
            _entries.TryRemove(userId, out _);
    }
}

public static class SecureAdminBootstrap
{
    public const string AdminRole = "SystemAdmin";
    public const string DefaultAdminEmail = "admin@conceptai.local";
    public static readonly string[] AllowedRoles = [AdminRole, "Designer", "Viewer", "User"];

    public static async Task InitializeAsync(IServiceProvider serviceProvider, IConfiguration configuration, ILogger logger)
    {
        using var scope = serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        // Single query checks all 4 required roles at once (saves 3 startup DB round-trips)
        var existingRoles = await db.Roles.AsNoTracking()
            .Select(r => r.Name!)
            .ToListAsync();
        var existingSet = new HashSet<string>(existingRoles, StringComparer.OrdinalIgnoreCase);

        foreach (var role in AllowedRoles)
        {
            if (!existingSet.Contains(role))
            {
                var result = await roleManager.CreateAsync(new IdentityRole(role));
                if (!result.Succeeded)
                    throw new InvalidOperationException("Unable to initialize account roles.");
            }
        }

        // Explicit one-time local provisioning: existing accounts are never promoted or reset.
        if (!configuration.GetValue<bool>("Admin:BootstrapEnabled")) return;
        if ((await userManager.GetUsersInRoleAsync(AdminRole)).Count > 0)
        {
            logger.LogInformation("Administrator bootstrap skipped: an administrator already exists.");
            return;
        }
        var email = configuration["Admin:Email"];
        var password = configuration["Admin:Password"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("Explicit administrator bootstrap requires Admin:Email and Admin:Password from a secure local source.");
        if (await userManager.FindByEmailAsync(email) is not null)
            throw new InvalidOperationException("Bootstrap will not promote an existing account. Use a new administrator email.");

        var user = new ApplicationUser
        {
            UserName = email, Email = email, DisplayName = "Quản trị hệ thống",
            IsActive = true, CreatedAt = DateTimeOffset.UtcNow
        };
        var created = await userManager.CreateAsync(user, password);
        if (!created.Succeeded)
            throw new InvalidOperationException("Administrator bootstrap failed: " + string.Join(", ", created.Errors.Select(e => e.Code)));
        var assigned = await userManager.AddToRoleAsync(user, AdminRole);
        if (!assigned.Succeeded)
            throw new InvalidOperationException("Unable to assign the administrator role.");
        logger.LogInformation("Initial administrator provisioned. Remove bootstrap secrets and disable Admin:BootstrapEnabled.");
    }
}

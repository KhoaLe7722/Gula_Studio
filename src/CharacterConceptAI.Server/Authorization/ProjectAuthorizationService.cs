using Microsoft.EntityFrameworkCore;
using CharacterConceptAI.Server.Data;

namespace CharacterConceptAI.Server.Authorization;

public enum ProjectAccessStatus
{
    Authorized,
    NotFound,
    Forbidden,
    Archived
}

public record ProjectAuthorizationResult(
    ProjectAccessStatus Status,
    string? UserRole = null,
    string? ErrorMessage = null
)
{
    public bool IsAuthorized => Status == ProjectAccessStatus.Authorized;
}

public interface IProjectAuthorizationService
{
    Task<ProjectAuthorizationResult> AuthorizeAsync(string userId, Guid projectId, string minimumRole, bool allowArchived = false, CancellationToken ct = default);
}

public class ProjectAuthorizationService : IProjectAuthorizationService
{
    private readonly AppDbContext _db;

    private static readonly Dictionary<string, int> RoleHierarchy = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Viewer"] = 1,
        ["Designer"] = 2,
        ["Owner"] = 3
    };

    public ProjectAuthorizationService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<ProjectAuthorizationResult> AuthorizeAsync(string userId, Guid projectId, string minimumRole, bool allowArchived = false, CancellationToken ct = default)
    {
        // Single projected SQL query (replaces 3 sequential full-entity round-trips per request)
        var info = await _db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new
            {
                u.IsActive,
                Project = _db.Projects.AsNoTracking()
                    .Where(p => p.Id == projectId)
                    .Select(p => new
                    {
                        p.Status,
                        Role = p.Members
                            .Where(pm => pm.UserId == userId)
                            .Select(pm => pm.Role)
                            .FirstOrDefault()
                    })
                    .FirstOrDefault()
            })
            .FirstOrDefaultAsync(ct);

        if (info is null || !info.IsActive)
        {
            return new ProjectAuthorizationResult(ProjectAccessStatus.Forbidden, null, "Tài khoản không tồn tại hoặc đã bị vô hiệu hóa.");
        }

        if (info.Project is null || info.Project.Role is null)
        {
            // IDOR Protection: Return NotFound to conceal existence from non-members
            return new ProjectAuthorizationResult(ProjectAccessStatus.NotFound, null, "Không tìm thấy dự án.");
        }

        var effectiveRole = info.Project.Role;

        if (!RoleHierarchy.TryGetValue(effectiveRole, out var userLevel) ||
            !RoleHierarchy.TryGetValue(minimumRole, out var requiredLevel) || userLevel < requiredLevel)
        {
            return new ProjectAuthorizationResult(ProjectAccessStatus.Forbidden, effectiveRole, $"Bạn cần tối thiểu quyền {minimumRole} để thực hiện thao tác này.");
        }

        if (!allowArchived && string.Equals(info.Project.Status, "Archived", StringComparison.OrdinalIgnoreCase))
        {
            return new ProjectAuthorizationResult(ProjectAccessStatus.Archived, effectiveRole, "Dự án đã được lưu trữ (Archived), không thể thực hiện thao tác sửa đổi.");
        }

        return new ProjectAuthorizationResult(ProjectAccessStatus.Authorized, effectiveRole);
    }
}

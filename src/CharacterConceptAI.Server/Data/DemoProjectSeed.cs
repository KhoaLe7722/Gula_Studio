using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using CharacterConceptAI.Server.Authentication;
using CharacterConceptAI.Server.Data.Entities;

namespace CharacterConceptAI.Server.Data;

public static class DemoProjectSeed
{
    public static async Task EnsureDemoDataAsync(IServiceProvider serviceProvider, ILogger logger)
    {
        using var scope = serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        if (await db.Projects.AnyAsync())
        {
            return;
        }

        var adminUser = await userManager.FindByEmailAsync(SecureAdminBootstrap.DefaultAdminEmail);
        if (adminUser is null)
        {
            return;
        }

        var demoProject = new Project
        {
            Name = "Dự Án Anime Khởi Tạo (Demo)",
            ProjectType = "Game",
            Description = "Dự án mẫu khởi tạo cho môi trường phát triển cục bộ CharacterConceptAI.",
            CreatedByUserId = adminUser.Id,
            Status = "Active",
            CreatedAt = DateTimeOffset.UtcNow
        };

        db.Projects.Add(demoProject);

        db.ProjectMembers.Add(new ProjectMember
        {
            ProjectId = demoProject.Id,
            UserId = adminUser.Id,
            Role = "Owner",
            JoinedAt = DateTimeOffset.UtcNow
        });

        var luna = new Character
        {
            ProjectId = demoProject.Id,
            Name = "Luna",
            Role = "Protagonist",
            Description = "Nữ hiệp sĩ tóc bạc mắt biếc, chiến binh bảo vệ vương quốc cổ đại.",
            Status = "Active"
        };

        luna.Variants.Add(new CharacterVariant
        {
            CharacterId = luna.Id,
            Name = "Default (Hiệp Sĩ Giáp Đen)",
            IsDefault = true,
            Status = "Active"
        });

        db.Characters.Add(luna);

        await db.SaveChangesAsync();
        logger.LogInformation("Demo project seeded successfully: '{ProjectName}' with Character '{CharacterName}'.", demoProject.Name, luna.Name);
    }
}

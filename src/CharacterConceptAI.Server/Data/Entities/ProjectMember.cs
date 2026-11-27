using System.ComponentModel.DataAnnotations;

namespace CharacterConceptAI.Server.Data.Entities;

public class ProjectMember
{
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public string UserId { get; set; } = string.Empty;
    public ApplicationUser User { get; set; } = null!;

    [MaxLength(50)]
    public string Role { get; set; } = "Designer"; // Owner, Designer, Viewer
    public DateTimeOffset JoinedAt { get; set; } = DateTimeOffset.UtcNow;
}

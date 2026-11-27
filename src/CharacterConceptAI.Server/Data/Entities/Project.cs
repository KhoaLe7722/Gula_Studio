using System.ComponentModel.DataAnnotations;

namespace CharacterConceptAI.Server.Data.Entities;

public class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(150)]
    public string Name { get; set; } = string.Empty;
    [MaxLength(50)]
    public string ProjectType { get; set; } = "Game";
    [MaxLength(5000)]
    public string? Description { get; set; }
    [MaxLength(50)]
    public string Status { get; set; } = "Active"; // Draft, Active, Completed, Archived
    public string CreatedByUserId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
    [Timestamp]
    public byte[]? RowVersion { get; set; }

    public ICollection<ProjectMember> Members { get; set; } = [];
    public ICollection<Script> Scripts { get; set; } = [];
    public ICollection<Character> Characters { get; set; } = [];
    public ICollection<GenerationJob> GenerationJobs { get; set; } = [];
    public ICollection<ImageAsset> ImageAssets { get; set; } = [];
    public ICollection<CharacterConcept> CharacterConcepts { get; set; } = [];
}

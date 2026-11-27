using System.ComponentModel.DataAnnotations;

namespace CharacterConceptAI.Server.Data.Entities;

public class Character
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;
    [MaxLength(50)]
    public string Role { get; set; } = "Protagonist";
    [MaxLength(5000)]
    public string? Description { get; set; }
    [MaxLength(50)]
    public string Status { get; set; } = "Active"; // Active, Archived
    [Timestamp]
    public byte[]? RowVersion { get; set; }

    public ICollection<CharacterVariant> Variants { get; set; } = [];
    public ICollection<CharacterAttributeValue> AttributeValues { get; set; } = [];
    public ICollection<GenerationJob> GenerationJobs { get; set; } = [];
    public ICollection<SceneCharacter> SceneCharacters { get; set; } = [];
    public ICollection<CharacterRevision> Revisions { get; set; } = [];
    public ICollection<CharacterRelationship> SourceRelationships { get; set; } = [];
    public ICollection<CharacterRelationship> TargetRelationships { get; set; } = [];
    public ICollection<CharacterConcept> Concepts { get; set; } = [];
}

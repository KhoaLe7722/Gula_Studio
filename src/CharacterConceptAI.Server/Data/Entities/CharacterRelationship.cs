using System.ComponentModel.DataAnnotations;

namespace CharacterConceptAI.Server.Data.Entities;

public class CharacterRelationship
{
    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public Guid SourceCharacterId { get; set; }
    public Character SourceCharacter { get; set; } = null!;

    public Guid TargetCharacterId { get; set; }
    public Character TargetCharacter { get; set; } = null!;

    [MaxLength(50)]
    public string Type { get; set; } = "Ally"; // Ally, Rival, Mentor, Enemy, Family, Romantic

    [MaxLength(1000)]
    public string? Notes { get; set; }

    [Timestamp]
    public byte[]? RowVersion { get; set; }
}

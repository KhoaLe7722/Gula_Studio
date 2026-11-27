using System.ComponentModel.DataAnnotations;

namespace CharacterConceptAI.Server.Data.Entities;

public class CharacterRevision
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid CharacterId { get; set; }
    public Character Character { get; set; } = null!;

    public Guid? VariantId { get; set; }

    public int RevisionNumber { get; set; }

    /// <summary>
    /// Canonical JSON snapshot of character profile, attributes, and variant configurations at revision time.
    /// </summary>
    public string SnapshotJson { get; set; } = string.Empty;

    public Guid? PreviousRevisionId { get; set; }

    public string? ActorUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [MaxLength(500)]
    public string? ChangeSummary { get; set; }
}

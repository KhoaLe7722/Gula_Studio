using System.ComponentModel.DataAnnotations;
using CharacterConceptAI.Shared.Contracts.Concepts;

namespace CharacterConceptAI.Server.Data.Entities;

public class CharacterConcept
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public Guid CharacterId { get; set; }
    public Character Character { get; set; } = null!;

    public Guid? VariantId { get; set; }
    public CharacterVariant? Variant { get; set; }

    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string? Description { get; set; }

    public Guid? RepresentativeVersionId { get; set; }
    public ConceptVersion? RepresentativeVersion { get; set; }

    public ConceptStatus Status { get; set; } = ConceptStatus.Draft;

    [MaxLength(450)]
    public string CreatedByUserId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }

    [Timestamp]
    public byte[]? RowVersion { get; set; }

    public ICollection<ConceptVersion> Versions { get; set; } = [];
}

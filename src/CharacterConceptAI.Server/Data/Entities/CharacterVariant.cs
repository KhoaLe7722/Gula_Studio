using System.ComponentModel.DataAnnotations;

namespace CharacterConceptAI.Server.Data.Entities;

public class CharacterVariant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CharacterId { get; set; }
    public Character Character { get; set; } = null!;

    [MaxLength(100)]
    public string Name { get; set; } = "Default";
    [MaxLength(1000)]
    public string? Description { get; set; }
    public bool IsDefault { get; set; } = true;
    [MaxLength(50)]
    public string Status { get; set; } = "Active";
    [Timestamp]
    public byte[]? RowVersion { get; set; }

    public ICollection<GenerationJob> GenerationJobs { get; set; } = [];
    public ICollection<VariantAttributeValue> AttributeValues { get; set; } = [];
}

using System.ComponentModel.DataAnnotations;

namespace CharacterConceptAI.Server.Data.Entities;

public class CharacterAttributeDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ProjectId { get; set; }
    public Project? Project { get; set; }

    [MaxLength(50)]
    public string Code { get; set; } = string.Empty; // e.g. hair_color, clothing
    [MaxLength(100)]
    public string DisplayName { get; set; } = string.Empty;
    [MaxLength(30)]
    public string DataType { get; set; } = "Text"; // Text, Number, Boolean, SingleSelect
    public string? AllowedValuesJson { get; set; }
    public bool IsRequired { get; set; }
    public bool IsIdentityCritical { get; set; }

    public ICollection<TemplateAttributeDefinition> TemplateAttributes { get; set; } = [];
    public ICollection<VariantAttributeValue> VariantValues { get; set; } = [];
}

public class CharacterAttributeValue
{
    public Guid CharacterId { get; set; }
    public Character Character { get; set; } = null!;

    public Guid DefinitionId { get; set; }
    public CharacterAttributeDefinition Definition { get; set; } = null!;

    public string? ValueJson { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

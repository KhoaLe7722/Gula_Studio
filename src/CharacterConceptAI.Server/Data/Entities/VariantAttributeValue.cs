namespace CharacterConceptAI.Server.Data.Entities;

public class VariantAttributeValue
{
    public Guid VariantId { get; set; }
    public CharacterVariant Variant { get; set; } = null!;

    public Guid DefinitionId { get; set; }
    public CharacterAttributeDefinition Definition { get; set; } = null!;

    public string? ValueJson { get; set; }
}

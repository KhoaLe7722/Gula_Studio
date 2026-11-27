namespace CharacterConceptAI.Server.Data.Entities;

public class TemplateAttributeDefinition
{
    public Guid TemplateId { get; set; }
    public CharacterTemplate Template { get; set; } = null!;

    public Guid DefinitionId { get; set; }
    public CharacterAttributeDefinition Definition { get; set; } = null!;

    public string? DefaultValueJson { get; set; }
}

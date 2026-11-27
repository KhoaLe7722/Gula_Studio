using System.ComponentModel.DataAnnotations;

namespace CharacterConceptAI.Server.Data.Entities;

public class CharacterTemplate
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid? ProjectId { get; set; }
    public Project? Project { get; set; }

    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string? Description { get; set; }

    public string? CreatedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public bool IsArchived { get; set; }

    public ICollection<TemplateAttributeDefinition> TemplateAttributes { get; set; } = [];
}

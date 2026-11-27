using System.ComponentModel.DataAnnotations;
using CharacterConceptAI.Shared.Contracts.Concepts;

namespace CharacterConceptAI.Server.Data.Entities;

public class ConceptApproval
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ConceptVersionId { get; set; }
    public ConceptVersion ConceptVersion { get; set; } = null!;

    [MaxLength(450)]
    public string ReviewerUserId { get; set; } = string.Empty;
    public ApplicationUser ReviewerUser { get; set; } = null!;

    public ConceptApprovalDecision Decision { get; set; }
    [MaxLength(2000)]
    public string? Comment { get; set; }
    public DateTimeOffset ReviewedAt { get; set; } = DateTimeOffset.UtcNow;
}

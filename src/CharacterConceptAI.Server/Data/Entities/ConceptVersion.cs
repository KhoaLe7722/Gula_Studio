using System.ComponentModel.DataAnnotations;
using CharacterConceptAI.Shared.Contracts.Concepts;

namespace CharacterConceptAI.Server.Data.Entities;

public class ConceptVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ConceptId { get; set; }
    public CharacterConcept Concept { get; set; } = null!;

    public int VersionNumber { get; set; } = 1;

    public Guid? ParentVersionId { get; set; }
    public ConceptVersion? ParentVersion { get; set; }

    public Guid? GenerationJobId { get; set; }
    public GenerationJob? GenerationJob { get; set; }

    public Guid ImageAssetId { get; set; }
    public ImageAsset ImageAsset { get; set; } = null!;

    public string FinalPromptSnapshot { get; set; } = string.Empty;
    public string? ParametersSnapshotJson { get; set; }

    public ConceptVersionStatus Status { get; set; } = ConceptVersionStatus.Draft;

    [MaxLength(450)]
    public string CreatedByUserId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<ConceptApproval> Approvals { get; set; } = [];
}

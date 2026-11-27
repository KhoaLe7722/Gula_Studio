using System.ComponentModel.DataAnnotations;
using CharacterConceptAI.Shared.Contracts.Generations;

namespace CharacterConceptAI.Server.Data.Entities;

public class GenerationJob
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public Guid CharacterId { get; set; }
    public Character Character { get; set; } = null!;

    public Guid? VariantId { get; set; }
    public CharacterVariant? Variant { get; set; }

    public string RequestedByUserId { get; set; } = string.Empty;
    public ApplicationUser RequestedByUser { get; set; } = null!;

    public GenerationStatus Status { get; set; } = GenerationStatus.Pending;
    public PromptMode PromptMode { get; set; } = PromptMode.Hybrid;

    public string FinalPrompt { get; set; } = string.Empty;
    public string? RawPrompt { get; set; }
    public string? ProfileSnapshotJson { get; set; }

    [MaxLength(50)]
    public string? PromptTemplateVersion { get; set; } = "1.0";

    [MaxLength(100)]
    public string? WorkflowKey { get; set; } = "t2i_default";

    [MaxLength(50)]
    public string? WorkflowVersion { get; set; } = "1.0";

    public double? Denoise { get; set; }
    public Guid? ReferenceConceptVersionId { get; set; }
    public ConceptVersion? ReferenceConceptVersion { get; set; }

    [MaxLength(128)]
    public string? InputAssetSha256 { get; set; }

    public int Width { get; set; } = 512;
    public int Height { get; set; } = 512;
    public int RequestedCount { get; set; } = 1;
    public int CompletedCount { get; set; } = 0;
    public long Seed { get; set; } = 123456;

    [MaxLength(100)]
    public string? ComfyPromptId { get; set; }
    public bool IsSimulated { get; set; } = true;

    [MaxLength(100)]
    public string? IdempotencyKey { get; set; }

    public DateTimeOffset RequestedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? LeaseUntilUtc { get; set; }
    public int AttemptCount { get; set; }

    public string? ErrorCode { get; set; }
    public string? ErrorSummary { get; set; }

    [Timestamp]
    public byte[]? RowVersion { get; set; }

    public ICollection<ImageAsset> OutputAssets { get; set; } = [];
}

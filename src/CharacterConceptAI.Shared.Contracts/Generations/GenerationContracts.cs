namespace CharacterConceptAI.Shared.Contracts.Generations;

public enum GenerationStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled,
    Interrupted
}

public enum PromptMode
{
    Raw,
    Structured,
    Hybrid
}

public record PromptPreviewRequest(
    Guid? VariantId = null,
    string? RawPrompt = null,
    string TemplateVersion = "1.0"
);

public record PromptPreviewDto(
    string RawPrompt,
    string StructuredPrompt,
    string HybridPrompt,
    IReadOnlyList<string> Warnings,
    string TemplateVersion,
    string SnapshotJson
);

public record CreateGenerationRequest(
    Guid CharacterId,
    Guid? VariantId,
    PromptMode PromptMode,
    string? RawPrompt,
    string? FinalPrompt,
    int Width,
    int Height,
    int RequestedCount,
    long? Seed,
    string? IdempotencyKey,
    string? PromptTemplateVersion = "1.0",
    Guid? ReferenceConceptVersionId = null,
    double? Denoise = null,
    bool FullQuality = false,
    string? DepthPoseId = null,
    double? DepthStrength = null,
    Guid? FaceReferenceConceptVersionId = null,
    double? FaceSwapStrength = null,
    double? FaceRestoreStrength = null,
    IReadOnlyList<Guid>? AdditionalFaceReferenceConceptVersionIds = null,
    string? OutfitId = null,
    Guid? SecondaryFaceReferenceConceptVersionId = null,
    double? SecondaryFaceSwapStrength = null,
    double? SecondaryFaceRestoreStrength = null,
    IReadOnlyList<Guid>? SecondaryAdditionalFaceReferenceConceptVersionIds = null,
    IReadOnlyList<LoraSelectionDto>? Loras = null
);

public record CancelJobResponse(
    Guid JobId,
    GenerationStatus Status,
    string Message
);

public record RetryJobResponse(
    Guid JobId,
    GenerationStatus Status,
    string Message
);

public record GenerationJobDto(
    Guid Id,
    Guid ProjectId,
    Guid CharacterId,
    Guid? VariantId,
    string RequestedByUserId,
    GenerationStatus Status,
    PromptMode PromptMode,
    string FinalPrompt,
    int Width,
    int Height,
    int RequestedCount,
    int CompletedCount,
    long Seed,
    string? ComfyPromptId,
    bool IsSimulated,
    DateTimeOffset RequestedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? ErrorSummary,
    IReadOnlyList<Guid> OutputAssetIds,
    string? RawPrompt = null,
    string? ErrorCode = null,
    string? PromptTemplateVersion = null,
    Guid? ReferenceConceptVersionId = null,
    double? Denoise = null,
    string? InputAssetSha256 = null,
    string? WorkflowKey = null,
    bool FullQuality = false,
    string? DepthPoseId = null,
    Guid? FaceReferenceConceptVersionId = null,
    IReadOnlyList<Guid>? AdditionalFaceReferenceConceptVersionIds = null
);

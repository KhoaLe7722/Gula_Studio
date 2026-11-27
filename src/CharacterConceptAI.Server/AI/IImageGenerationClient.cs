using CharacterConceptAI.Shared.Contracts.Generations;

namespace CharacterConceptAI.Server.AI;

public interface IImageGenerationClient
{
    bool IsSimulated { get; }
    Task<GenerationExecutionResult> GenerateAsync(GenerationExecutionRequest request, CancellationToken ct = default);
}

public record GenerationExecutionRequest(
    Guid JobId,
    string Prompt,
    int Width,
    int Height,
    long Seed,
    string? ReferenceImagePath = null,
    double? Denoise = null,
    byte[]? ReferenceImageBytes = null,
    bool FullQuality = false,
    bool RefineFace = false,
    byte[]? DepthPoseImageBytes = null,
    double? DepthStrength = null,
    byte[]? FaceReferenceImageBytes = null,
    double? FaceSwapStrength = null,
    double? FaceRestoreStrength = null,
    IReadOnlyList<byte[]>? AdditionalFaceReferenceImageBytes = null,
    byte[]? SecondaryFaceReferenceImageBytes = null,
    double? SecondaryFaceSwapStrength = null,
    double? SecondaryFaceRestoreStrength = null,
    IReadOnlyList<byte[]>? SecondaryAdditionalFaceReferenceImageBytes = null,
    IReadOnlyList<LoraSelectionDto>? Loras = null
);

public record GenerationExecutionResult(
    bool Success,
    IReadOnlyList<GeneratedImageOutput> Outputs,
    string? ProviderPromptId,
    string? ErrorMessage,
    TimeSpan Elapsed
);

public record GeneratedImageOutput(
    byte[] ImageBytes,
    string Extension,
    int Width,
    int Height
);

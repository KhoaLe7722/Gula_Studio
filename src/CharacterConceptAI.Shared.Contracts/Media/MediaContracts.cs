namespace CharacterConceptAI.Shared.Contracts.Media;

public record ImageAssetDto(
    Guid Id,
    Guid ProjectId,
    string ContentType,
    long SizeBytes,
    string Sha256,
    int Width,
    int Height,
    DateTimeOffset CreatedAt,
    string StreamUrl
);

using CharacterConceptAI.Shared.Contracts.Media;

namespace CharacterConceptAI.Shared.Contracts.Concepts;

public enum ConceptStatus
{
    Draft = 0,
    UnderReview = 1,
    Approved = 2,
    Rejected = 3,
    Archived = 4
}

public enum ConceptVersionStatus
{
    Draft = 0,
    Submitted = 1,
    Approved = 2,
    Rejected = 3
}

public enum ConceptApprovalDecision
{
    Approved = 1,
    Rejected = 2
}

public record CharacterConceptDto(
    Guid Id,
    Guid ProjectId,
    Guid CharacterId,
    string CharacterName,
    Guid? VariantId,
    string? VariantName,
    string Title,
    string? Description,
    ConceptStatus Status,
    Guid? RepresentativeVersionId,
    Guid? RepresentativeAssetId,
    string? RepresentativeStreamUrl,
    int VersionCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    string? RowVersion
);

public record ConceptVersionDto(
    Guid Id,
    Guid ConceptId,
    int VersionNumber,
    Guid? ParentVersionId,
    Guid? GenerationJobId,
    Guid ImageAssetId,
    string StreamUrl,
    string FinalPromptSnapshot,
    string? ParametersSnapshotJson,
    ConceptVersionStatus Status,
    string CreatedByUserId,
    DateTimeOffset CreatedAt,
    List<ConceptApprovalDto> Approvals
);

public record ConceptDetailDto(
    CharacterConceptDto Concept,
    List<ConceptVersionDto> Versions
);

public record ConceptApprovalDto(
    Guid Id,
    Guid ConceptVersionId,
    string ReviewerUserId,
    string? ReviewerName,
    ConceptApprovalDecision Decision,
    string? Comment,
    DateTimeOffset ReviewedAt
);

public record CreateConceptRequest(
    Guid? VariantId,
    string Title,
    string? Description,
    Guid GenerationJobId,
    Guid ImageAssetId
);

public record CreateConceptVersionRequest(
    Guid GenerationJobId,
    Guid ImageAssetId,
    Guid? ParentVersionId,
    string? Note
);

public record ReviewConceptVersionRequest(
    ConceptApprovalDecision Decision,
    string? Comment
);

public record SetRepresentativeVersionRequest(
    Guid VersionId
);

public record ConceptVersionComparisonItemDto(
    Guid VersionId,
    int VersionNumber,
    Guid ImageAssetId,
    string StreamUrl,
    int Width,
    int Height,
    long SizeBytes,
    string Sha256,
    string FinalPrompt,
    string? ParametersJson,
    ConceptVersionStatus Status,
    DateTimeOffset CreatedAt,
    List<ConceptApprovalDto> Approvals
);

public record ConceptComparisonDto(
    Guid ConceptId,
    string ConceptTitle,
    ConceptVersionComparisonItemDto Left,
    ConceptVersionComparisonItemDto Right
);

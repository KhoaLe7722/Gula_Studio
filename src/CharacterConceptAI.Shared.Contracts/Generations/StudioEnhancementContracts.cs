namespace CharacterConceptAI.Shared.Contracts.Generations;

public sealed record EnhancePromptRequest(
    string Prompt,
    string? StylePreset = null,
    string? OverlayText = null,
    string? OverlayTextStyle = null,
    string? OverlayTextPosition = null,
    int Width = 1024,
    int Height = 1024,
    bool EnableReasoning = false
);

public sealed record EnhancePromptResponse(
    string EnhancedPrompt,
    string Provider,
    IReadOnlyList<string> AppliedNotes
);

public sealed record UploadReferenceResponse(
    Guid AssetId,
    Guid ConceptVersionId,
    int Width,
    int Height
);

public sealed record PoseGalleryItemDto(
    string Id,
    string GroupKey,
    string PoseCode,
    string SystemKey,
    string SystemLabelVi,
    string CategoryKey,
    string CategoryLabelVi,
    string NameVi,
    string NameOriginal,
    string AngleKey,
    string AngleLabelVi,
    bool IsNsfw,
    string ThumbUrl,
    string DepthThumbUrl
);

public sealed record PoseGalleryGroupDto(
    string GroupKey,
    string PoseCode,
    string SystemKey,
    string SystemLabelVi,
    string CategoryKey,
    string CategoryLabelVi,
    string NameVi,
    bool IsNsfw,
    IReadOnlyList<PoseGalleryItemDto> Angles,
    int QualityScore = 90,
    string QualityBadge = ""
);

public sealed record PoseGalleryFilterOption(
    string Key,
    string Label,
    int Count,
    bool IsNsfw = false
);

public sealed record PoseGalleryResponse(
    int TotalPoses,
    int TotalGroups,
    int FilteredGroups,
    int Page,
    int PageSize,
    bool IncludeNsfw,
    bool IsAvailable,
    IReadOnlyList<PoseGalleryFilterOption> Systems,
    IReadOnlyList<PoseGalleryFilterOption> Categories,
    IReadOnlyList<PoseGalleryFilterOption> Angles,
    IReadOnlyList<PoseGalleryGroupDto> Groups
);

public sealed record OutfitGalleryViewDto(
    string ViewKey,
    string ViewLabelVi,
    string ThumbUrl
);

public sealed record OutfitGalleryItemDto(
    string Id,
    string Code,
    string CollectionKey,
    string CollectionLabelVi,
    string CategoryKey,
    string CategoryLabelVi,
    string NameVi,
    string NameOriginal,
    string PromptEn,
    IReadOnlyList<string> TagsVi,
    bool IsNsfw,
    int QualityScore,
    string QualityBadge,
    string ThumbUrl,
    IReadOnlyList<OutfitGalleryViewDto> Views,
    string GenderKey = "female",
    string GenderLabelVi = "Nữ"
);

public sealed record OutfitGalleryResponse(
    int TotalOutfits,
    int FilteredOutfits,
    int Page,
    int PageSize,
    bool IncludeNsfw,
    bool IsAvailable,
    IReadOnlyList<PoseGalleryFilterOption> Collections,
    IReadOnlyList<PoseGalleryFilterOption> Categories,
    IReadOnlyList<OutfitGalleryItemDto> Items,
    IReadOnlyList<PoseGalleryFilterOption>? Genders = null
);

public sealed record LoraSelectionDto(
    string FileName,
    double Strength = 0.45
);

public sealed record LoraCatalogItemDto(
    string FileName,
    string DisplayNameVi,
    string DescriptionVi,
    string Category,
    double DefaultStrength,
    long SizeBytes,
    bool IsRecommended = false
);

public sealed record LoraCatalogResponse(
    IReadOnlyList<LoraCatalogItemDto> Items,
    string LorasFolder
);

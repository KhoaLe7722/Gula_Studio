using System;
using System.Collections.Generic;

namespace CharacterConceptAI.Shared.Contracts.Characters;

// ==========================================
// 1. Core Characters & Attributes
// ==========================================
public record CharacterDto(
    Guid Id,
    Guid ProjectId,
    string Name,
    string Role,
    string? Description,
    string Status,
    IReadOnlyList<CharacterAttributeValueDto> Attributes,
    IReadOnlyList<CharacterVariantDto> Variants,
    string? RowVersion
);

public record CharacterAttributeValueDto(
    Guid DefinitionId,
    string Code,
    string DisplayName,
    string DataType,
    string? ValueJson,
    bool IsIdentityCritical = false,
    bool IsOverridden = false
);

public record AttributeDefinitionDto(
    Guid Id,
    Guid? ProjectId,
    string Code,
    string DisplayName,
    string DataType,
    string? AllowedValuesJson,
    bool IsRequired,
    bool IsIdentityCritical
);

public record CreateAttributeDefinitionRequest(
    string Code,
    string DisplayName,
    string DataType, // Text, Number, Boolean, SingleSelect, MultiSelect
    string? AllowedValuesJson = null,
    bool IsRequired = false,
    bool IsIdentityCritical = false
);

public record UpdateAttributeDefinitionRequest(
    string DisplayName,
    string? AllowedValuesJson = null,
    bool IsRequired = false,
    bool IsIdentityCritical = false
);

public record CreateCharacterRequest(
    string Name,
    string Role,
    string? Description,
    Dictionary<string, string?>? InitialAttributes = null
);

public record UpdateCharacterRequest(
    string Name,
    string Role,
    string? Description,
    string Version
);

public record SetCharacterAttributesRequest(
    Dictionary<string, string?> Attributes
);

// ==========================================
// 2. Character Variants (Override Base Attributes)
// ==========================================
public record CharacterVariantDto(
    Guid Id,
    Guid CharacterId,
    string Name,
    string? Description,
    bool IsDefault,
    string Status,
    string? RowVersion,
    IReadOnlyList<CharacterAttributeValueDto>? EffectiveAttributes = null
);

public record CreateVariantRequest(
    string Name,
    string? Description,
    bool IsDefault = false,
    Dictionary<string, string?>? AttributeOverrides = null
);

public record UpdateVariantRequest(
    string Name,
    string? Description,
    string Version
);

public record SetVariantAttributesRequest(
    Dictionary<string, string?> AttributeOverrides
);

// ==========================================
// 3. Append-Only Character Revisions & Diff
// ==========================================
public record CharacterRevisionDto(
    Guid Id,
    Guid CharacterId,
    Guid? VariantId,
    int RevisionNumber,
    string SnapshotJson,
    Guid? PreviousRevisionId,
    string? ActorUserId,
    DateTimeOffset CreatedAt,
    string? ChangeSummary
);

public record CharacterRevisionDiffDto(
    Guid FromRevisionId,
    Guid ToRevisionId,
    int FromRevisionNumber,
    int ToRevisionNumber,
    IReadOnlyList<RevisionFieldDiff> Changes
);

public record RevisionFieldDiff(
    string FieldPath,
    string? OldValue,
    string? NewValue,
    string ChangeType // Added, Modified, Removed
);

// ==========================================
// 4. Character Templates & Archetypes
// ==========================================
public record CharacterTemplateDto(
    Guid Id,
    Guid? ProjectId,
    string Name,
    string? Description,
    DateTimeOffset CreatedAt,
    bool IsArchived,
    IReadOnlyList<TemplateAttributeItemDto> Attributes
);

public record TemplateAttributeItemDto(
    Guid DefinitionId,
    string Code,
    string DisplayName,
    string DataType,
    string? DefaultValueJson
);

public record CreateTemplateRequest(
    string Name,
    string? Description,
    IReadOnlyList<CreateTemplateAttributeItemRequest> Attributes
);

public record CreateTemplateAttributeItemRequest(
    Guid DefinitionId,
    string? DefaultValueJson = null
);

public record ApplyTemplateRequest(
    Guid TemplateId,
    bool OverwriteExisting = false
);

public record ApplyTemplatePreviewDto(
    Guid CharacterId,
    Guid TemplateId,
    string TemplateName,
    IReadOnlyList<TemplateApplyPreviewItemDto> PreviewItems
);

public record TemplateApplyPreviewItemDto(
    string Code,
    string DisplayName,
    string? CurrentValue,
    string? NewValue,
    bool WillChange
);

// ==========================================
// 5. Project-Scoped Character Relationships
// ==========================================
public record CharacterRelationshipDto(
    Guid Id,
    Guid ProjectId,
    Guid SourceCharacterId,
    string SourceCharacterName,
    Guid TargetCharacterId,
    string TargetCharacterName,
    string Type,
    string? Notes,
    string? RowVersion
);

public record CreateCharacterRelationshipRequest(
    Guid SourceCharacterId,
    Guid TargetCharacterId,
    string Type,
    string? Notes = null
);

public record UpdateCharacterRelationshipRequest(
    string Type,
    string? Notes,
    string Version
);

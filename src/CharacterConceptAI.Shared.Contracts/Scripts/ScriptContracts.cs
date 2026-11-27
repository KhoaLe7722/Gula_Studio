using System;
using System.Collections.Generic;

namespace CharacterConceptAI.Shared.Contracts.Scripts;

public record ScriptDto(
    Guid Id,
    Guid ProjectId,
    string Title,
    string? Summary,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    string? RowVersion,
    int SceneCount = 0
);

public record CreateScriptRequest(
    string Title,
    string? Summary
);

public record UpdateScriptRequest(
    string Title,
    string? Summary,
    string Version
);

public record SceneDto(
    Guid Id,
    Guid ScriptId,
    int OrderIndex,
    string Title,
    string? Setting,
    string? Content,
    IReadOnlyList<Guid> LinkedCharacterIds,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    string? RowVersion
);

public record CreateSceneRequest(
    string Title,
    string? Setting,
    string? Content,
    int? OrderIndex = null,
    IReadOnlyList<Guid>? CharacterIds = null
);

public record UpdateSceneRequest(
    string Title,
    string? Setting,
    string? Content,
    string Version
);

public record SceneOrderItem(
    Guid SceneId,
    int OrderIndex
);

public record ReorderScenesRequest(
    IReadOnlyList<SceneOrderItem> Items
);

public record UpdateSceneCharactersRequest(
    IReadOnlyList<Guid> CharacterIds
);

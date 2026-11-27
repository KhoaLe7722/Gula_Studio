using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace CharacterConceptAI.Server.Data.Entities;

public class Scene
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ScriptId { get; set; }
    public Script Script { get; set; } = null!;

    public int OrderIndex { get; set; }

    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string? Setting { get; set; }

    [MaxLength(10000)]
    public string? Content { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }

    [Timestamp]
    public byte[]? RowVersion { get; set; }

    public ICollection<SceneCharacter> SceneCharacters { get; set; } = [];
}

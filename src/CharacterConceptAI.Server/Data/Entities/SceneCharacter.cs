using System;
using System.ComponentModel.DataAnnotations;

namespace CharacterConceptAI.Server.Data.Entities;

public class SceneCharacter
{
    public Guid SceneId { get; set; }
    public Scene Scene { get; set; } = null!;

    public Guid CharacterId { get; set; }
    public Character Character { get; set; } = null!;

    [MaxLength(500)]
    public string? Notes { get; set; }
}

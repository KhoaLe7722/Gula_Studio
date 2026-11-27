using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace CharacterConceptAI.Server.Data.Entities;

public class Script
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string? Summary { get; set; }

    [MaxLength(50)]
    public string Status { get; set; } = "Active"; // Active, Archived

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }

    [Timestamp]
    public byte[]? RowVersion { get; set; }

    public ICollection<Scene> Scenes { get; set; } = [];
}

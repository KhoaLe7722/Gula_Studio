using System.ComponentModel.DataAnnotations;

namespace CharacterConceptAI.Server.Data.Entities;

public class ImageAsset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public Guid? GenerationJobId { get; set; }
    public GenerationJob? GenerationJob { get; set; }

    [MaxLength(300)]
    public string StorageKey { get; set; } = string.Empty;
    [MaxLength(100)]
    public string ContentType { get; set; } = "image/png";
    public long SizeBytes { get; set; }
    [MaxLength(64)]
    public string Sha256 { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

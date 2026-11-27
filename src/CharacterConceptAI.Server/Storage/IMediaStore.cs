namespace CharacterConceptAI.Server.Storage;

public interface IMediaStore
{
    Task<StoredMediaResult> SaveAsync(Guid projectId, Stream content, string extension, CancellationToken ct = default);
    Task<Stream?> OpenReadAsync(string storageKey, CancellationToken ct = default);
    Task DeleteAsync(string storageKey);
    string GetFullPath(string storageKey);
}

public record StoredMediaResult(
    string StorageKey,
    long SizeBytes,
    string Sha256,
    int Width,
    int Height,
    string ContentType
);

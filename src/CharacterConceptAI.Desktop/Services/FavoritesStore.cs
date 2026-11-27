using System.IO;
using System.Text.Json;

namespace CharacterConceptAI.Desktop.Services;

public sealed record FavoriteImageRecord(
    string Key,
    Guid? AssetId,
    long? Seed,
    int Width,
    int Height,
    string PromptSnapshot,
    string? LocalImagePath,
    string? UpscaledImagePath,
    bool IsFullQuality,
    DateTimeOffset CreatedAt,
    DateTimeOffset FavoritedAt);

/// <summary>
/// Independent persistent store for user-favorited images (%LOCALAPPDATA%\CharacterConceptAI\favorites.json).
/// Decoupled from chat-history.json so deleting chat threads, exceeding the 80-thread history limit,
/// or resetting chat history can never erase or corrupt the user's Favorites collection.
/// </summary>
public static class FavoritesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly object SaveLock = new();

    public static string DefaultRootDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CharacterConceptAI");

    public static string GetFavoritesFilePath(string? rootDir = null)
        => Path.Combine(string.IsNullOrWhiteSpace(rootDir) ? DefaultRootDir : rootDir, "favorites.json");

    public static bool ExistsOnDisk(string? rootDir = null)
        => File.Exists(GetFavoritesFilePath(rootDir));

    /// <summary>
    /// Builds a deterministic, case-insensitive identity key for an image.
    /// Prefers normalized full local file path (so original and _inpaint/_upscale variants remain distinct),
    /// falling back to server AssetId when no local file path has been saved yet.
    /// </summary>
    public static string BuildKey(string? localImagePath, Guid? assetId)
    {
        if (!string.IsNullOrWhiteSpace(localImagePath))
        {
            try
            {
                return Path.GetFullPath(localImagePath.Trim()).ToLowerInvariant();
            }
            catch
            {
                return localImagePath.Trim().ToLowerInvariant();
            }
        }

        if (assetId is Guid id && id != Guid.Empty)
            return $"asset:{id:N}";

        return string.Empty;
    }

    public static List<FavoriteImageRecord> Load(string? rootDir = null)
    {
        var path = GetFavoritesFilePath(rootDir);
        try
        {
            if (!File.Exists(path)) return [];
            var json = File.ReadAllText(path);
            var raw = JsonSerializer.Deserialize<List<FavoriteImageRecord>>(json, JsonOptions) ?? [];
            return NormalizeAndDeduplicate(raw);
        }
        catch
        {
            TryQuarantineCorruptFile(path);
            return NormalizeAndDeduplicate(TryLoadBackup(path) ?? []);
        }
    }

    public static void Save(IEnumerable<FavoriteImageRecord> favorites, string? rootDir = null)
    {
        lock (SaveLock)
        {
            var dir = string.IsNullOrWhiteSpace(rootDir) ? DefaultRootDir : rootDir;
            var path = GetFavoritesFilePath(dir);
            string? tempFile = null;
            try
            {
                Directory.CreateDirectory(dir);
                var normalized = NormalizeAndDeduplicate(favorites);
                tempFile = $"{path}.{Guid.NewGuid():N}.tmp";
                File.WriteAllText(tempFile, JsonSerializer.Serialize(normalized, JsonOptions));

                // Never overwrite a non-empty .bak with an empty "[]" snapshot.
                if (File.Exists(path) && new FileInfo(path).Length > 4)
                    try { File.Copy(path, path + ".bak", overwrite: true); } catch { }

                File.Move(tempFile, path, overwrite: true);
                tempFile = null;
            }
            catch
            {
                // Keep UI resilient even if local disk write fails.
            }
            finally
            {
                if (tempFile != null) try { File.Delete(tempFile); } catch { }
            }
        }
    }

    private static List<FavoriteImageRecord> NormalizeAndDeduplicate(IEnumerable<FavoriteImageRecord> source)
    {
        var byKey = new Dictionary<string, FavoriteImageRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in source)
        {
            if (item == null) continue;
            var key = !string.IsNullOrWhiteSpace(item.Key)
                ? item.Key.Trim().ToLowerInvariant()
                : BuildKey(item.LocalImagePath, item.AssetId);
            if (string.IsNullOrWhiteSpace(key)) continue;

            var normalized = item with { Key = key };
            if (!byKey.TryGetValue(key, out var existing) || normalized.FavoritedAt >= existing.FavoritedAt)
                byKey[key] = normalized;
        }

        return byKey.Values
            .OrderByDescending(r => r.FavoritedAt)
            .ThenByDescending(r => r.CreatedAt)
            .ToList();
    }

    private static void TryQuarantineCorruptFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Copy(path, $"{path}.corrupt.{DateTime.Now:yyyyMMdd_HHmmss}.bak", overwrite: true);
        }
        catch { }
    }

    private static List<FavoriteImageRecord>? TryLoadBackup(string path)
    {
        try
        {
            var bak = path + ".bak";
            if (!File.Exists(bak)) return null;
            return JsonSerializer.Deserialize<List<FavoriteImageRecord>>(File.ReadAllText(bak), JsonOptions);
        }
        catch
        {
            return null;
        }
    }
}

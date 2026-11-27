using System.IO;
using System.Security.Cryptography;
using System.Text;
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
    DateTimeOffset FavoritedAt,
    string? FavoriteImagePath = null,
    string? FavoriteUpscaledImagePath = null);

/// <summary>
/// Independent persistent store + physical image vault for user-favorited images:
/// - Metadata: %LOCALAPPDATA%\CharacterConceptAI\favorites.json (+ safe .bak)
/// - Copies:   %LOCALAPPDATA%\CharacterConceptAI\favorite-images\
///
/// Favoriting an image creates a true physical copy inside favorite-images\, completely decoupled
/// from chat-history.json and chat-images\. Deleting chat threads, clearing chat history, or even
/// deleting original chat image files on disk can never break or erase favorited images.
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

    public static string GetImagesDir(string? rootDir = null)
        => Path.Combine(string.IsNullOrWhiteSpace(rootDir) ? DefaultRootDir : rootDir, "favorite-images");

    public static bool ExistsOnDisk(string? rootDir = null)
        => File.Exists(GetFavoritesFilePath(rootDir));

    /// <summary>
    /// Builds a deterministic, case-insensitive identity key for matching an image across Chat and Favorites.
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

    private static string ComputeStableToken(string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key.Trim().ToLowerInvariant()));
        return Convert.ToHexString(hash)[..24].ToLowerInvariant();
    }

    /// <summary>
    /// Creates an independent physical copy of an image file inside favorite-images\.
    /// Returns the vault file path, or null if the source file does not exist or cannot be copied.
    /// </summary>
    public static string? CopyFileIntoVault(string key, string? sourcePath, string suffix = "", string? rootDir = null)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(sourcePath)) return null;
        try
        {
            var fullSrc = Path.GetFullPath(sourcePath.Trim());
            if (!File.Exists(fullSrc)) return null;

            var vaultDir = Path.GetFullPath(GetImagesDir(rootDir));
            Directory.CreateDirectory(vaultDir);

            // Already inside the favorites vault
            if (fullSrc.StartsWith(vaultDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return fullSrc;

            var ext = Path.GetExtension(fullSrc);
            if (string.IsNullOrWhiteSpace(ext) || ext.Length > 6) ext = ".png";
            var token = ComputeStableToken(key);
            var fileName = string.IsNullOrWhiteSpace(suffix) ? $"fav_{token}{ext}" : $"fav_{token}_{suffix}{ext}";
            var destPath = Path.Combine(vaultDir, fileName);

            File.Copy(fullSrc, destPath, overwrite: true);
            return destPath;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Writes raw PNG/JPEG bytes as an independent physical copy inside favorite-images\.
    /// Used when an image is favorited directly from RAM before a chat-images file exists on disk.
    /// </summary>
    public static string? SaveBytesIntoVault(string key, byte[]? bytes, string suffix = "", string? rootDir = null)
    {
        if (string.IsNullOrWhiteSpace(key) || bytes is not { Length: > 0 }) return null;
        try
        {
            var vaultDir = Path.GetFullPath(GetImagesDir(rootDir));
            Directory.CreateDirectory(vaultDir);

            var token = ComputeStableToken(key);
            var fileName = string.IsNullOrWhiteSpace(suffix) ? $"fav_{token}.png" : $"fav_{token}_{suffix}.png";
            var destPath = Path.Combine(vaultDir, fileName);

            var tempPath = $"{destPath}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllBytes(tempPath, bytes);
                File.Move(tempPath, destPath, overwrite: true);
                tempPath = null;
                return destPath;
            }
            finally
            {
                if (tempPath != null) try { File.Delete(tempPath); } catch { }
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Safely deletes a file ONLY if it resides inside favorite-images\. Never touches chat-images\.
    /// </summary>
    public static void DeleteVaultFileSafe(string? path, string? rootDir = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var fullPath = Path.GetFullPath(path.Trim());
            var vaultDir = Path.GetFullPath(GetImagesDir(rootDir));
            if (!fullPath.StartsWith(vaultDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return;
            if (File.Exists(fullPath))
                File.Delete(fullPath);
        }
        catch { }
    }

    public static List<FavoriteImageRecord> Load(string? rootDir = null)
    {
        var path = GetFavoritesFilePath(rootDir);
        List<FavoriteImageRecord> loaded;
        try
        {
            if (!File.Exists(path)) return [];
            var json = File.ReadAllText(path);
            var raw = JsonSerializer.Deserialize<List<FavoriteImageRecord>>(json, JsonOptions) ?? [];
            loaded = NormalizeAndDeduplicate(raw);
        }
        catch
        {
            TryQuarantineCorruptFile(path);
            loaded = NormalizeAndDeduplicate(TryLoadBackup(path) ?? []);
        }

        // Self-heal legacy pointer-only favorite entries by backfilling physical copies into favorite-images\
        var upgraded = false;
        var result = new List<FavoriteImageRecord>(loaded.Count);
        foreach (var rec in loaded)
        {
            var favImg = rec.FavoriteImagePath;
            var favUp = rec.FavoriteUpscaledImagePath;

            if ((string.IsNullOrWhiteSpace(favImg) || !File.Exists(favImg)) &&
                !string.IsNullOrWhiteSpace(rec.LocalImagePath) && File.Exists(rec.LocalImagePath))
            {
                var copied = CopyFileIntoVault(rec.Key, rec.LocalImagePath, "", rootDir);
                if (!string.IsNullOrWhiteSpace(copied))
                {
                    favImg = copied;
                    upgraded = true;
                }
            }

            if ((string.IsNullOrWhiteSpace(favUp) || !File.Exists(favUp)) &&
                !string.IsNullOrWhiteSpace(rec.UpscaledImagePath) && File.Exists(rec.UpscaledImagePath))
            {
                var copiedUp = CopyFileIntoVault(rec.Key, rec.UpscaledImagePath, "2x", rootDir);
                if (!string.IsNullOrWhiteSpace(copiedUp))
                {
                    favUp = copiedUp;
                    upgraded = true;
                }
            }

            result.Add(rec with
            {
                FavoriteImagePath = favImg,
                FavoriteUpscaledImagePath = favUp
            });
        }

        if (upgraded)
            Save(result, rootDir);

        return result;
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
                : BuildKey(item.FavoriteImagePath ?? item.LocalImagePath, item.AssetId);
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

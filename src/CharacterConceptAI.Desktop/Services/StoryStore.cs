using System.IO;
using System.Text.Json;

namespace CharacterConceptAI.Desktop.Services;

public sealed record StoryFaceAngleRecord(
    Guid Id,
    string AngleLabel,
    string? ImagePath,
    Guid? ConceptVersionId,
    Guid? UploadedProjectId = null);

public sealed record StoryCharacterRecord(
    Guid Id,
    string Name,
    string VisualDna,
    string? FaceImagePath,
    Guid? FaceConceptVersionId,
    bool EnableFaceLock = true,
    double FaceSwapStrength = 0.90,
    double FaceRestoreStrength = 0.40,
    long? AnchorSeed = null,
    List<StoryFaceAngleRecord>? ExtraFaceAngles = null,
    string BodyDna = "",
    bool EnableBodyDnaLock = true,
    Guid? FaceUploadedProjectId = null);

public sealed record StorySceneRecord(
    Guid Id,
    int OrderIndex,
    string Title,
    Guid? CharacterId,
    string ScenePrompt,
    string Dialogue,
    string? DepthPoseId,
    string? DepthPoseLabel,
    double DepthStrength,
    long? Seed,
    Guid? OutputAssetId,
    string? LocalImagePath,
    string? UpscaledImagePath,
    int Width,
    int Height,
    string StatusText,
    DateTimeOffset? GeneratedAt,
    string? OutfitId = null,
    string? OutfitLabel = null,
    Guid? SecondaryCharacterId = null);

public sealed record StoryProjectRecord(
    Guid Id,
    string Title,
    string StylePresetKey,
    string AspectRatioKey,
    int Width,
    int Height,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    List<StoryCharacterRecord> Characters,
    List<StorySceneRecord> Scenes);

public static class StoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static string RootDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CharacterConceptAI");

    private static string StoriesFile => Path.Combine(RootDir, "stories.json");
    private static string StoryImagesDir => Path.Combine(RootDir, "story-images");

    public static bool LastLoadHadCorruptError { get; private set; }

    public static List<StoryProjectRecord> Load()
    {
        LastLoadHadCorruptError = false;
        try
        {
            if (!File.Exists(StoriesFile)) return [];
            var json = File.ReadAllText(StoriesFile);
            return JsonSerializer.Deserialize<List<StoryProjectRecord>>(json, JsonOptions) ?? [];
        }
        catch
        {
            // Preserve the unreadable file so we NEVER silently overwrite user's story roster!
            try
            {
                if (File.Exists(StoriesFile))
                    File.Copy(StoriesFile, $"{StoriesFile}.corrupt.{DateTime.Now:yyyyMMdd_HHmmss}.bak", overwrite: true);
            }
            catch { }
            try
            {
                var bak = StoriesFile + ".bak";
                if (File.Exists(bak))
                {
                    var recovered = JsonSerializer.Deserialize<List<StoryProjectRecord>>(File.ReadAllText(bak), JsonOptions);
                    if (recovered is { Count: > 0 }) return recovered;
                }
            }
            catch { }
            LastLoadHadCorruptError = true;
            return [];
        }
    }

    private static readonly object SaveLock = new();
    private static readonly object DebounceLock = new();
    private static List<StoryProjectRecord>? _pendingStories;
    private static Timer? _saveTimer;

    /// <summary>
    /// Non-blocking debounced background save (400ms coalescing) — eliminates UI freezes &amp; 11x write storms.
    /// </summary>
    public static void SaveDebounced(IEnumerable<StoryProjectRecord> stories, int delayMs = 400)
    {
        var snapshot = stories.Take(60).ToList();
        lock (DebounceLock)
        {
            _pendingStories = snapshot;
            _saveTimer ??= new Timer(_ => FlushPendingSaves(), null, Timeout.Infinite, Timeout.Infinite);
            _saveTimer.Change(delayMs, Timeout.Infinite);
        }
    }

    public static void FlushPendingSaves()
    {
        List<StoryProjectRecord>? snapshot;
        lock (DebounceLock)
        {
            snapshot = _pendingStories;
            _pendingStories = null;
            _saveTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }
        if (snapshot is not null)
            Save(snapshot);
    }

    public static void Save(IEnumerable<StoryProjectRecord> stories)
    {
        lock (SaveLock)
        {
            string? tempFile = null;
            try
            {
                Directory.CreateDirectory(RootDir);
                var list = stories.Take(60).ToList();
                tempFile = $"{StoriesFile}.{Guid.NewGuid():N}.tmp";
                File.WriteAllText(tempFile, JsonSerializer.Serialize(list, JsonOptions));
                if (File.Exists(StoriesFile))
                    try { File.Copy(StoriesFile, StoriesFile + ".bak", overwrite: true); } catch { }
                File.Move(tempFile, StoriesFile, overwrite: true);
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

    public static string? SaveStoryImageBytes(Guid id, byte[] bytes, string suffix = "")
    {
        try
        {
            Directory.CreateDirectory(StoryImagesDir);
            var fileName = string.IsNullOrWhiteSpace(suffix) ? $"{id:N}.png" : $"{id:N}_{suffix}.png";
            var path = Path.Combine(StoryImagesDir, fileName);
            File.WriteAllBytes(path, bytes);
            return path;
        }
        catch
        {
            return null;
        }
    }

    public static Task<string?> SaveStoryImageBytesAsync(Guid id, byte[] bytes, string suffix = "")
        => Task.Run(() => SaveStoryImageBytes(id, bytes, suffix));
}

using System.IO;
using System.Text.Json;

namespace CharacterConceptAI.Desktop.Services;

public sealed record ChatTurnImageRecord(
    Guid? AssetId,
    long? Seed,
    int Width,
    int Height,
    string? LocalImagePath,
    string? UpscaledImagePath = null,
    bool IsFavorite = false);

public sealed record ChatTurnRecord(
    Guid Id,
    string UserPrompt,
    int Width,
    int Height,
    bool FullQuality,
    long? Seed,
    Guid? AssetId,
    string? LocalImagePath,
    string StatusText,
    string? ErrorMessage,
    DateTimeOffset CreatedAt,
    List<ChatTurnImageRecord>? Images = null,
    string? StylePreset = null,
    string? OverlayText = null);

public sealed record ChatThreadRecord(
    Guid Id,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    List<ChatTurnRecord> Turns);

public sealed record StudioPreferencesRecord(
    bool AutoExportEnabled = true,
    string AutoExportFolder = "",
    double UiFontScale = 1.0,
    bool AllowNsfwContent = false);

public static class ChatHistoryStore
{
    public static string DefaultAutoExportFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "GulaStudio");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static string RootDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CharacterConceptAI");

    private static string HistoryFile => Path.Combine(RootDir, "chat-history.json");
    private static string ImagesDir => Path.Combine(RootDir, "chat-images");
    private static string PrefsFile => Path.Combine(RootDir, "studio-prefs.json");

    public static List<ChatThreadRecord> Load()
    {
        try
        {
            if (!File.Exists(HistoryFile)) return [];
            var json = File.ReadAllText(HistoryFile);
            return JsonSerializer.Deserialize<List<ChatThreadRecord>>(json, JsonOptions) ?? [];
        }
        catch
        {
            TryQuarantineCorruptFile(HistoryFile);
            return TryLoadBackup<List<ChatThreadRecord>>(HistoryFile) ?? [];
        }
    }

    private static readonly object SaveLock = new();
    private static readonly object DebounceLock = new();
    private static List<ChatThreadRecord>? _pendingThreads;
    private static StudioPreferencesRecord? _pendingPrefs;
    private static Timer? _threadsTimer;
    private static Timer? _prefsTimer;

    /// <summary>
    /// Non-blocking debounced background save (400ms coalescing) — zero UI thread stalls.
    /// </summary>
    public static void SaveDebounced(IEnumerable<ChatThreadRecord> threads, int delayMs = 400)
    {
        var snapshot = threads.Take(80).ToList();
        lock (DebounceLock)
        {
            _pendingThreads = snapshot;
            _threadsTimer ??= new Timer(_ => FlushPendingThreads(), null, Timeout.Infinite, Timeout.Infinite);
            _threadsTimer.Change(delayMs, Timeout.Infinite);
        }
    }

    public static void SavePreferencesDebounced(StudioPreferencesRecord prefs, int delayMs = 400)
    {
        lock (DebounceLock)
        {
            _pendingPrefs = prefs;
            _prefsTimer ??= new Timer(_ => FlushPendingPrefs(), null, Timeout.Infinite, Timeout.Infinite);
            _prefsTimer.Change(delayMs, Timeout.Infinite);
        }
    }

    public static void FlushPendingSaves()
    {
        FlushPendingThreads();
        FlushPendingPrefs();
    }

    private static void FlushPendingThreads()
    {
        List<ChatThreadRecord>? snapshot;
        lock (DebounceLock)
        {
            snapshot = _pendingThreads;
            _pendingThreads = null;
            _threadsTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }
        if (snapshot is not null)
            Save(snapshot);
    }

    private static void FlushPendingPrefs()
    {
        StudioPreferencesRecord? snapshot;
        lock (DebounceLock)
        {
            snapshot = _pendingPrefs;
            _pendingPrefs = null;
            _prefsTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }
        if (snapshot is not null)
            SavePreferences(snapshot);
    }

    public static void Save(IEnumerable<ChatThreadRecord> threads)
    {
        lock (SaveLock)
        {
            string? tempFile = null;
            try
            {
                Directory.CreateDirectory(RootDir);
                var list = threads.Take(80).ToList();
                tempFile = $"{HistoryFile}.{Guid.NewGuid():N}.tmp";
                File.WriteAllText(tempFile, JsonSerializer.Serialize(list, JsonOptions));
                if (File.Exists(HistoryFile) && new FileInfo(HistoryFile).Length > 4)
                    try { File.Copy(HistoryFile, HistoryFile + ".bak", overwrite: true); } catch { }
                File.Move(tempFile, HistoryFile, overwrite: true);
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

    public static string? SaveImageBytes(Guid assetId, byte[] bytes, string suffix = "")
    {
        try
        {
            Directory.CreateDirectory(ImagesDir);
            var fileName = string.IsNullOrWhiteSpace(suffix) ? $"{assetId:N}.png" : $"{assetId:N}_{suffix}.png";
            var path = Path.Combine(ImagesDir, fileName);
            File.WriteAllBytes(path, bytes);
            return path;
        }
        catch
        {
            return null;
        }
    }

    public static Task<string?> SaveImageBytesAsync(Guid assetId, byte[] bytes, string suffix = "")
        => Task.Run(() => SaveImageBytes(assetId, bytes, suffix));

    public static void DeleteLocalFileSafe(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    public static StudioPreferencesRecord LoadPreferences()
    {
        try
        {
            if (!File.Exists(PrefsFile)) return new StudioPreferencesRecord();
            var json = File.ReadAllText(PrefsFile);
            return JsonSerializer.Deserialize<StudioPreferencesRecord>(json, JsonOptions) ?? new StudioPreferencesRecord();
        }
        catch
        {
            return TryLoadBackup<StudioPreferencesRecord>(PrefsFile) ?? new StudioPreferencesRecord();
        }
    }

    public static void SavePreferences(StudioPreferencesRecord prefs)
    {
        lock (SaveLock)
        {
            string? tempFile = null;
            try
            {
                Directory.CreateDirectory(RootDir);
                tempFile = $"{PrefsFile}.{Guid.NewGuid():N}.tmp";
                File.WriteAllText(tempFile, JsonSerializer.Serialize(prefs, JsonOptions));
                if (File.Exists(PrefsFile))
                    try { File.Copy(PrefsFile, PrefsFile + ".bak", overwrite: true); } catch { }
                File.Move(tempFile, PrefsFile, overwrite: true);
                tempFile = null;
            }
            catch { }
            finally
            {
                if (tempFile != null) try { File.Delete(tempFile); } catch { }
            }
        }
    }

    public static string? AutoExportImage(string folder, long seed, int width, int height, bool isUpscaled, byte[] bytes)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        try
        {
            Directory.CreateDirectory(folder);
            var tag = isUpscaled ? "_2xFull" : string.Empty;
            var baseName = $"GulaStudio_{DateTime.Now:yyyyMMdd_HHmmss}_s{seed}_{width}x{height}{tag}";
            var path = Path.Combine(folder, baseName + ".png");
            for (var n = 1; File.Exists(path) && n < 100; n++)
                path = Path.Combine(folder, $"{baseName}_{n}.png");
            File.WriteAllBytes(path, bytes);
            return path;
        }
        catch
        {
            return null;
        }
    }

    public static Task<string?> AutoExportImageAsync(string folder, long seed, int width, int height, bool isUpscaled, byte[] bytes)
        => Task.Run(() => AutoExportImage(folder, seed, width, height, isUpscaled, bytes));

    private static void TryQuarantineCorruptFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Copy(path, $"{path}.corrupt.{DateTime.Now:yyyyMMdd_HHmmss}.bak", overwrite: true);
        }
        catch { }
    }

    private static T? TryLoadBackup<T>(string path)
    {
        try
        {
            var bak = path + ".bak";
            if (!File.Exists(bak)) return default;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(bak), JsonOptions);
        }
        catch { return default; }
    }
}

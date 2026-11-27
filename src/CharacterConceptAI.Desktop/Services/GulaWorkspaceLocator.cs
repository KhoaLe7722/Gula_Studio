using System.IO;

namespace CharacterConceptAI.Desktop.Services;

/// <summary>
/// Portable workspace locator for Gula Studio AI runtimes &amp; models (ComfyUI, PromptEnhancer, SpeechToText).
/// Resolves user-configured path, GULA_STUDIO_HOME env var, or relative parent discovery — zero hardcoded developer paths.
/// </summary>
public static class GulaWorkspaceLocator
{
    private static readonly object Sync = new();
    private static string? _cachedRoot;

    private static string ConfigDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CharacterConceptAI");

    private static string WorkspaceConfigFile => Path.Combine(ConfigDir, "workspace-root.txt");

    public static string GetWorkspaceRoot()
    {
        lock (Sync)
        {
            if (!string.IsNullOrWhiteSpace(_cachedRoot))
                return _cachedRoot;

            _cachedRoot = ResolveWorkspaceRootCore();
            return _cachedRoot;
        }
    }

    public static void SetWorkspaceRoot(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return;
        var normalized = Path.GetFullPath(folderPath.Trim());
        Directory.CreateDirectory(normalized);
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(WorkspaceConfigFile, normalized);
        lock (Sync)
        {
            _cachedRoot = normalized;
        }
        VoiceInputService.InvalidateModelCache();
    }

    public static void InvalidateCache()
    {
        lock (Sync)
        {
            _cachedRoot = null;
        }
    }

    private static string ResolveWorkspaceRootCore()
    {
        // 1. Saved user preference from In-App Setup Manager
        try
        {
            if (File.Exists(WorkspaceConfigFile))
            {
                var saved = File.ReadAllText(WorkspaceConfigFile).Trim();
                if (!string.IsNullOrWhiteSpace(saved) && Directory.Exists(saved))
                    return Path.GetFullPath(saved);
            }
        }
        catch { }

        // 2. Explicit environment variable
        var envRoot = Environment.GetEnvironmentVariable("GULA_STUDIO_HOME");
        if (!string.IsNullOrWhiteSpace(envRoot))
            return Path.GetFullPath(envRoot.Trim());

        // 3. Walk up from executable base directory looking for an existing AI workspace (ComfyUI / PromptEnhancer / SpeechToText)
        var current = AppContext.BaseDirectory;
        for (var depth = 0; depth < 7 && !string.IsNullOrWhiteSpace(current); depth++)
        {
            if (LooksLikeAiWorkspace(current))
                return Path.GetFullPath(current);
            current = Directory.GetParent(current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))?.FullName;
        }

        // 4. Clean default portable location under %LOCALAPPDATA%\GulaStudio
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GulaStudio");
    }

    private static bool LooksLikeAiWorkspace(string dir)
    {
        return File.Exists(Path.Combine(dir, "ComfyUI", "main.py"))
            || File.Exists(Path.Combine(dir, "run_comfyui.bat"))
            || Directory.Exists(Path.Combine(dir, "PromptEnhancer", "models"))
            || Directory.Exists(Path.Combine(dir, "SpeechToText", "models"));
    }

    public static string? FindLocalServerExecutable()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new List<string>
        {
            Path.GetFullPath(Path.Combine(baseDir, "..", "server", "CharacterConceptAI.Server.exe")),
            Path.GetFullPath(Path.Combine(baseDir, "CharacterConceptAI.Server.exe")),
        };

        var current = baseDir;
        for (var depth = 0; depth < 7 && !string.IsNullOrWhiteSpace(current); depth++)
        {
            candidates.Add(Path.Combine(current, "dist", "redesign", "server", "CharacterConceptAI.Server.exe"));
            candidates.Add(Path.Combine(current, "src", "CharacterConceptAI.Server", "bin", "Release", "net10.0", "CharacterConceptAI.Server.exe"));
            candidates.Add(Path.Combine(current, "src", "CharacterConceptAI.Server", "bin", "Debug", "net10.0", "CharacterConceptAI.Server.exe"));
            current = Directory.GetParent(current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))?.FullName;
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    public static string? FindComfyLaunchBatch()
    {
        var ws = GetWorkspaceRoot();
        var direct = Path.Combine(ws, "run_comfyui.bat");
        if (File.Exists(direct)) return direct;

        var current = AppContext.BaseDirectory;
        for (var depth = 0; depth < 7 && !string.IsNullOrWhiteSpace(current); depth++)
        {
            var candidate = Path.Combine(current, "run_comfyui.bat");
            if (File.Exists(candidate)) return candidate;
            current = Directory.GetParent(current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))?.FullName;
        }

        return null;
    }

    public static string? FindBundledCustomNodesSource()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "BundledCustomNodes"),
            Path.Combine(AppContext.BaseDirectory, "scripts", "comfyui", "custom_nodes"),
        };

        var current = AppContext.BaseDirectory;
        for (var depth = 0; depth < 7 && !string.IsNullOrWhiteSpace(current); depth++)
        {
            candidates.Add(Path.Combine(current, "scripts", "comfyui", "custom_nodes"));
            candidates.Add(Path.Combine(current, "CharacterConceptAI", "scripts", "comfyui", "custom_nodes"));
            current = Directory.GetParent(current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))?.FullName;
        }

        return candidates.FirstOrDefault(d => File.Exists(Path.Combine(d, "cca_studio_pro", "__init__.py")));
    }

    public static string? FindComfyPythonExecutable(string workspaceRoot)
    {
        var candidates = new[]
        {
            Path.Combine(workspaceRoot, "python_embeded", "python.exe"),
            Path.Combine(workspaceRoot, "ComfyUI_windows_portable", "python_embeded", "python.exe"),
            Path.Combine(workspaceRoot, "ComfyUI", "venv", "Scripts", "python.exe"),
            Path.Combine(workspaceRoot, "ComfyUI", ".venv", "Scripts", "python.exe"),
        };
        var found = candidates.FirstOrDefault(File.Exists);
        if (found != null) return found;

        // Fall back to Python on PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var py = Path.Combine(dir.Trim(), "python.exe");
                if (File.Exists(py) && !py.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
                    return py;
            }
            catch { }
        }
        return null;
    }
}

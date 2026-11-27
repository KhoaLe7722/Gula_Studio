using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;

namespace CharacterConceptAI.Desktop.Infrastructure.Settings;

public enum AppLanguage
{
    Vi,
    En,
    Zh
}

/// <summary>
/// Hot-swaps localized ResourceDictionaries (Vietnamese / English / Simplified Chinese)
/// in real time without restarting or rebuilding the WPF window tree.
/// </summary>
public sealed class LocalizationService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter<AppLanguage>() }
    };

    private Application? _application;
    private bool _initialized;

    private LocalizationService() { }

    public static LocalizationService Instance { get; } = new();
    public AppLanguage CurrentLanguage { get; private set; } = AppLanguage.Vi;
    public event EventHandler? LanguageChanged;

    public string CurrentLanguageCode => CurrentLanguage switch
    {
        AppLanguage.En => "en",
        AppLanguage.Zh => "zh",
        _ => "vi"
    };

    public string ShortBadge => CurrentLanguage switch
    {
        AppLanguage.En => "EN",
        AppLanguage.Zh => "中文",
        _ => "VI"
    };

    public string CurrentDisplayName => CurrentLanguage switch
    {
        AppLanguage.En => "English",
        AppLanguage.Zh => "中文 (简体)",
        _ => "Tiếng Việt"
    };

    public void Initialize(Application? application = null, bool loadPersistedPreference = true)
    {
        if (_initialized) return;
        _application = application ?? Application.Current;
        if (_application == null) return;
        _application.Dispatcher.VerifyAccess();
        CurrentLanguage = loadPersistedPreference ? ReadPreference() : AppLanguage.Vi;
        ApplyDictionary();
        _application.Exit += OnApplicationExit;
        _initialized = true;
    }

    public void SetLanguage(AppLanguage language, bool persist = true)
    {
        if (!Enum.IsDefined(language)) throw new ArgumentOutOfRangeException(nameof(language));
        if (!_initialized) Initialize(Application.Current, loadPersistedPreference: false);
        CurrentLanguage = language;
        if (_application != null)
        {
            if (_application.Dispatcher.CheckAccess())
                ApplyDictionary();
            else
                _application.Dispatcher.Invoke(ApplyDictionary);
        }
        if (persist) SavePreference();
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetLanguageByCode(string? code, bool persist = true)
    {
        var lang = (code ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "en" or "english" => AppLanguage.En,
            "zh" or "cn" or "zh-cn" or "chinese" => AppLanguage.Zh,
            _ => AppLanguage.Vi
        };
        SetLanguage(lang, persist);
    }

    public void CycleLanguage()
    {
        var next = CurrentLanguage switch
        {
            AppLanguage.Vi => AppLanguage.En,
            AppLanguage.En => AppLanguage.Zh,
            _ => AppLanguage.Vi
        };
        SetLanguage(next, persist: true);
    }

    public string Get(string key, string fallback = "")
    {
        var app = _application ?? Application.Current;
        if (app?.TryFindResource(key) is string s && !string.IsNullOrEmpty(s))
            return s;
        return string.IsNullOrEmpty(fallback) ? key : fallback;
    }

    public string Format(string key, string fallback, params object[] args)
    {
        var template = Get(key, fallback);
        try { return string.Format(template, args); }
        catch { return template; }
    }

    private static string PreferencePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CharacterConceptAI", "desktop-language.json");

    private static AppLanguage ReadPreference()
    {
        try
        {
            if (!File.Exists(PreferencePath)) return AppLanguage.Vi;
            if (new FileInfo(PreferencePath).Length > 4096) return AppLanguage.Vi;
            var pref = JsonSerializer.Deserialize<LanguagePreference>(File.ReadAllText(PreferencePath), JsonOptions);
            return pref is not null && Enum.IsDefined(pref.Language) ? pref.Language : AppLanguage.Vi;
        }
        catch
        {
            return AppLanguage.Vi;
        }
    }

    private void SavePreference()
    {
        var tmp = PreferencePath + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PreferencePath)!);
            File.WriteAllText(tmp, JsonSerializer.Serialize(new LanguagePreference(CurrentLanguage), JsonOptions));
            File.Move(tmp, PreferencePath, overwrite: true);
        }
        catch { }
    }

    private void ApplyDictionary()
    {
        if (_application == null) return;
        var dict = new ResourceDictionary
        {
            Source = new Uri($"/CharacterConceptAI.Desktop;component/Theme/Strings/Strings.{CurrentLanguageCode}.xaml", UriKind.Relative)
        };
        var dictionaries = _application.Resources.MergedDictionaries;
        for (var i = 0; i < dictionaries.Count; i++)
        {
            if (dictionaries[i].Source?.OriginalString.Contains("/Strings.", StringComparison.OrdinalIgnoreCase) != true)
                continue;
            dictionaries[i] = dict;
            return;
        }
        dictionaries.Insert(Math.Min(1, dictionaries.Count), dict);
    }

    private void OnApplicationExit(object sender, ExitEventArgs e)
    {
        if (_application != null) _application.Exit -= OnApplicationExit;
        _application = null;
        _initialized = false;
    }

    private sealed record LanguagePreference(AppLanguage Language);
}

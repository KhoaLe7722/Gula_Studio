using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;

namespace CharacterConceptAI.Desktop.Infrastructure.Settings;

public enum AppTheme
{
    Dark,
    Light
}

/// <summary>
/// Switches semantic resources without rebuilding the shell. Stores appearance only;
/// credentials and sessions must never be included in this preference file.
/// </summary>
public sealed class ThemeService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter<AppTheme>() }
    };

    private Application? _application;
    private bool _initialized;

    private ThemeService() { }

    public static ThemeService Instance { get; } = new();
    public AppTheme CurrentTheme { get; private set; } = AppTheme.Dark;
    public double CurrentFontScale { get; private set; } = 1.0;
    public bool IsHighContrast => SystemParameters.HighContrast;
    public string? PersistenceWarning { get; private set; }
    public event EventHandler? ThemeChanged;
    public event EventHandler? FontScaleChanged;

    public void Initialize(Application? application = null)
    {
        if (_initialized) return;
        _application = application ?? Application.Current
            ?? throw new InvalidOperationException("ThemeService requires a running WPF application.");
        _application.Dispatcher.VerifyAccess();
        CurrentTheme = ReadPreference();
        ApplyPalette();
        ApplyFontScaleResources(CurrentFontScale);
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
        _application.Exit += OnApplicationExit;
        _initialized = true;
    }

    public void SetFontScale(double scale)
    {
        var clamped = Math.Round(Math.Clamp(scale, 0.85, 1.30), 2);
        CurrentFontScale = clamped;
        if (_application != null)
        {
            if (_application.Dispatcher.CheckAccess())
                ApplyFontScaleResources(clamped);
            else
                _application.Dispatcher.Invoke(() => ApplyFontScaleResources(clamped));
        }
        FontScaleChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyFontScaleResources(double scale)
    {
        if (_application == null) return;
        var res = _application.Resources;
        res["FontSizeCaption"] = Math.Round(12.5 * scale, 1);
        res["FontSizeSmall"] = Math.Round(13.5 * scale, 1);
        res["FontSizeBody"] = Math.Round(15.5 * scale, 1);
        res["FontSizeSubtitle"] = Math.Round(17.5 * scale, 1);
        res["FontSizeSection"] = Math.Round(20.0 * scale, 1);
        res["FontSizePage"] = Math.Round(28.0 * scale, 1);
        res["FontSizeHero"] = Math.Round(34.0 * scale, 1);
    }

    public void SetTheme(AppTheme theme, bool persist = true)
    {
        if (!Enum.IsDefined(theme)) throw new ArgumentOutOfRangeException(nameof(theme));
        Initialize();
        _application!.Dispatcher.VerifyAccess();
        CurrentTheme = theme;
        ApplyPalette();
        if (persist) SavePreference();
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ToggleTheme() => SetTheme(CurrentTheme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark);

    private static string PreferencePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CharacterConceptAI", "desktop-appearance.json");

    private AppTheme ReadPreference()
    {
        try
        {
            if (!File.Exists(PreferencePath)) return AppTheme.Dark;
            // A preference file is deliberately tiny. Ignore corrupt or oversized input.
            if (new FileInfo(PreferencePath).Length > 4096) return AppTheme.Dark;
            var preference = JsonSerializer.Deserialize<AppearancePreference>(File.ReadAllText(PreferencePath), JsonOptions);
            return preference is not null && Enum.IsDefined(preference.Theme) ? preference.Theme : AppTheme.Dark;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            PersistenceWarning = "Không đọc được giao diện đã lưu. Đang dùng giao diện tối mặc định.";
            return AppTheme.Dark;
        }
    }

    private void SavePreference()
    {
        var temporaryPath = PreferencePath + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PreferencePath)!);
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new AppearancePreference(CurrentTheme), JsonOptions));
            File.Move(temporaryPath, PreferencePath, overwrite: true);
            PersistenceWarning = null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Applying the theme still succeeds when a profile cannot be written.
            PersistenceWarning = "Đã đổi giao diện cho phiên này nhưng chưa lưu được tùy chọn trên máy.";
        }
    }

    private void ApplyPalette()
    {
        var paletteName = IsHighContrast ? "HighContrast" : CurrentTheme.ToString();
        var palette = new ResourceDictionary
        {
            Source = new Uri($"/CharacterConceptAI.Desktop;component/Theme/Tokens/Colors.{paletteName}.xaml", UriKind.Relative)
        };
        var dictionaries = _application!.Resources.MergedDictionaries;
        for (var index = 0; index < dictionaries.Count; index++)
        {
            if (dictionaries[index].Source?.OriginalString.Contains("/Colors.", StringComparison.Ordinal) != true) continue;
            dictionaries[index] = palette;
            return;
        }
        dictionaries.Insert(0, palette);
    }

    private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName != nameof(SystemParameters.HighContrast) || _application is null) return;
        _application.Dispatcher.Invoke(() =>
        {
            ApplyPalette();
            ThemeChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    private void OnApplicationExit(object sender, ExitEventArgs eventArgs)
    {
        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        if (_application is not null) _application.Exit -= OnApplicationExit;
        _application = null;
        _initialized = false;
    }

    private sealed record AppearancePreference(AppTheme Theme);
}

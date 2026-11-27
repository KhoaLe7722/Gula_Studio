using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;
using CharacterConceptAI.Desktop.Services;
using CharacterConceptAI.Shared.Contracts.Generations;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CharacterConceptAI.Desktop.ViewModels;

public sealed record StudioLightingPreset(string Key, string LabelVi, string PromptCueVi, string PromptCueEn)
{
    public string Label => Infrastructure.Settings.LocalizationService.Instance.CurrentLanguage switch
    {
        Infrastructure.Settings.AppLanguage.En => Key switch
        {
            "none" => "Default",
            "golden" => "Golden Hour",
            "rembrandt" => "Studio Rembrandt",
            "rim" => "Rim Backlight",
            "window" => "Soft Window",
            "neon" => "Night Neon",
            "chiaroscuro" => "Chiaroscuro",
            _ => LabelVi
        },
        Infrastructure.Settings.AppLanguage.Zh => Key switch
        {
            "none" => "默认",
            "golden" => "黄金时刻",
            "rembrandt" => "伦勃朗光",
            "rim" => "轮廓逆光",
            "window" => "柔和窗光",
            "neon" => "霓虹夜景",
            "chiaroscuro" => "高对比明暗",
            _ => LabelVi
        },
        _ => LabelVi
    };
}

public sealed record StudioCameraPreset(string Key, string LabelVi, string PromptCueVi, string PromptCueEn)
{
    public string Label => Infrastructure.Settings.LocalizationService.Instance.CurrentLanguage switch
    {
        Infrastructure.Settings.AppLanguage.En => Key switch
        {
            "none" => "Default",
            "portrait85" => "85mm Portrait",
            "street35" => "35mm Street",
            "anamorphic" => "2.39 Cinema",
            "closeup" => "Expressive Close-Up",
            "wide24" => "24mm Wide",
            _ => LabelVi
        },
        Infrastructure.Settings.AppLanguage.Zh => Key switch
        {
            "none" => "默认",
            "portrait85" => "85mm 人像",
            "street35" => "35mm 街拍",
            "anamorphic" => "2.39 电影宽银幕",
            "closeup" => "特写表情",
            "wide24" => "24mm 广角全景",
            _ => LabelVi
        },
        _ => LabelVi
    };
}

public sealed partial class StudioLoraItemVm : ObservableObject
{
    private readonly Action _onSelectionChanged;

    public string FileName { get; }
    public string DisplayNameVi { get; }
    public string DescriptionVi { get; }
    public string Category { get; }
    public bool IsRecommended { get; }
    public string SizeMbLabel { get; }

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StrengthLabel))]
    private double _strength;

    public string StrengthLabel => $"{Strength:0.00}";

    public StudioLoraItemVm(LoraCatalogItemDto dto, Action onSelectionChanged)
    {
        FileName = dto.FileName;
        DisplayNameVi = dto.DisplayNameVi;
        DescriptionVi = dto.DescriptionVi;
        Category = dto.Category;
        IsRecommended = dto.IsRecommended;
        SizeMbLabel = $"{dto.SizeBytes / (1024.0 * 1024.0):0} MB";
        _strength = dto.DefaultStrength;
        _onSelectionChanged = onSelectionChanged;
    }

    partial void OnIsEnabledChanged(bool value) => _onSelectionChanged();
    partial void OnStrengthChanged(double value) => _onSelectionChanged();
}

public partial class MainViewModel
{
    // ─── Pillar 5A: Cinematic Lighting & Lens Presets (Subject-First Safe — zero hardware leakage) ───
    public IReadOnlyList<StudioLightingPreset> AvailableLightingPresets { get; } =
    [
        new("none", "Mặc định", "", ""),
        new("golden", "Giờ vàng", "ánh nắng hoàng hôn giờ vàng ấm áp chiếu xiên tạo viền tóc rực rỡ", "warm golden hour sunlight casting soft rim glow along hair and shoulders"),
        new("rembrandt", "Studio Rembrandt", "ánh sáng studio Rembrandt tinh tế một bên mặt có chiều sâu bóng tối nghệ thuật", "sculpted studio Rembrandt key lighting with gentle dimensional shadow falloff"),
        new("rim", "Viền sáng Rim", "ánh sáng viền ngược nổi bật đường nét nhân vật tách khỏi hậu cảnh mờ sâu", "crisp cinematic rim backlight separating subject silhouette from atmospheric depth"),
        new("window", "Cửa sổ dịu", "ánh sáng tự nhiên dịu nhẹ từ cửa sổ bên tạo làn da trong trẻo và bóng đổ mềm", "soft diffused natural side window light with gentle highlights and luminous skin"),
        new("neon", "Neon Đêm", "ánh đèn neon xanh lam và hồng tím đêm đô thị phản chiếu tinh tế lên da và trang phục", "moody nocturnal cyan and magenta neon street glow reflecting softly on skin and fabric"),
        new("chiaroscuro", "Tương phản sâu", "ánh sáng chiaroscuro điện ảnh tương phản sáng tối sắc nét đầy kịch tính", "dramatic high-contrast chiaroscuro cinematic lighting with rich velvety shadows")
    ];

    public IReadOnlyList<StudioCameraPreset> AvailableCameraPresets { get; } =
    [
        new("none", "Mặc định", "", ""),
        new("portrait85", "Chân dung 85mm", "bố cục chân dung cận vai xóa phông bokeh mịn màng tiêu cự 85mm sắc nét", "flattering shallow depth-of-field portrait framing with creamy background bokeh"),
        new("street35", "Đời thường 35mm", "góc nhìn đời thực tự nhiên trung cảnh rộng vừa lấy cả nhân vật và bối cảnh sống động", "natural candid environmental medium-wide framing capturing subject and authentic surroundings"),
        new("anamorphic", "Điện ảnh 2.39", "khung hình điện ảnh màn ảnh rộng chiều sâu trường ảnh mịn và ánh sáng loe nhẹ tinh tế", "widescreen cinema composition with deep atmospheric perspective and subtle horizontal light bloom"),
        new("closeup", "Cận mặt biểu cảm", "góc cận mặt tập trung sâu vào ánh mắt long lanh, chi tiết làn da tự nhiên và biểu cảm tinh tế", "intimate close-up framing focusing sharply on expressive eyes, natural skin pores, and fine hair"),
        new("wide24", "Toàn cảnh rộng", "bố cục toàn thân góc rộng hùng vĩ lấy trọn kiến trúc không gian xung quanh", "expansive full-body wide-angle environmental framing showcasing full outfit and grand backdrop")
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedLightingPreset))]
    [NotifyPropertyChangedFor(nameof(SelectedLightingPresetLabel))]
    [NotifyPropertyChangedFor(nameof(StudioOptionsSummary))]
    private string _selectedLightingPresetKey = "none";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedCameraPreset))]
    [NotifyPropertyChangedFor(nameof(SelectedCameraPresetLabel))]
    [NotifyPropertyChangedFor(nameof(StudioOptionsSummary))]
    private string _selectedCameraPresetKey = "none";

    public bool HasSelectedLightingPreset => !string.Equals(SelectedLightingPresetKey, "none", StringComparison.OrdinalIgnoreCase);
    public bool HasSelectedCameraPreset => !string.Equals(SelectedCameraPresetKey, "none", StringComparison.OrdinalIgnoreCase);
    public string SelectedLightingPresetLabel => AvailableLightingPresets.FirstOrDefault(p => p.Key == SelectedLightingPresetKey)?.Label ?? "Mặc định";
    public string SelectedCameraPresetLabel => AvailableCameraPresets.FirstOrDefault(p => p.Key == SelectedCameraPresetKey)?.Label ?? "Mặc định";

    [RelayCommand]
    private void SelectLightingPreset(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        SelectedLightingPresetKey = key;
        StatusMessage = key == "none" ? "Đã đặt lại ánh sáng mặc định." : $"Đã chọn ánh sáng: {SelectedLightingPresetLabel}.";
    }

    [RelayCommand]
    private void SelectCameraPreset(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        SelectedCameraPresetKey = key;
        StatusMessage = key == "none" ? "Đã đặt lại góc máy mặc định." : $"Đã chọn góc máy: {SelectedCameraPresetLabel}.";
    }

    // ─── Pillar 1 & 5B: Curated & Dynamic Local LoRA Manager ───
    public ObservableCollection<StudioLoraItemVm> AvailableLoras { get; } = [];

    [ObservableProperty]
    private string _lorasFolderPath = string.Empty;

    public int ActiveLoraCount => AvailableLoras.Count(l => l.IsEnabled);
    public bool HasActiveLoras => ActiveLoraCount > 0;
    public bool HasAvailableLoras => AvailableLoras.Count > 0;
    public string ActiveLorasBadge => ActiveLoraCount > 0
        ? $"{ActiveLoraCount} LoRA"
        : Infrastructure.Settings.LocalizationService.Instance.CurrentLanguage switch
        {
            Infrastructure.Settings.AppLanguage.En => "No LoRA",
            Infrastructure.Settings.AppLanguage.Zh => "未启用 LoRA",
            _ => "Chưa bật LoRA"
        };

    private void NotifyLoraSelectionChanged()
    {
        // Cap active LoRAs at 3 for clean Z-Image-Turbo fidelity
        var enabled = AvailableLoras.Where(l => l.IsEnabled).ToList();
        if (enabled.Count > 3)
        {
            enabled[0].IsEnabled = false;
            return;
        }
        OnPropertyChanged(nameof(ActiveLoraCount));
        OnPropertyChanged(nameof(HasActiveLoras));
        OnPropertyChanged(nameof(ActiveLorasBadge));
        OnPropertyChanged(nameof(StudioOptionsSummary));
    }

    public IReadOnlyList<LoraSelectionDto>? GetActiveLoraSelections()
    {
        var active = AvailableLoras
            .Where(l => l.IsEnabled)
            .Take(3)
            .Select(l => new LoraSelectionDto(l.FileName, Math.Round(Math.Clamp(l.Strength, 0.05, 1.50), 2)))
            .ToList();
        return active.Count > 0 ? active : null;
    }

    [RelayCommand]
    private async Task RefreshLoraCatalogAsync() => await LoadLoraCatalogAsync();

    public async Task LoadLoraCatalogAsync()
    {
        if (!IsLoggedIn || !IsRealSession) return;
        try
        {
            var res = await _api.GetLoraCatalogAsync();
            if (res == null) return;
            LorasFolderPath = res.LorasFolder;

            var prevStates = AvailableLoras.ToDictionary(l => l.FileName, l => (l.IsEnabled, l.Strength), StringComparer.OrdinalIgnoreCase);
            AvailableLoras.Clear();
            foreach (var item in res.Items)
            {
                var vm = new StudioLoraItemVm(item, NotifyLoraSelectionChanged);
                if (prevStates.TryGetValue(item.FileName, out var prev))
                {
                    vm.IsEnabled = prev.IsEnabled;
                    vm.Strength = prev.Strength;
                }
                AvailableLoras.Add(vm);
            }
            OnPropertyChanged(nameof(HasAvailableLoras));
            NotifyLoraSelectionChanged();
        }
        catch { }
    }

    [RelayCommand]
    private void OpenLorasFolder()
    {
        var path = string.IsNullOrWhiteSpace(LorasFolderPath)
            ? Path.Combine(GulaWorkspaceLocator.GetWorkspaceRoot(), "ComfyUI", "models", "loras")
            : LorasFolderPath;
        if (!Directory.Exists(path)) return;
        try { Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }); }
        catch { }
    }

    // ─── Pillar 3: Dual-Face Couple Identity Lock (Character 2 — Right Side) ───
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSecondaryStudioCharacter))]
    [NotifyPropertyChangedFor(nameof(SecondaryStudioFaceLockSummary))]
    private StoryCharacterVm? _secondaryStudioCharacter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SecondaryStudioFaceLockSummary))]
    private double _secondaryStudioFaceStrength = 0.90;

    [ObservableProperty]
    private bool _isSecondaryCharacterPickerOpen;

    public bool HasSecondaryStudioCharacter => SecondaryStudioCharacter != null;
    public string SecondaryStudioFaceLockSummary => SecondaryStudioCharacter is { } ch
        ? $"Phải: {ch.Name} ({Math.Round(SecondaryStudioFaceStrength * 100):0}%)"
        : string.Empty;

    [RelayCommand]
    private void ToggleSecondaryCharacterPicker()
    {
        EnsureStoriesLoaded();
        RefreshAllSavedCharacters();
        IsSecondaryCharacterPickerOpen = !IsSecondaryCharacterPickerOpen;
    }

    [RelayCommand]
    private void SelectSecondaryStudioCharacter(StoryCharacterVm? character)
    {
        if (character == null) return;
        if (ActiveStudioCharacter == null)
        {
            SelectStudioCharacter(character);
            IsSecondaryCharacterPickerOpen = false;
            return;
        }
        SecondaryStudioCharacter = character;
        SecondaryStudioFaceStrength = character.FaceSwapStrength;
        SelectedPromptMode = PromptMode.Raw;
        IsSecondaryCharacterPickerOpen = false;
        StatusMessage = $"🔒 Khóa cặp đôi 2 nhân vật: Trái = '{ActiveStudioCharacter.Name}' · Phải = '{character.Name}'!";
    }

    [RelayCommand]
    private void ClearSecondaryStudioCharacter()
    {
        SecondaryStudioCharacter = null;
        StatusMessage = "Đã tắt khóa nhân vật thứ 2 (quay về chế độ 1 nhân vật).";
    }

    [RelayCommand]
    private void SwapStudioCharacters()
    {
        if (ActiveStudioCharacter == null || SecondaryStudioCharacter == null) return;
        var left = ActiveStudioCharacter;
        var right = SecondaryStudioCharacter;
        SelectStudioCharacter(right);
        SecondaryStudioCharacter = left;
        SecondaryStudioFaceStrength = left.FaceSwapStrength;
        StatusMessage = $"⇄ Đã đổi vị trí: Trái = '{ActiveStudioCharacter.Name}' · Phải = '{SecondaryStudioCharacter.Name}'.";
    }

    private async Task EnsureSecondaryStudioFaceLockUploadedAsync(CancellationToken ct, bool forceReupload = false)
    {
        if (SecondaryStudioCharacter is not { EnableFaceLock: true } ch) return;
        if (SelectedProject == null || SelectedCharacter == null || SelectedCharacter.ProjectId != SelectedProject.Id)
            await EnsureAutoWorkspaceAsync();
        if (SelectedProject == null || SelectedCharacter == null) return;

        var currentProjectId = SelectedProject.Id;
        var currentCharacterId = SelectedCharacter.Id;

        if ((forceReupload || ch.FaceConceptVersionId == null || ch.FaceUploadedProjectId != currentProjectId) &&
            !string.IsNullOrWhiteSpace(ch.FaceImagePath) && File.Exists(ch.FaceImagePath))
        {
            var bytes = await File.ReadAllBytesAsync(ch.FaceImagePath, ct);
            var uploaded = await _api.UploadReferenceImageAsync(currentProjectId, currentCharacterId, bytes, Path.GetFileName(ch.FaceImagePath), ct);
            if (uploaded != null)
            {
                ch.FaceConceptVersionId = uploaded.ConceptVersionId;
                ch.FaceUploadedProjectId = currentProjectId;
                SaveLocalStories();
            }
        }

        var extraUploaded = false;
        foreach (var extra in ch.ExtraFaces.Take(2))
        {
            if ((forceReupload || extra.ConceptVersionId == null || extra.UploadedProjectId != currentProjectId) &&
                !string.IsNullOrWhiteSpace(extra.ImagePath) && File.Exists(extra.ImagePath))
            {
                var bytes = await File.ReadAllBytesAsync(extra.ImagePath, ct);
                var uploaded = await _api.UploadReferenceImageAsync(currentProjectId, currentCharacterId, bytes, Path.GetFileName(extra.ImagePath), ct);
                if (uploaded != null)
                {
                    extra.ConceptVersionId = uploaded.ConceptVersionId;
                    extra.UploadedProjectId = currentProjectId;
                    extraUploaded = true;
                }
            }
        }
        if (extraUploaded) SaveLocalStories();
    }

    private Guid? GetSecondaryStudioPrimaryFaceId() =>
        SecondaryStudioCharacter is { EnableFaceLock: true } ch ? ch.FaceConceptVersionId : null;

    private IReadOnlyList<Guid>? GetSecondaryStudioAdditionalFaceIds()
    {
        var primaryId = GetSecondaryStudioPrimaryFaceId();
        if (!primaryId.HasValue || SecondaryStudioCharacter is not { EnableFaceLock: true, ExtraFaces.Count: > 0 } ch)
            return null;
        var extras = ch.ExtraFaces
            .Where(a => a.ConceptVersionId.HasValue && a.ConceptVersionId.Value != primaryId.Value)
            .Select(a => a.ConceptVersionId!.Value)
            .Distinct()
            .Take(2)
            .ToList();
        return extras.Count > 0 ? extras : null;
    }

    // ─── Pillars 1, 2 & 4: Lightbox Pro Studio (2K Tiled Hires-Fix, Magic Brush Inpaint, Outpaint, RMBG, 0-GPU Film Grade) ───
    public Func<byte[]?>? MaskPngExporter { get; set; }
    public Action? MaskClearAction { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLightboxDetailsTab))]
    [NotifyPropertyChangedFor(nameof(IsLightboxMagicBrushTab))]
    [NotifyPropertyChangedFor(nameof(IsLightboxColorGradeTab))]
    [NotifyPropertyChangedFor(nameof(ShowLightboxStandardView))]
    [NotifyPropertyChangedFor(nameof(ShowLightboxBrushCanvas))]
    private int _lightboxToolTab; // 0 = Chi tiết, 1 = Cọ sửa & Mở rộng, 2 = Màu Film

    public bool IsLightboxDetailsTab => LightboxToolTab == 0;
    public bool IsLightboxMagicBrushTab => LightboxToolTab == 1;
    public bool IsLightboxColorGradeTab => LightboxToolTab == 2;
    public bool ShowLightboxBrushCanvas => IsLightboxMagicBrushTab && !IsLightboxCompareMode;

    [ObservableProperty]
    private bool _isLightboxProBusy;

    [ObservableProperty]
    private string _lightboxProStatusText = string.Empty;

    [ObservableProperty]
    private bool _isLightboxTransparencyMode;

    [ObservableProperty]
    private double _hiresDenoise = 0.24;

    [ObservableProperty]
    private double _lightboxBrushSize = 44;

    [ObservableProperty]
    private bool _isLightboxBrushErase;

    [ObservableProperty]
    private string _inpaintPrompt = string.Empty;

    [ObservableProperty]
    private double _inpaintControlStrength = 0.90;

    [ObservableProperty]
    private string _outpaintPrompt = string.Empty;

    // Color Grading State (0-GPU Real-time)
    public IReadOnlyList<FilmColorPreset> AvailableFilmPresets => ImageColorGradingService.Presets;

    [ObservableProperty]
    private string _selectedFilmPresetKey = "original";

    [ObservableProperty] private double _gradeExposure;
    [ObservableProperty] private double _gradeContrast;
    [ObservableProperty] private double _gradeSaturation;
    [ObservableProperty] private double _gradeWarmth;
    [ObservableProperty] private double _gradeClarity;
    [ObservableProperty] private double _gradeFilmGrain;
    [ObservableProperty] private double _gradeVignette;

    private BitmapSource? _gradeBaseSource;
    private PreparedColorGradeBuffer? _gradePreparedBuffer;
    private BitmapSource? _gradedPreviewSource;
    private bool _suppressGradePreview;
    private CancellationTokenSource? _gradeDebounceCts;

    /// <summary>Zero-reencode live preview ImageSource for Lightbox (eliminates 150-400ms PNG encode/decode lag on slider drag).</summary>
    public System.Windows.Media.ImageSource? LightboxDisplayImage => _gradedPreviewSource ?? GeneratedImage;

    private void ResetLightboxProState(BitmapSource? baseSource)
    {
        _gradeDebounceCts?.Cancel();
        LightboxToolTab = 0;
        IsLightboxTransparencyMode = false;
        IsLightboxBrushErase = false;
        _gradeBaseSource = baseSource;
        _gradePreparedBuffer = null;
        _gradedPreviewSource = null;
        _suppressGradePreview = true;
        SelectedFilmPresetKey = "original";
        GradeExposure = GradeContrast = GradeSaturation = GradeWarmth = GradeClarity = GradeFilmGrain = GradeVignette = 0;
        _suppressGradePreview = false;
        OnPropertyChanged(nameof(LightboxDisplayImage));
        MaskClearAction?.Invoke();
    }

    [RelayCommand]
    private void SetLightboxToolTab(string? tabStr)
    {
        if (!int.TryParse(tabStr, out var idx) || idx is < 0 or > 2) return;
        LightboxToolTab = idx;
        if (idx == 1)
        {
            IsLightboxCompareMode = false;
            if (string.IsNullOrWhiteSpace(InpaintPrompt) && !string.IsNullOrWhiteSpace(LightboxPrompt))
                InpaintPrompt = LightboxPrompt;
            if (string.IsNullOrWhiteSpace(OutpaintPrompt) && !string.IsNullOrWhiteSpace(LightboxPrompt))
                OutpaintPrompt = LightboxPrompt;
        }
        else if (idx == 2)
        {
            IsLightboxCompareMode = false;
            if (!ReferenceEquals(_gradeBaseSource, GeneratedImage))
            {
                _gradeBaseSource = GeneratedImage;
                _gradePreparedBuffer = null;
                _gradedPreviewSource = null;
            }
        }
        NotifyLightboxChanged();
        OnPropertyChanged(nameof(LightboxDisplayImage));
    }

    [RelayCommand]
    private void SelectFilmPreset(string? key)
    {
        var preset = ImageColorGradingService.GetPreset(key);
        _suppressGradePreview = true;
        SelectedFilmPresetKey = preset.Key;
        GradeExposure = preset.Exposure;
        GradeContrast = preset.Contrast;
        GradeSaturation = preset.Saturation;
        GradeWarmth = preset.Warmth;
        GradeClarity = preset.Clarity;
        GradeFilmGrain = preset.FilmGrain;
        GradeVignette = preset.Vignette;
        _suppressGradePreview = false;
        ScheduleColorGradePreview();
    }

    [RelayCommand]
    private void ResetColorGrade()
    {
        _gradeDebounceCts?.Cancel();
        SelectFilmPreset("original");
        _gradedPreviewSource = null;
        OnPropertyChanged(nameof(LightboxDisplayImage));
        StatusMessage = "Đã đặt lại màu ảnh gốc.";
    }

    partial void OnGradeExposureChanged(double value) => ScheduleColorGradePreview();
    partial void OnGradeContrastChanged(double value) => ScheduleColorGradePreview();
    partial void OnGradeSaturationChanged(double value) => ScheduleColorGradePreview();
    partial void OnGradeWarmthChanged(double value) => ScheduleColorGradePreview();
    partial void OnGradeClarityChanged(double value) => ScheduleColorGradePreview();
    partial void OnGradeFilmGrainChanged(double value) => ScheduleColorGradePreview();
    partial void OnGradeVignetteChanged(double value) => ScheduleColorGradePreview();

    private void ScheduleColorGradePreview()
    {
        if (_suppressGradePreview || !IsLightboxOpen || _gradeBaseSource == null) return;
        _gradeDebounceCts?.Cancel();
        _gradeDebounceCts?.Dispose();
        _gradeDebounceCts = new CancellationTokenSource();
        var ct = _gradeDebounceCts.Token;
        var baseImg = _gradeBaseSource;
        var cachedBuf = _gradePreparedBuffer;
        var settings = new ColorGradeSettings(
            SelectedFilmPresetKey, GradeExposure, GradeContrast, GradeSaturation,
            GradeWarmth, GradeClarity, GradeFilmGrain, GradeVignette);

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(16, ct);
                if (ImageColorGradingService.IsIdentity(settings))
                {
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        if (ct.IsCancellationRequested || !IsLightboxOpen) return;
                        _gradedPreviewSource = null;
                        OnPropertyChanged(nameof(LightboxDisplayImage));
                    });
                    return;
                }

                var buf = cachedBuf ?? ImageColorGradingService.Prepare(baseImg);
                var graded = ImageColorGradingService.Apply(buf, settings);
                if (ct.IsCancellationRequested) return;
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (ct.IsCancellationRequested || !IsLightboxOpen) return;
                    if (ReferenceEquals(_gradeBaseSource, baseImg))
                        _gradePreparedBuffer = buf;
                    _gradedPreviewSource = graded;
                    OnPropertyChanged(nameof(LightboxDisplayImage));
                });
            }
            catch (OperationCanceledException) { }
            catch { }
        }, ct);
    }

    [RelayCommand]
    private async Task CommitColorGradeAsync()
    {
        if (_gradedPreviewSource == null)
        {
            StatusMessage = "Hãy chọn bộ lọc màu hoặc chỉnh thanh trượt trước khi áp dụng.";
            return;
        }
        var bytes = await Task.Run(() => ImageColorGradingService.EncodePngBytes(_gradedPreviewSource));
        var finalBmp = await DecodeImageAsync(bytes, decodeWidth: 0);
        if (finalBmp == null) return;

        _gradedPreviewSource = null;
        _gradePreparedBuffer = null;
        await ApplyLightboxEditedResultAsync(bytes, finalBmp, "graded", "Đã áp dụng chỉnh màu Film 35mm vào ảnh!");
        _gradeBaseSource = finalBmp;
        OnPropertyChanged(nameof(LightboxDisplayImage));
    }

    private async Task<byte[]?> GetCurrentLightboxSourceBytesAsync()
    {
        // Prefer full-res PNG directly from disk if available, else encode current GeneratedImage
        var path = LightboxItem?.UpscaledImagePath ?? LightboxItem?.LocalImagePath;
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            return await File.ReadAllBytesAsync(path);
        if (GeneratedImage is { } bmp)
            return await Task.Run(() => ImageColorGradingService.EncodePngBytes(bmp));
        return null;
    }

    private async Task ApplyLightboxEditedResultAsync(byte[] resultBytes, BitmapImage resultBmp, string fileSuffix, string successMsg, bool openCompare = false, bool isTransparent = false)
    {
        IsLightboxTransparencyMode = isTransparent;
        if (LightboxItem is { } target)
        {
            if (target.OriginalImage == null && GeneratedImage != null)
                target.OriginalImage = GeneratedImage;
            target.UpscaledImage = resultBmp;
            if (IsRealSession)
            {
                var id = target.AssetId ?? Guid.NewGuid();
                target.UpscaledImagePath = await ChatHistoryStore.SaveImageBytesAsync(id, resultBytes, fileSuffix);
                TryAutoExportImage(target.Seed ?? 0, resultBmp.PixelWidth, resultBmp.PixelHeight, isUpscaled: true, resultBytes);
                SaveLocalChatHistory();
            }
        }
        GeneratedImage = resultBmp;
        _gradeBaseSource = resultBmp;
        _gradePreparedBuffer = null;
        _gradedPreviewSource = null;
        IsLightboxCompareMode = openCompare && CanCompareLightbox;
        NotifyLightboxChanged();
        OnPropertyChanged(nameof(LightboxDisplayImage));
        StatusMessage = successMsg;
    }

    [RelayCommand]
    private async Task HiresFixLightboxImageAsync()
    {
        if (IsLightboxProBusy) return;
        ReleaseVoiceModelMemory();
        if (SelectedProject == null) await EnsureAutoWorkspaceAsync();
        if (SelectedProject == null) return;

        var srcBytes = await GetCurrentLightboxSourceBytesAsync();
        if (srcBytes == null) return;

        IsLightboxProBusy = true;
        LightboxProStatusText = "Đang tái tạo siêu nét 2K (Tiled Hires-Fix + Detailer LoRA)…";
        StatusMessage = LightboxProStatusText;
        try
        {
            var resultBytes = await _api.HiresUpscale2KAsync(
                SelectedProject.Id,
                srcBytes,
                prompt: LightboxPrompt,
                useTiledHires: true,
                denoise: HiresDenoise,
                upscaler: null,
                loras: GetActiveLoraSelections());

            if (resultBytes == null || resultBytes.Length == 0)
            {
                StatusMessage = ApiError("Không chạy được Siêu nét 2K (GPU đang bận hoặc ComfyUI cần khởi động lại để nạp node mới).");
                return;
            }
            var bmp = await DecodeImageAsync(resultBytes, decodeWidth: 0);
            if (bmp == null) return;

            await ApplyLightboxEditedResultAsync(
                resultBytes, bmp, "2k_hires",
                $"✨ Hoàn tất Siêu nét 2K Hires-Fix ({bmp.PixelWidth} × {bmp.PixelHeight} px)!",
                openCompare: true);
        }
        catch (Exception ex) { ReportError(ex); }
        finally
        {
            IsLightboxProBusy = false;
            LightboxProStatusText = string.Empty;
        }
    }

    [RelayCommand]
    private async Task RemoveBackgroundLightboxAsync()
    {
        if (IsLightboxProBusy) return;
        if (SelectedProject == null) await EnsureAutoWorkspaceAsync();
        if (SelectedProject == null) return;

        var srcBytes = await GetCurrentLightboxSourceBytesAsync();
        if (srcBytes == null) return;

        IsLightboxProBusy = true;
        LightboxProStatusText = "Đang tách nền trong suốt PNG (RMBG-1.4 · 0 VRAM)…";
        StatusMessage = LightboxProStatusText;
        try
        {
            var resultBytes = await _api.RemoveBackgroundAsync(SelectedProject.Id, srcBytes);
            if (resultBytes == null || resultBytes.Length == 0)
            {
                StatusMessage = ApiError("Không tách được nền (ComfyUI có thể cần khởi động lại để nạp node RMBG-1.4).");
                return;
            }
            var bmp = await DecodeImageAsync(resultBytes, decodeWidth: 0);
            if (bmp == null) return;

            IsLightboxTransparencyMode = true;
            IsLightboxCompareMode = false;
            _gradedPreviewSource = null;
            _gradePreparedBuffer = null;
            GeneratedImage = bmp;
            _gradeBaseSource = bmp;
            if (IsRealSession)
            {
                TryAutoExportImage(LightboxItem?.Seed ?? 0, bmp.PixelWidth, bmp.PixelHeight, isUpscaled: false, resultBytes);
            }
            NotifyLightboxChanged();
            OnPropertyChanged(nameof(LightboxDisplayImage));
            StatusMessage = $"✂️ Đã tách nền PNG trong suốt ({bmp.PixelWidth} × {bmp.PixelHeight} px) — bấm Tải về PNG để lưu!";
        }
        catch (Exception ex) { ReportError(ex); }
        finally
        {
            IsLightboxProBusy = false;
            LightboxProStatusText = string.Empty;
        }
    }

    [RelayCommand]
    private async Task RunInpaintLightboxAsync()
    {
        if (IsLightboxProBusy) return;
        var maskBytes = MaskPngExporter?.Invoke();
        if (maskBytes == null || maskBytes.Length == 0)
        {
            StatusMessage = "Hãy dùng chuột tô lên vùng cần sửa trên ảnh trước khi bấm Sửa vùng chọn.";
            return;
        }
        ReleaseVoiceModelMemory();
        if (SelectedProject == null) await EnsureAutoWorkspaceAsync();
        if (SelectedProject == null) return;

        var srcBytes = await GetCurrentLightboxSourceBytesAsync();
        if (srcBytes == null) return;

        IsLightboxProBusy = true;
        LightboxProStatusText = "Đang sửa vùng đã tô bằng Magic Brush (ControlNet Union 2.1)…";
        StatusMessage = LightboxProStatusText;
        try
        {
            var promptToUse = string.IsNullOrWhiteSpace(InpaintPrompt) ? LightboxPrompt : InpaintPrompt.Trim();
            var resultBytes = await _api.InpaintAsync(
                SelectedProject.Id, srcBytes, maskBytes, promptToUse,
                controlStrength: InpaintControlStrength,
                loras: GetActiveLoraSelections());

            if (resultBytes == null || resultBytes.Length == 0)
            {
                StatusMessage = ApiError("Không chạy được Cọ sửa ảnh (GPU bận hoặc ComfyUI cần khởi động lại để nạp node Inpaint).");
                return;
            }
            var bmp = await DecodeImageAsync(resultBytes, decodeWidth: 0);
            if (bmp == null) return;

            MaskClearAction?.Invoke();
            LightboxToolTab = 0;
            await ApplyLightboxEditedResultAsync(
                resultBytes, bmp, "inpaint",
                $"🖌️ Đã sửa vùng chọn hoàn tất ({bmp.PixelWidth} × {bmp.PixelHeight} px)! Kéo thanh gạt để so sánh Trước / Sau.",
                openCompare: true);
        }
        catch (Exception ex) { ReportError(ex); }
        finally
        {
            IsLightboxProBusy = false;
            LightboxProStatusText = string.Empty;
        }
    }

    [RelayCommand]
    private async Task RunOutpaintLightboxAsync(string? targetPreset)
    {
        if (IsLightboxProBusy) return;
        ReleaseVoiceModelMemory();
        if (SelectedProject == null) await EnsureAutoWorkspaceAsync();
        if (SelectedProject == null) return;

        var srcBytes = await GetCurrentLightboxSourceBytesAsync();
        if (srcBytes == null) return;

        var preset = string.IsNullOrWhiteSpace(targetPreset) ? "16:9" : targetPreset.Trim();
        IsLightboxProBusy = true;
        LightboxProStatusText = $"Đang mở rộng khung ảnh sang {preset} (Outpaint Union 2.1)…";
        StatusMessage = LightboxProStatusText;
        try
        {
            var promptToUse = string.IsNullOrWhiteSpace(OutpaintPrompt) ? LightboxPrompt : OutpaintPrompt.Trim();
            var resultBytes = await _api.OutpaintAsync(
                SelectedProject.Id, srcBytes, promptToUse,
                targetPreset: preset,
                controlStrength: InpaintControlStrength,
                loras: GetActiveLoraSelections());

            if (resultBytes == null || resultBytes.Length == 0)
            {
                StatusMessage = ApiError("Không chạy được Mở rộng khung ảnh (GPU bận hoặc ComfyUI cần khởi động lại để nạp node Outpaint).");
                return;
            }
            var bmp = await DecodeImageAsync(resultBytes, decodeWidth: 0);
            if (bmp == null) return;

            LightboxToolTab = 0;
            await ApplyLightboxEditedResultAsync(
                resultBytes, bmp, $"outpaint_{preset.Replace(':', 'x')}",
                $"🖼️ Đã mở rộng khung hình sang {preset} ({bmp.PixelWidth} × {bmp.PixelHeight} px)!",
                openCompare: false);
        }
        catch (Exception ex) { ReportError(ex); }
        finally
        {
            IsLightboxProBusy = false;
            LightboxProStatusText = string.Empty;
        }
    }
}

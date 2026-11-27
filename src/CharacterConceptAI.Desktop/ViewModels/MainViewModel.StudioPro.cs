using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;
using CharacterConceptAI.Desktop.Services;
using CharacterConceptAI.Shared.Contracts.Concepts;
using CharacterConceptAI.Shared.Contracts.Generations;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CharacterConceptAI.Desktop.ViewModels;

public sealed record StudioStylePreset(string Key, string LabelVi, string PromptCueVi, string PromptCueEn)
{
    public string Label => Infrastructure.Settings.LocalizationService.Instance.CurrentLanguage switch
    {
        Infrastructure.Settings.AppLanguage.En => Key switch
        {
            "none" => "Free",
            "photoreal" => "Photoreal",
            "anime" => "Anime",
            "3d" => "3D Pixar",
            "cyberpunk" => "Cyberpunk",
            "oilpaint" => "Oil Paint",
            "poster" => "Poster",
            "film35mm" => "35mm Film",
            _ => LabelVi
        },
        Infrastructure.Settings.AppLanguage.Zh => Key switch
        {
            "none" => "自由",
            "photoreal" => "写实摄影",
            "anime" => "动漫",
            "3d" => "3D 皮克斯",
            "cyberpunk" => "赛博朋克",
            "oilpaint" => "油画",
            "poster" => "海报设计",
            "film35mm" => "35mm 胶片",
            _ => LabelVi
        },
        _ => LabelVi
    };
}
public sealed record TextStyleOption(string Key, string Label);

public partial class MainViewModel
{
    // ─── Feature 1: Magic Prompt Enhancer (Tongyi-MAI pe.py / Z-Captioner) ───
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EnhancePromptCommand))]
    private bool _isEnhancingPrompt;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUndoEnhance))]
    private string _previousPromptBeforeEnhance = string.Empty;

    public bool CanUndoEnhance => !string.IsNullOrWhiteSpace(PreviousPromptBeforeEnhance);

    [ObservableProperty]
    private bool _enableReasoningEnhance;

    private string _lastEnhancedPrompt = string.Empty;

    [RelayCommand(CanExecute = nameof(CanEnhancePrompt))]
    private async Task EnhancePromptAsync()
    {
        var input = RawPromptInput?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            StatusMessage = "Hãy nhập ý tưởng ngắn gọn trước khi bấm Nâng cấp Prompt.";
            return;
        }
        ReleaseVoiceModelMemory();
        IsEnhancingPrompt = true;
        StatusMessage = EnableReasoningEnhance
            ? "AI đang phân tích bố cục & suy luận ngữ cảnh (~4 giây)…"
            : "AI đang nâng cấp prompt chi tiết (~1.5 giây)…";
        try
        {
            PreviousPromptBeforeEnhance = input;
            var req = new EnhancePromptRequest(
                Prompt: input,
                StylePreset: SelectedStylePresetKey,
                OverlayText: HasOverlayText ? OverlayText.Trim() : null,
                OverlayTextStyle: OverlayTextStyle,
                OverlayTextPosition: OverlayTextPosition,
                Width: ImageWidth,
                Height: ImageHeight,
                EnableReasoning: EnableReasoningEnhance);

            var res = await _api.EnhancePromptAsync(req);
            if (res is { EnhancedPrompt.Length: > 0 })
            {
                _lastEnhancedPrompt = res.EnhancedPrompt.Trim();
                RawPromptInput = _lastEnhancedPrompt;
                StatusMessage = $"Đã nâng cấp prompt bằng {res.Provider}.";
            }
            else
            {
                StatusMessage = ApiError("Không thể nâng cấp prompt lúc này.");
            }
        }
        catch (Exception ex) { ReportError(ex); }
        finally { IsEnhancingPrompt = false; }
    }

    private bool CanEnhancePrompt => !IsEnhancingPrompt && !IsGenerating;

    [RelayCommand]
    private void UndoEnhancePrompt()
    {
        if (string.IsNullOrWhiteSpace(PreviousPromptBeforeEnhance)) return;
        RawPromptInput = PreviousPromptBeforeEnhance;
        PreviousPromptBeforeEnhance = string.Empty;
        _lastEnhancedPrompt = string.Empty;
        StatusMessage = "Đã khôi phục prompt ban đầu.";
    }

    // ─── Feature 2: Style Presets Bar (Subject-First Safe — ZERO camera/lens/metal object leakage!) ───
    public IReadOnlyList<StudioStylePreset> AvailableStylePresets { get; } =
    [
        new("none", "Tự do", "", ""),
        new("photoreal", "Chân thực",
            "phong cách ảnh đời thực chân thực sống động, ánh sáng tự nhiên mềm mại, màu sắc trung thực, hậu cảnh xóa phông nhẹ",
            "candid real-life photographic realism, soft natural daylight, true-to-life colors, gentle natural background blur"),
        new("anime", "Anime",
            "phong cách minh họa anime điện ảnh chất lượng cao, đường nét thanh mảnh sắc sảo, ánh sáng trong trẻo rực rỡ",
            "cinematic anime key visual illustration, crisp expressive linework, luminous cel-painterly lighting and rich colors"),
        new("3d", "3D Pixar",
            "phong cách dựng hình 3D hoạt hình điện ảnh sắc nét, tán xạ ánh sáng dưới bề mặt chân thực, chi tiết vải và tóc mịn",
            "stylized 3D animated film render, subsurface scattering, global illumination, fine woven fabrics and cinematic rim light"),
        new("cyberpunk", "Cyberpunk",
            "không khí cyberpunk khoa học viễn tưởng về đêm, ánh đèn neon xanh lam và tím hồng phản chiếu trên mặt đường ẩm ướt",
            "nocturnal cyberpunk sci-fi atmosphere, electric cyan and magenta neon reflections on rain-slick surfaces, volumetric haze"),
        new("oilpaint", "Sơn dầu",
            "phong cách tranh sơn dầu nghệ thuật, nét cọ đắp nổi giàu chiều sâu, ánh sáng chiaroscuro cổ điển",
            "painterly oil concept painting, visible impasto brushwork, dramatic chiaroscuro lighting, rich fine-art color harmony"),
        new("poster", "Poster",
            "bố cục thiết kế áp phích đồ họa chuyên nghiệp, mảng sáng tối rõ ràng và tách nền sắc nét",
            "modern commercial graphic design poster, structured editorial layout, crisp studio lighting, clean typography space"),
        new("film35mm", "Phim 35mm",
            "chất màu phim nhựa cổ điển ấm áp, hạt phim mịn tự nhiên và độ tương phản dịu mắt",
            "warm vintage analog film tone, fine organic film grain, soft highlights and authentic shadows")
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedStylePresetLabel))]
    [NotifyPropertyChangedFor(nameof(HasSelectedStylePreset))]
    [NotifyPropertyChangedFor(nameof(StudioOptionsSummary))]
    private string _selectedStylePresetKey = "none";

    public bool HasSelectedStylePreset => !string.Equals(SelectedStylePresetKey, "none", StringComparison.OrdinalIgnoreCase);

    public string SelectedStylePresetLabel =>
        AvailableStylePresets.FirstOrDefault(p => p.Key == SelectedStylePresetKey)?.Label ?? "Tự do";

    public string StudioOptionsSummary
    {
        get
        {
            var tags = new List<string>();
            if (HasSelectedStylePreset) tags.Add(SelectedStylePresetLabel);
            if (HasSelectedLightingPreset) tags.Add(SelectedLightingPresetLabel);
            if (HasSelectedCameraPreset) tags.Add(SelectedCameraPresetLabel);
            if (HasActiveLoras) tags.Add(ActiveLorasBadge);
            return tags.Count == 0
                ? Infrastructure.Settings.LocalizationService.Instance.Get("Str.Studio.Options", "Tùy chỉnh")
                : string.Join(" · ", tags.Take(2));
        }
    }

    [RelayCommand]
    private void SelectStylePreset(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        SelectedStylePresetKey = key;
        StatusMessage = key == "none"
            ? "Đã tắt phong cách cố định (vẽ tự do theo prompt)."
            : $"Đã chọn phong cách: {SelectedStylePresetLabel}.";
    }

    // ─── Feature 5: Poster / Text Overlay Builder (Ideogram 3.0 style) ───
    public IReadOnlyList<TextStyleOption> AvailableOverlayTextStyles { get; } =
    [
        new("modern", "Hiện đại (Sans-serif)"),
        new("neon", "Đèn Neon phát sáng"),
        new("gold3d", "Chữ nổi 3D Vàng kim"),
        new("calligraphy", "Thư pháp nghệ thuật"),
        new("serif", "Tạp chí cổ điển (Serif)"),
        new("signboard", "Bảng hiệu đường phố")
    ];

    public IReadOnlyList<TextStyleOption> AvailableOverlayTextPositions { get; } =
    [
        new("auto", "Tự động"),
        new("top", "Phía trên"),
        new("center", "Ở giữa"),
        new("bottom", "Phía dưới")
    ];

    [ObservableProperty]
    private bool _isTextOverlayOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOverlayText))]
    private string _overlayText = string.Empty;

    [ObservableProperty]
    private string _overlayTextStyle = "modern";

    [ObservableProperty]
    private string _overlayTextPosition = "auto";

    public bool HasOverlayText => !string.IsNullOrWhiteSpace(OverlayText);

    [RelayCommand]
    private void ToggleTextOverlay() => IsTextOverlayOpen = !IsTextOverlayOpen;

    [RelayCommand]
    private void ClearTextOverlay()
    {
        OverlayText = string.Empty;
        StatusMessage = "Đã xóa nội dung chữ chèn trên ảnh.";
    }

    private string ComposeEffectiveStudioPrompt(string userPrompt)
    {
        var prompt = userPrompt.Trim();
        // If prompt was already crafted by Magic Enhance, send it cleanly without double-injecting style cues!
        var alreadyEnhanced = !string.IsNullOrWhiteSpace(_lastEnhancedPrompt) &&
            string.Equals(prompt, _lastEnhancedPrompt, StringComparison.Ordinal);

        var isVietnamese = prompt.Any(ch => ch is >= '\u00C0' and <= '\u024F' or >= '\u1E00' and <= '\u1EFF');

        // ALWAYS lock Active Character DNA (and Secondary Character DNA in Dual-Face Couple Mode) cleanly
        var parts = new List<string>();
        var bodyPrompt = prompt.TrimEnd('.', ',');
        if (ActiveStudioCharacter is { } lockedChar && SecondaryStudioCharacter is { } secondChar)
        {
            var leftDna = lockedChar.BuildFullDna(ActiveStudioLockBodyDna);
            var rightDna = secondChar.BuildFullDna(ActiveStudioLockBodyDna);
            if (!string.IsNullOrWhiteSpace(leftDna) && !string.IsNullOrWhiteSpace(rightDna))
            {
                parts.Add(isVietnamese
                    ? $"hai nhân vật đứng cùng khung hình — nhân vật bên trái là {leftDna}; nhân vật bên phải là {rightDna}"
                    : $"two distinct people in the same frame — person on the left: {leftDna}; person on the right: {rightDna}");
            }
            else if (!string.IsNullOrWhiteSpace(leftDna))
            {
                parts.Add(leftDna);
            }
        }
        else if (ActiveStudioCharacter is { } lockedSingle)
        {
            var charDna = lockedSingle.BuildFullDna(ActiveStudioLockBodyDna);
            if (!string.IsNullOrWhiteSpace(charDna))
            {
                var replacedPlaceholder = false;
                foreach (var ph in new[] { "[MÔ TẢ NHÂN VẬT CỦA BẠN]", "[MO TA NHAN VAT CUA BAN]", "[CHARACTER]", "[NHÂN VẬT]" })
                {
                    if (bodyPrompt.Contains(ph, StringComparison.OrdinalIgnoreCase))
                    {
                        bodyPrompt = bodyPrompt.Replace(ph, charDna, StringComparison.OrdinalIgnoreCase);
                        replacedPlaceholder = true;
                    }
                }
                if (!replacedPlaceholder && !bodyPrompt.Contains(charDna, StringComparison.OrdinalIgnoreCase))
                    parts.Add(charDna);
            }
        }
        parts.Add(bodyPrompt);

        if (!alreadyEnhanced && HasSelectedStylePreset)
        {
            var preset = AvailableStylePresets.FirstOrDefault(p => p.Key == SelectedStylePresetKey);
            if (preset != null)
            {
                var cue = isVietnamese ? preset.PromptCueVi : preset.PromptCueEn;
                if (!string.IsNullOrWhiteSpace(cue) && !prompt.Contains(preset.Label, StringComparison.OrdinalIgnoreCase))
                    parts.Add(cue);
            }
        }

        if (HasSelectedLightingPreset)
        {
            var lighting = AvailableLightingPresets.FirstOrDefault(p => p.Key == SelectedLightingPresetKey);
            var lCue = lighting == null ? string.Empty : (isVietnamese ? lighting.PromptCueVi : lighting.PromptCueEn);
            if (!string.IsNullOrWhiteSpace(lCue))
                parts.Add(lCue);
        }

        if (HasSelectedCameraPreset)
        {
            var cam = AvailableCameraPresets.FirstOrDefault(p => p.Key == SelectedCameraPresetKey);
            var cCue = cam == null ? string.Empty : (isVietnamese ? cam.PromptCueVi : cam.PromptCueEn);
            if (!string.IsNullOrWhiteSpace(cCue))
                parts.Add(cCue);
        }

        if (HasOverlayText)
        {
            var cleanText = OverlayText.Trim().Trim('"');
            if (!prompt.Contains($"\"{cleanText}\"", StringComparison.Ordinal))
            {
                var styleDesc = OverlayTextStyle switch
                {
                    "neon" => isVietnamese ? "kiểu chữ đèn neon phát sáng sắc nét" : "glowing crisp neon tube typography",
                    "gold3d" => isVietnamese ? "kiểu chữ nổi 3D màu vàng ánh kim sang trọng" : "embossed 3D golden lettering",
                    "calligraphy" => isVietnamese ? "kiểu chữ thư pháp nghệ thuật rõ nét" : "expressive brush calligraphy lettering",
                    "serif" => isVietnamese ? "kiểu chữ serif tạp chí cổ điển sắc nét" : "refined editorial serif typography",
                    "signboard" => isVietnamese ? "in sắc nét trên bảng hiệu thực tế" : "printed sharply on a realistic storefront signboard",
                    _ => isVietnamese ? "kiểu chữ không chân hiện đại, đậm và rõ nét" : "bold clean modern sans-serif typography"
                };
                var posDesc = OverlayTextPosition switch
                {
                    "top" => isVietnamese ? "đặt nổi bật ở phía trên" : "placed prominently at the top",
                    "center" => isVietnamese ? "đặt cân đối ở giữa ảnh" : "centered prominently in the frame",
                    "bottom" => isVietnamese ? "đặt gọn gàng ở phía dưới" : "placed cleanly along the bottom",
                    _ => isVietnamese ? "bố trí hài hòa trong bố cục" : "integrated harmoniously in the layout"
                };
                parts.Add(isVietnamese
                    ? $"hiển thị chính xác dòng chữ \"{cleanText}\" bằng {styleDesc}, {posDesc}"
                    : $"featuring crisp legible text reading \"{cleanText}\" in {styleDesc}, {posDesc}");
            }
        }

        return string.Join(", ", parts.Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    // ─── Feature 3: Batch Count (1/2/4) + Post-Upscale 2x + Lightbox A/B Comparison ───
    [RelayCommand]
    private void SetBatchCount(string? countStr)
    {
        if (!int.TryParse(countStr, out var count) || count is not (1 or 2 or 4)) return;
        RequestedImageCount = count;
        StatusMessage = count == 1
            ? "Đã chọn tạo 1 ảnh mỗi lượt."
            : $"Đã chọn tạo lưới {count} biến thể cùng lúc.";
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLightboxItem))]
    [NotifyPropertyChangedFor(nameof(LightboxFavoriteLabel))]
    [NotifyPropertyChangedFor(nameof(LightboxBeforeImage))]
    [NotifyPropertyChangedFor(nameof(LightboxAfterImage))]
    [NotifyPropertyChangedFor(nameof(CanCompareLightbox))]
    [NotifyPropertyChangedFor(nameof(LightboxPrompt))]
    [NotifyPropertyChangedFor(nameof(LightboxMetaSummary))]
    private ChatTurnImageItem? _lightboxItem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLightboxStandardView))]
    [NotifyPropertyChangedFor(nameof(ShowLightboxBrushCanvas))]
    [NotifyPropertyChangedFor(nameof(LightboxCompareButtonLabel))]
    private bool _isLightboxCompareMode;

    private List<ChatTurnImageItem> _lightboxGalleryItems = [];
    private int _lightboxGalleryIndex = -1;
    private bool _lightboxOpenedFromFavorites;

    public bool HasLightboxItem => LightboxItem != null;
    public string LightboxFavoriteLabel => LightboxItem?.IsFavorite == true ? "★ Đã thích" : "☆ Yêu thích";
    public BitmapImage? LightboxBeforeImage => LightboxItem?.OriginalImage;
    public BitmapImage? LightboxAfterImage => LightboxItem?.UpscaledImage;
    public bool CanCompareLightbox => LightboxItem is { OriginalImage: not null, UpscaledImage: not null };
    public bool ShowLightboxStandardView => (!IsLightboxCompareMode || !CanCompareLightbox) && !ShowLightboxBrushCanvas;
    public string LightboxCompareButtonLabel => IsLightboxCompareMode ? "Chế độ Zoom 1:1" : "So sánh Trước / Sau";
    public bool HasLightboxGalleryNavigation => _lightboxGalleryItems.Count > 1;
    public bool CanGoPreviousLightboxImage => _lightboxGalleryIndex > 0;
    public bool CanGoNextLightboxImage => _lightboxGalleryIndex >= 0 && _lightboxGalleryIndex < _lightboxGalleryItems.Count - 1;
    public string LightboxPositionLabel => _lightboxGalleryItems.Count > 1 && _lightboxGalleryIndex >= 0
        ? $"{_lightboxGalleryIndex + 1} / {_lightboxGalleryItems.Count}"
        : string.Empty;
    public string LightboxScopeBadge => _lightboxGalleryItems.Count > 1 && _lightboxGalleryIndex >= 0
        ? (_lightboxOpenedFromFavorites ? $"Yêu thích · {LightboxPositionLabel}" : $"Ảnh chat · {LightboxPositionLabel}")
        : string.Empty;

    private void NotifyLightboxChanged()
    {
        OnPropertyChanged(nameof(HasLightboxItem));
        OnPropertyChanged(nameof(LightboxFavoriteLabel));
        OnPropertyChanged(nameof(LightboxBeforeImage));
        OnPropertyChanged(nameof(LightboxAfterImage));
        OnPropertyChanged(nameof(CanCompareLightbox));
        OnPropertyChanged(nameof(ShowLightboxStandardView));
        OnPropertyChanged(nameof(ShowLightboxBrushCanvas));
        OnPropertyChanged(nameof(LightboxCompareButtonLabel));
        OnPropertyChanged(nameof(LightboxPrompt));
        OnPropertyChanged(nameof(LightboxMetaSummary));
        OnPropertyChanged(nameof(HasLightboxGalleryNavigation));
        OnPropertyChanged(nameof(CanGoPreviousLightboxImage));
        OnPropertyChanged(nameof(CanGoNextLightboxImage));
        OnPropertyChanged(nameof(LightboxPositionLabel));
        OnPropertyChanged(nameof(LightboxScopeBadge));
        PreviousLightboxImageCommand.NotifyCanExecuteChanged();
        NextLightboxImageCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void ToggleLightboxCompare()
    {
        if (!CanCompareLightbox) return;
        IsLightboxCompareMode = !IsLightboxCompareMode;
    }

    [RelayCommand]
    private async Task OpenImageLightboxAsync(ChatTurnImageItem? item)
    {
        if (item == null) return;
        // Ensure full-resolution decode for crisp 1:1 Lightbox zoom (chat cards use lighter thumbnail decodes)
        await EnsureFullResolutionChatImageAsync(item);
        if (item.DisplayImage == null) return;

        // Context-aware navigation scope: Favorites gallery browses Favorites; Chat browses active thread images.
        if (ShowFavoritesOnly)
        {
            _lightboxOpenedFromFavorites = true;
            _lightboxGalleryItems = FavoriteImages.ToList();
        }
        else
        {
            _lightboxOpenedFromFavorites = false;
            _lightboxGalleryItems = ActiveChatThread?.Turns.SelectMany(t => t.Images).ToList() ?? [];
        }

        _lightboxGalleryIndex = _lightboxGalleryItems.IndexOf(item);
        if (_lightboxGalleryIndex < 0)
        {
            _lightboxGalleryItems = [item];
            _lightboxGalleryIndex = 0;
        }

        LightboxItem = item;
        _lightboxTurn = null;
        GeneratedImage = item.DisplayImage;
        IsLightboxCompareMode = false;
        ResetLightboxProState(item.DisplayImage);
        NotifyLightboxChanged();
        IsLightboxOpen = true;
    }

    [RelayCommand(CanExecute = nameof(CanGoPreviousLightboxImage))]
    private async Task PreviousLightboxImageAsync()
    {
        if (!CanGoPreviousLightboxImage) return;
        await NavigateLightboxToIndexAsync(_lightboxGalleryIndex - 1);
    }

    [RelayCommand(CanExecute = nameof(CanGoNextLightboxImage))]
    private async Task NextLightboxImageAsync()
    {
        if (!CanGoNextLightboxImage) return;
        await NavigateLightboxToIndexAsync(_lightboxGalleryIndex + 1);
    }

    private async Task NavigateLightboxToIndexAsync(int targetIndex)
    {
        if (targetIndex < 0 || targetIndex >= _lightboxGalleryItems.Count) return;
        var target = _lightboxGalleryItems[targetIndex];
        await EnsureFullResolutionChatImageAsync(target);
        if (target.DisplayImage == null) return;

        _lightboxGalleryIndex = targetIndex;
        LightboxItem = target;
        _lightboxTurn = null;
        GeneratedImage = target.DisplayImage;
        IsLightboxCompareMode = false;
        ResetLightboxProState(target.DisplayImage);
        NotifyLightboxChanged();
    }

    [RelayCommand]
    private async Task UpscaleTurnImageAsync(ChatTurnImageItem? item)
    {
        var target = item ?? LightboxItem;
        if (target == null || target.AssetId is not Guid assetId || target.IsUpscaling) return;
        ReleaseVoiceModelMemory();
        if (SelectedProject == null) await EnsureAutoWorkspaceAsync();
        if (SelectedProject == null) return;

        target.IsUpscaling = true;
        StatusMessage = "Đang nâng cấp làm nét Full 2× bằng RealESRGAN trên GPU…";
        try
        {
            var bytes = await _api.UpscaleAssetAsync(SelectedProject.Id, assetId);
            if (bytes == null || bytes.Length == 0)
            {
                StatusMessage = ApiError("Không thể nâng cấp 2× lúc này (GPU có thể đang bận).");
                return;
            }

            var upscaledImg = await DecodeImageAsync(bytes, decodeWidth: 0);
            if (upscaledImg == null) return;

            target.UpscaledImage = upscaledImg;
            if (IsRealSession)
            {
                target.UpscaledImagePath = await ChatHistoryStore.SaveImageBytesAsync(assetId, bytes, "2x");
                TryAutoExportImage(target.Seed ?? 0, upscaledImg.PixelWidth, upscaledImg.PixelHeight, isUpscaled: true, bytes);
                SyncFavoriteUpscalePath(target);
                SaveLocalChatHistory();
            }

            GeneratedImage = upscaledImg;
            if (LightboxItem == target)
            {
                IsLightboxCompareMode = true;
                NotifyLightboxChanged();
            }
            StatusMessage = $"Đã nâng cấp nét 2× ({upscaledImg.PixelWidth} × {upscaledImg.PixelHeight} px)! Mở Soi 1:1 để gạt so sánh Trước / Sau.";
        }
        catch (Exception ex) { ReportError(ex); }
        finally { target.IsUpscaling = false; }
    }

    [RelayCommand]
    private void SaveImageItem(ChatTurnImageItem? item)
    {
        if (item?.DisplayImage == null) return;
        GeneratedImage = item.DisplayImage;
        SaveCanvasImage();
    }

    [RelayCommand]
    private void CopyImageItem(ChatTurnImageItem? item)
    {
        if (item?.DisplayImage == null) return;
        GeneratedImage = item.DisplayImage;
        CopyCanvasImage();
    }

    [RelayCommand]
    private void UseImageSeed(ChatTurnImageItem? item)
    {
        if (item?.Seed is not long seed) return;
        GenerationSeed = seed;
        StatusMessage = $"Đã khóa Seed {seed} cho lượt vẽ tiếp theo.";
    }

    [RelayCommand]
    private void ToggleFavoriteImage(ChatTurnImageItem? item)
    {
        var target = item ?? LightboxItem;
        if (target == null) return;

        var newState = !target.IsFavorite;
        var primaryKey = GetFavoriteKey(target);
        var assetKey = target.AssetId is Guid aid && aid != Guid.Empty ? FavoritesStore.BuildKey(null, aid) : string.Empty;

        target.IsFavorite = newState;

        // Keep every matching instance across ChatThreads in sync
        foreach (var thread in ChatThreads)
        {
            foreach (var turn in thread.Turns)
            {
                foreach (var img in turn.Images)
                {
                    var imgKey = GetFavoriteKey(img);
                    if (!string.IsNullOrWhiteSpace(primaryKey) && string.Equals(imgKey, primaryKey, StringComparison.OrdinalIgnoreCase))
                    {
                        img.IsFavorite = newState;
                    }
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(primaryKey))
        {
            if (newState)
            {
                if (!string.IsNullOrWhiteSpace(assetKey) && !string.Equals(assetKey, primaryKey, StringComparison.OrdinalIgnoreCase))
                    _favoriteRecords.Remove(assetKey);

                _favoriteRecords[primaryKey] = CreateVaultBackedFavoriteRecord(primaryKey, target, DateTimeOffset.Now);
            }
            else
            {
                if (_favoriteRecords.Remove(primaryKey, out var removedRec) && IsRealSession)
                {
                    FavoritesStore.DeleteVaultFileSafe(removedRec.FavoriteImagePath);
                    FavoritesStore.DeleteVaultFileSafe(removedRec.FavoriteUpscaledImagePath);
                }
                _standaloneFavoriteItems.Remove(primaryKey);
                if (!string.IsNullOrWhiteSpace(assetKey) && !string.Equals(assetKey, primaryKey, StringComparison.OrdinalIgnoreCase))
                {
                    if (_favoriteRecords.Remove(assetKey, out var removedAssetRec) && IsRealSession)
                    {
                        FavoritesStore.DeleteVaultFileSafe(removedAssetRec.FavoriteImagePath);
                        FavoritesStore.DeleteVaultFileSafe(removedAssetRec.FavoriteUpscaledImagePath);
                    }
                    _standaloneFavoriteItems.Remove(assetKey);
                }
            }
        }

        OnPropertyChanged(nameof(LightboxFavoriteLabel));
        SaveFavoritesNow();
        SaveLocalChatHistory();
        RefreshFavoritesGallery();

        if (ShowFavoritesOnly && newState)
            _ = HydrateFavoriteImagesAsync();

        // Keep Lightbox Prev/Next gallery in sync when unfavoriting while browsing Favorites
        if (IsLightboxOpen && _lightboxOpenedFromFavorites && !target.IsFavorite)
        {
            var removedIdx = _lightboxGalleryItems.IndexOf(target);
            if (removedIdx >= 0)
            {
                _lightboxGalleryItems.RemoveAt(removedIdx);
                if (_lightboxGalleryItems.Count == 0)
                {
                    CloseLightbox();
                }
                else
                {
                    var nextIdx = Math.Min(removedIdx, _lightboxGalleryItems.Count - 1);
                    _ = NavigateLightboxToIndexAsync(nextIdx);
                }
            }
        }
        StatusMessage = target.IsFavorite ? "Đã lưu ảnh vào mục Yêu thích." : "Đã bỏ khỏi mục Yêu thích.";
    }

    [RelayCommand]
    private void ReuseFavoritePrompt(ChatTurnImageItem? item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.PromptSnapshot)) return;
        RawPromptInput = item.PromptSnapshot;
        if (item.Width >= 256 && item.Width <= 1536 && item.Width % 64 == 0 &&
            item.Height >= 256 && item.Height <= 1536 && item.Height % 64 == 0)
        {
            ImageWidth = item.Width;
            ImageHeight = item.Height;
        }
        IsLightboxOpen = false;
        ShowFavoritesOnly = false;
        if (CurrentTabIndex != 0) NavigateTo(0);
        StatusMessage = "Đã nạp lại prompt yêu thích vào khung chat để tạo tiếp.";
    }

    // ─── Feature 4: Drag-and-Drop / Attach Reference Image (Img2Img) ───
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAttachedReference))]
    [NotifyPropertyChangedFor(nameof(AttachedReferenceSummary))]
    private string _attachedReferenceName = string.Empty;

    [ObservableProperty]
    private BitmapImage? _attachedReferencePreview;

    [ObservableProperty]
    private bool _isUploadingReference;

    private Guid? _attachedReferenceAssetId;
    private ConceptVersionDto? _attachedReferenceVersionDto;
    private string? _attachedReferenceFilePath;
    private Guid? _attachedReferenceUploadedProjectId;
    private int _attachedReferenceWidth;
    private int _attachedReferenceHeight;

    public bool HasAttachedReference => !string.IsNullOrWhiteSpace(AttachedReferenceName) && _attachedReferenceAssetId.HasValue;

    public string AttachedReferenceSummary => !HasAttachedReference
        ? string.Empty
        : $"Biến đổi từ ảnh gốc (Img2Img · {_attachedReferenceWidth}×{_attachedReferenceHeight} · Denoise {Denoise:0.00})";

    partial void OnDenoiseChanged(double value) => OnPropertyChanged(nameof(AttachedReferenceSummary));

    public async Task AttachLocalReferenceFromFileAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return;
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ".jpeg"))
        {
            StatusMessage = "Chỉ hỗ trợ đính kèm ảnh định dạng PNG hoặc JPG/JPEG.";
            return;
        }

        if (SelectedProject == null || SelectedCharacter == null)
            await EnsureAutoWorkspaceAsync();
        if (SelectedProject == null || SelectedCharacter == null)
        {
            StatusMessage = "Chưa sẵn sàng kết nối máy chủ để tải ảnh tham chiếu.";
            return;
        }

        IsUploadingReference = true;
        StatusMessage = $"Đang tải ảnh mẫu {Path.GetFileName(filePath)}…";
        try
        {
            var bytes = await File.ReadAllBytesAsync(filePath);
            if (bytes.Length > 10_000_000)
            {
                StatusMessage = "Ảnh mẫu vượt quá 10 MB. Hãy chọn ảnh nhẹ hơn.";
                return;
            }

            var preview = await DecodeImageAsync(bytes, decodeWidth: 256);
            var uploaded = await _api.UploadReferenceImageAsync(SelectedProject.Id, SelectedCharacter.Id, bytes, Path.GetFileName(filePath));
            if (uploaded == null)
            {
                StatusMessage = ApiError("Không tải được ảnh tham chiếu lên máy chủ.");
                return;
            }

            _attachedReferenceAssetId = uploaded.AssetId;
            _attachedReferenceFilePath = filePath;
            _attachedReferenceUploadedProjectId = SelectedProject.Id;
            _attachedReferenceWidth = uploaded.Width;
            _attachedReferenceHeight = uploaded.Height;
            _attachedReferenceVersionDto = new ConceptVersionDto(
                Id: uploaded.ConceptVersionId,
                ConceptId: Guid.Empty,
                VersionNumber: 1,
                ParentVersionId: null,
                GenerationJobId: null,
                ImageAssetId: uploaded.AssetId,
                StreamUrl: $"/api/v1/projects/{SelectedProject.Id}/media/{uploaded.AssetId}",
                FinalPromptSnapshot: "Uploaded local reference",
                ParametersSnapshotJson: null,
                Status: ConceptVersionStatus.Draft,
                CreatedByUserId: string.Empty,
                CreatedAt: DateTimeOffset.UtcNow,
                Approvals: []);

            AttachedReferencePreview = preview;
            AttachedReferenceName = Path.GetFileName(filePath);
            ReferencePreviewImage = preview;

            if (IsImg2ImgAvailable)
            {
                if (HasActiveDepthPose) ClearDepthPose();
                SelectedReferenceVersion = _attachedReferenceVersionDto;
                IsImg2ImgMode = true;
                QualityModeIndex = 0;
                StatusMessage = $"Đã đính kèm {AttachedReferenceName} ({uploaded.Width}×{uploaded.Height}) — chế độ Biến đổi ảnh (Img2Img).";
            }
            else
            {
                StatusMessage = $"Đã tải ảnh {AttachedReferenceName} nhưng máy chủ chưa bật Img2Img.";
            }
        }
        catch (Exception ex) { ReportError(ex); }
        finally { IsUploadingReference = false; }
    }

    [RelayCommand]
    private async Task BrowseAttachReferenceAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Chọn ảnh mẫu để chạy Img2Img",
            Filter = "Hình ảnh (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg"
        };
        if (dialog.ShowDialog() == true)
        {
            await AttachLocalReferenceFromFileAsync(dialog.FileName);
        }
    }

    private async Task EnsureAttachedReferenceUploadedAsync(CancellationToken ct, bool forceReupload = false)
    {
        if (!IsImg2ImgMode || string.IsNullOrWhiteSpace(_attachedReferenceFilePath) || !File.Exists(_attachedReferenceFilePath))
            return;
        if (SelectedProject == null || SelectedCharacter == null || SelectedCharacter.ProjectId != SelectedProject.Id)
            await EnsureAutoWorkspaceAsync();
        if (SelectedProject == null || SelectedCharacter == null) return;

        if (!forceReupload && SelectedReferenceVersion != null && _attachedReferenceUploadedProjectId == SelectedProject.Id)
            return;

        var bytes = await File.ReadAllBytesAsync(_attachedReferenceFilePath, ct);
        var uploaded = await _api.UploadReferenceImageAsync(SelectedProject.Id, SelectedCharacter.Id, bytes, Path.GetFileName(_attachedReferenceFilePath), ct);
        if (uploaded == null) return;

        _attachedReferenceAssetId = uploaded.AssetId;
        _attachedReferenceUploadedProjectId = SelectedProject.Id;
        _attachedReferenceWidth = uploaded.Width;
        _attachedReferenceHeight = uploaded.Height;
        _attachedReferenceVersionDto = new ConceptVersionDto(
            Id: uploaded.ConceptVersionId,
            ConceptId: Guid.Empty,
            VersionNumber: 1,
            ParentVersionId: null,
            GenerationJobId: null,
            ImageAssetId: uploaded.AssetId,
            StreamUrl: $"/api/v1/projects/{SelectedProject.Id}/media/{uploaded.AssetId}",
            FinalPromptSnapshot: "Uploaded local reference",
            ParametersSnapshotJson: null,
            Status: ConceptVersionStatus.Draft,
            CreatedByUserId: string.Empty,
            CreatedAt: DateTimeOffset.UtcNow,
            Approvals: []);
        SelectedReferenceVersion = _attachedReferenceVersionDto;
    }

    [RelayCommand]
    private void ClearAttachedReference()
    {
        _attachedReferenceAssetId = null;
        _attachedReferenceVersionDto = null;
        _attachedReferenceFilePath = null;
        _attachedReferenceUploadedProjectId = null;
        AttachedReferenceName = string.Empty;
        AttachedReferencePreview = null;
        SelectedReferenceVersion = null;
        ReferencePreviewImage = null;
        IsImg2ImgMode = false;
        StatusMessage = "Đã gỡ ảnh mẫu đính kèm.";
    }

    // ─── Feature 6: Auto-Export, UI Font Size Scaling & Master NSFW Safety Switch ───
    [ObservableProperty]
    private bool _autoExportEnabled = true;

    [ObservableProperty]
    private string _autoExportFolder = ChatHistoryStore.DefaultAutoExportFolder;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UiFontScalePercentLabel))]
    [NotifyPropertyChangedFor(nameof(UiFontScalePresetKey))]
    [NotifyPropertyChangedFor(nameof(CanDecreaseFontSize))]
    [NotifyPropertyChangedFor(nameof(CanIncreaseFontSize))]
    [NotifyCanExecuteChangedFor(nameof(DecreaseFontSizeCommand))]
    [NotifyCanExecuteChangedFor(nameof(IncreaseFontSizeCommand))]
    private double _uiFontScale = 1.0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NsfwSafetyStatusTitle))]
    [NotifyPropertyChangedFor(nameof(NsfwSafetyStatusDetail))]
    private bool _allowNsfwContent;

    public string UiFontScalePercentLabel => $"{Math.Round(UiFontScale * 100):0}%";
    public string UiFontScalePresetKey => Math.Round(UiFontScale, 2).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
    public bool CanDecreaseFontSize => UiFontScale > 0.86;
    public bool CanIncreaseFontSize => UiFontScale < 1.29;
    public string NsfwSafetyStatusTitle => AllowNsfwContent
        ? Infrastructure.Settings.LocalizationService.Instance.Get("Str.Nsfw.UnlockedTitle", "Đang bật quyền nội dung 18+ (Người lớn)")
        : Infrastructure.Settings.LocalizationService.Instance.Get("Str.Nsfw.SafeTitle", "Đang bật chế độ An toàn (Trẻ nhỏ & Gia đình)");
    public string NsfwSafetyStatusDetail => AllowNsfwContent
        ? Infrastructure.Settings.LocalizationService.Instance.Get("Str.Nsfw.UnlockedDetail", "Tùy chọn Mở khóa 18+ sẽ hiển thị trong Kho Dáng 3D và Kho Trang phục.")
        : Infrastructure.Settings.LocalizationService.Instance.Get("Str.Nsfw.SafeDetail", "Đã ẩn hoàn toàn nút Mở khóa 18+ và lọc sạch mọi tư thế / trang phục nhạy cảm.");

    private bool _loadingPrefs;

    private void LoadStudioPreferences()
    {
        _loadingPrefs = true;
        try
        {
            var prefs = ChatHistoryStore.LoadPreferences();
            AutoExportEnabled = prefs.AutoExportEnabled;
            AutoExportFolder = string.IsNullOrWhiteSpace(prefs.AutoExportFolder)
                ? ChatHistoryStore.DefaultAutoExportFolder
                : prefs.AutoExportFolder;
            UiFontScale = prefs.UiFontScale is >= 0.85 and <= 1.30 ? Math.Round(prefs.UiFontScale, 2) : 1.0;
            AllowNsfwContent = prefs.AllowNsfwContent;
            Infrastructure.Settings.ThemeService.Instance.SetFontScale(UiFontScale);
            if (!AllowNsfwContent)
            {
                PoseGalleryIncludeNsfw = false;
                OutfitGalleryIncludeNsfw = false;
            }
        }
        finally { _loadingPrefs = false; }
    }

    partial void OnAutoExportEnabledChanged(bool value) => PersistStudioPreferences();
    partial void OnAutoExportFolderChanged(string value) => PersistStudioPreferences();

    partial void OnUiFontScaleChanged(double value)
    {
        var clamped = Math.Round(Math.Clamp(value, 0.85, 1.30), 2);
        if (Math.Abs(clamped - value) > 0.001)
        {
            UiFontScale = clamped;
            return;
        }
        Infrastructure.Settings.ThemeService.Instance.SetFontScale(clamped);
        PersistStudioPreferences();
    }

    partial void OnAllowNsfwContentChanged(bool value)
    {
        PersistStudioPreferences();
        if (!value)
        {
            PoseGalleryIncludeNsfw = false;
            OutfitGalleryIncludeNsfw = false;
            if (PoseGallerySystemFilter is "full_sexy" or "couple")
                PoseGallerySystemFilter = string.Empty;
            if (OutfitGalleryCategoryFilter is "sexy" or "lingerie")
                OutfitGalleryCategoryFilter = string.Empty;
            if (IsPoseGalleryOpen) _ = LoadPoseGalleryAsync();
            if (IsOutfitGalleryOpen) _ = LoadOutfitGalleryAsync();
        }
        if (!_loadingPrefs)
        {
            StatusMessage = value
                ? "Đã bật quyền hiển thị tùy chọn 18+ (NSFW) trong Kho Dáng & Trang phục."
                : "Đã tắt hoàn toàn nội dung 18+ (NSFW) — chế độ An toàn cho gia đình & trẻ nhỏ.";
        }
    }

    [RelayCommand(CanExecute = nameof(CanDecreaseFontSize))]
    private void DecreaseFontSize()
    {
        UiFontScale = Math.Round(Math.Clamp(UiFontScale - 0.05, 0.85, 1.30), 2);
        StatusMessage = $"Kích thước chữ: {UiFontScalePercentLabel}";
    }

    [RelayCommand(CanExecute = nameof(CanIncreaseFontSize))]
    private void IncreaseFontSize()
    {
        UiFontScale = Math.Round(Math.Clamp(UiFontScale + 0.05, 0.85, 1.30), 2);
        StatusMessage = $"Kích thước chữ: {UiFontScalePercentLabel}";
    }

    [RelayCommand]
    private void ResetFontSize()
    {
        UiFontScale = 1.0;
        StatusMessage = "Đã đặt lại kích thước chữ mặc định (100%).";
    }

    [RelayCommand]
    private void SetFontScalePreset(string? scaleStr)
    {
        if (double.TryParse(scaleStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            UiFontScale = Math.Round(Math.Clamp(parsed, 0.85, 1.30), 2);
            StatusMessage = $"Kích thước chữ: {UiFontScalePercentLabel}";
        }
    }

    private void PersistStudioPreferences()
    {
        if (_loadingPrefs || !IsRealSession) return;
        ChatHistoryStore.SavePreferencesDebounced(new StudioPreferencesRecord(AutoExportEnabled, AutoExportFolder, UiFontScale, AllowNsfwContent));
    }

    private void TryAutoExportImage(long seed, int width, int height, bool isUpscaled, byte[] bytes)
    {
        if (!AutoExportEnabled || !IsRealSession || string.IsNullOrWhiteSpace(AutoExportFolder)) return;
        _ = ChatHistoryStore.AutoExportImageAsync(AutoExportFolder, seed, width, height, isUpscaled, bytes);
    }

    [RelayCommand]
    private void BrowseAutoExportFolder()
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Chọn thư mục tự động lưu ảnh PNG",
                InitialDirectory = Directory.Exists(AutoExportFolder)
                    ? AutoExportFolder
                    : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
            };
            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
            {
                AutoExportFolder = dialog.FolderName;
                StatusMessage = $"Đã chọn thư mục lưu ảnh: {dialog.FolderName}";
            }
        }
        catch (Exception ex) { ReportError(ex); }
    }

    [RelayCommand]
    private void OpenAutoExportFolder()
    {
        try
        {
            var dir = string.IsNullOrWhiteSpace(AutoExportFolder) ? ChatHistoryStore.DefaultAutoExportFolder : AutoExportFolder;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch (Exception ex) { ReportError(ex); }
    }
}

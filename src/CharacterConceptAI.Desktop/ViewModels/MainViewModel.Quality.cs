using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;
using CharacterConceptAI.Shared.Contracts.Concepts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CharacterConceptAI.Desktop.ViewModels;

public sealed record SessionGenerationItem(
    Guid AssetId,
    long Seed,
    int Width,
    int Height,
    string Prompt,
    bool FullQuality,
    DateTimeOffset CreatedAt,
    BitmapImage FullImage,
    BitmapImage Thumbnail)
{
    public string ResolutionLabel => $"{Width} × {Height}";
    public string ModeLabel => FullQuality ? "Full 2×" : "Chuẩn";
    public string TimeLabel => CreatedAt.ToLocalTime().ToString("HH:mm:ss");
    public string ShortPrompt => string.IsNullOrWhiteSpace(Prompt) ? "Không có mô tả" : Prompt.Trim();
}

public partial class MainViewModel
{
    private readonly ConcurrentDictionary<Guid, BitmapImage> _conceptThumbnails = new();
    private readonly ConcurrentDictionary<Guid, byte> _conceptThumbnailInFlight = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QualityModeHint))]
    private bool _isFullQualityAvailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QualityModeHint))]
    private int _qualityModeIndex;

    [ObservableProperty]
    private string _studioImageSize = string.Empty;

    [ObservableProperty]
    private int _conceptThumbnailsVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LightboxPrompt))]
    [NotifyPropertyChangedFor(nameof(LightboxMetaSummary))]
    private SessionGenerationItem? _selectedSessionGeneration;

    [ObservableProperty]
    private bool _isLightboxOpen;

    [ObservableProperty]
    private bool _isStudioAdvancedOpen;

    public ObservableCollection<SessionGenerationItem> SessionGenerations { get; } = [];
    public bool HasSessionGenerations => SessionGenerations.Count > 0;
    public bool ShowStudioEmptyFeed => !HasSessionGenerations && GeneratedImage == null;
    public bool ShowSingleGeneratedHero => !HasSessionGenerations && GeneratedImage != null;
    public string AspectRatioSummary => $"{ImageWidth} × {ImageHeight} px";
    public string ActiveAspectRatioPreset => (ImageWidth, ImageHeight) switch
    {
        (1024, 1024) => "1:1",
        (896, 1152) => "3:4",
        (832, 1216) => "2:3",
        (1280, 704) => "16:9",
        (512, 768) => "512",
        _ => string.Empty
    };
    public string SessionFeedCountLabel => SessionGenerations.Count == 0
        ? "Chưa có ảnh trong phiên"
        : $"{SessionGenerations.Count} ảnh vừa tạo";

    public string LightboxPrompt => LightboxItem?.PromptSnapshot
        ?? _lightboxTurn?.UserPrompt
        ?? SelectedSessionGeneration?.ShortPrompt
        ?? (!string.IsNullOrWhiteSpace(Prompt) ? Prompt : RawPromptInput);

    public string LightboxMetaSummary => LightboxItem is { } imgItem
        ? $"{imgItem.ResolutionLabel} px{(imgItem.Seed.HasValue ? $" · Seed {imgItem.Seed}" : string.Empty)} · {imgItem.ModeBadge} · {imgItem.CreatedAt.ToLocalTime():HH:mm}"
        : _lightboxTurn is { } turn
            ? $"{turn.ResolutionLabel} px{(turn.Seed.HasValue ? $" · Seed {turn.Seed}" : string.Empty)} · {turn.ModeLabel} · {turn.TimeLabel}"
            : SelectedSessionGeneration is { } item
                ? $"{item.ResolutionLabel} px · Seed {item.Seed} · {item.ModeLabel} · {item.TimeLabel}"
                : StudioImageSize;

    public string QualityModeHint => !IsFullQualityAvailable
        ? Infrastructure.Settings.LocalizationService.Instance.CurrentLanguage switch
        {
            Infrastructure.Settings.AppLanguage.En => "Full 2× mode is not enabled on server.",
            Infrastructure.Settings.AppLanguage.Zh => "服务器尚未启用 Full 2× 模式。",
            _ => "Máy chủ chưa bật chế độ Full."
        }
        : QualityModeIndex == 1
            ? Infrastructure.Settings.LocalizationService.Instance.CurrentLanguage switch
            {
                Infrastructure.Settings.AppLanguage.En => "Generate and upscale 2× in a single job.",
                Infrastructure.Settings.AppLanguage.Zh => "在单次任务中完成生成与 2× 超清放大。",
                _ => "Tạo và hoàn thiện ảnh 2× trong cùng tác vụ."
            }
            : string.Empty;

    partial void OnIsFullQualityAvailableChanged(bool value)
    {
        if (!value) QualityModeIndex = 0;
    }

    partial void OnImageWidthChanged(int value)
    {
        OnPropertyChanged(nameof(AspectRatioSummary));
        OnPropertyChanged(nameof(ActiveAspectRatioPreset));
        OnPropertyChanged(nameof(StudioOptionsSummary));
    }

    partial void OnImageHeightChanged(int value)
    {
        OnPropertyChanged(nameof(AspectRatioSummary));
        OnPropertyChanged(nameof(ActiveAspectRatioPreset));
        OnPropertyChanged(nameof(StudioOptionsSummary));
    }

    partial void OnRequestedImageCountChanged(int value) => OnPropertyChanged(nameof(StudioOptionsSummary));

    partial void OnGeneratedImageChanged(BitmapImage? value)
    {
        StudioImageSize = value == null ? string.Empty : $"{value.PixelWidth} × {value.PixelHeight} px";
        OnPropertyChanged(nameof(ShowStudioEmptyFeed));
        OnPropertyChanged(nameof(ShowSingleGeneratedHero));
        OnPropertyChanged(nameof(LightboxMetaSummary));
        OpenCurrentImageLightboxCommand.NotifyCanExecuteChanged();
        if (value == null) IsLightboxOpen = false;
    }

    partial void OnSelectedSessionGenerationChanged(SessionGenerationItem? value)
    {
        if (value == null) return;
        GeneratedImage = value.FullImage;
    }

    [RelayCommand]
    private void SetAspectRatio(string? preset)
    {
        switch (preset)
        {
            case "1:1": ImageWidth = 1024; ImageHeight = 1024; break;
            case "3:4": ImageWidth = 896; ImageHeight = 1152; break;
            case "2:3": ImageWidth = 832; ImageHeight = 1216; break;
            case "16:9": ImageWidth = 1280; ImageHeight = 704; break;
            case "512": ImageWidth = 512; ImageHeight = 768; break;
            default: return;
        }
        StatusMessage = $"Đã chọn tỷ lệ {preset} ({ImageWidth} × {ImageHeight} px).";
    }

    [RelayCommand]
    private void SelectSessionGeneration(SessionGenerationItem? item)
    {
        if (item == null) return;
        SelectedSessionGeneration = item;
    }

    [RelayCommand]
    private void OpenLightbox(SessionGenerationItem? item)
    {
        if (item != null) SelectedSessionGeneration = item;
        if (GeneratedImage == null) return;
        IsLightboxOpen = true;
    }

    [RelayCommand(CanExecute = nameof(HasGeneratedImage))]
    private void OpenCurrentImageLightbox()
    {
        if (GeneratedImage == null) return;
        IsLightboxOpen = true;
    }

    [RelayCommand]
    private void CloseLightbox() => IsLightboxOpen = false;

    [RelayCommand]
    private void ToggleStudioAdvanced() => IsStudioAdvancedOpen = !IsStudioAdvancedOpen;

    [RelayCommand]
    private void TogglePromptPreviewPanel() => IsPromptPreviewOpen = !IsPromptPreviewOpen;

    [RelayCommand]
    private void UseLastSeed()
    {
        var seed = LightboxItem?.Seed ?? _lightboxTurn?.Seed ?? SelectedSessionGeneration?.Seed ?? SelectedQueueJob?.Seed;
        if (seed is null) return;
        GenerationSeed = seed.Value;
        StatusMessage = $"Đã khóa Seed {seed.Value} cho lần tạo tiếp theo.";
    }

    [RelayCommand]
    private void UseSessionSeed(SessionGenerationItem? item)
    {
        if (item == null) { UseLastSeed(); return; }
        SelectedSessionGeneration = item;
        GenerationSeed = item.Seed;
        StatusMessage = $"Đã khóa Seed {item.Seed} cho lần tạo tiếp theo.";
    }

    [RelayCommand]
    private void ReuseSessionPrompt(SessionGenerationItem? item)
    {
        var text = item?.Prompt ?? LightboxItem?.PromptSnapshot ?? _lightboxTurn?.UserPrompt ?? SelectedSessionGeneration?.Prompt;
        if (string.IsNullOrWhiteSpace(text)) return;
        RawPromptInput = text;
        IsLightboxOpen = false;
        ShowFavoritesOnly = false;
        StatusMessage = "Đã đưa prompt lên khung chat để chỉnh sửa và tạo lại.";
    }

    [RelayCommand]
    private void ApplyStarterPrompt(string? preset)
    {
        switch (preset)
        {
            case "anime-knight":
                ImageWidth = 832; ImageHeight = 1216;
                RawPromptInput = "nữ kiếm sĩ anime tóc bạc dài bay trong gió, mặc giáp trắng viền vàng thanh lịch và áo choàng xanh thẫm, đứng trên đỉnh tháp đá lúc hoàng hôn, ánh sáng ngược vàng ấm rực rỡ, phong cách minh họa anime điện ảnh độ chi tiết cực cao";
                StatusMessage = "Đã nạp gợi ý Nữ hiệp Anime (2:3 · 832×1216).";
                break;
            case "tokyo-rain":
                ImageWidth = 896; ImageHeight = 1152;
                RawPromptInput = "chân dung điện ảnh cô gái trẻ cầm ô trong suốt giữa phố đêm Tokyo mưa nhẹ, ánh đèn neon xanh lam và hồng phản chiếu trên mặt đường ướt, bokeh mềm mượt, ống kính 85mm f/1.4, ánh sáng tự nhiên chân thực";
                StatusMessage = "Đã nạp gợi ý Chân dung mưa Tokyo (3:4 · 896×1152).";
                break;
            case "cyber-mage":
                ImageWidth = 1024; ImageHeight = 1024;
                RawPromptInput = "pháp sư cyberpunk mặc áo khoác công nghệ tối màu phát sáng mạch rune xanh ngọc, đôi mắt phát sáng nhẹ, vòng tròn phép thuật hologram lơ lửng quanh tay, hậu cảnh thành phố tương lai mờ ảo, ánh sáng studio sắc nét";
                StatusMessage = "Đã nạp gợi ý Pháp sư Cyberpunk (1:1 · 1024×1024).";
                break;
            case "expression-3x3":
                ApplyExpressionSheetPreset();
                break;
        }
    }

    private void NotifySessionFeedChanged()
    {
        OnPropertyChanged(nameof(HasSessionGenerations));
        OnPropertyChanged(nameof(ShowStudioEmptyFeed));
        OnPropertyChanged(nameof(ShowSingleGeneratedHero));
        OnPropertyChanged(nameof(SessionFeedCountLabel));
    }

    private void ClearSessionGenerations()
    {
        SessionGenerations.Clear();
        SelectedSessionGeneration = null;
        IsLightboxOpen = false;
        NotifySessionFeedChanged();
    }

    private async Task RecordSessionGenerationAsync(
        Guid assetId, long seed, string prompt, bool fullQuality, byte[] bytes, BitmapImage fullImage)
    {
        var thumb = await DecodeImageAsync(bytes, decodeWidth: 480);
        var item = new SessionGenerationItem(
            assetId, seed, fullImage.PixelWidth, fullImage.PixelHeight,
            prompt, fullQuality, DateTimeOffset.Now, fullImage, thumb);
        SessionGenerations.Insert(0, item);
        while (SessionGenerations.Count > 24)
            SessionGenerations.RemoveAt(SessionGenerations.Count - 1);
        SelectedSessionGeneration = item;
        NotifySessionFeedChanged();
    }

    public BitmapImage? GetConceptThumbnail(Guid? assetId)
        => assetId.HasValue && _conceptThumbnails.TryGetValue(assetId.Value, out var img) ? img : null;

    private void ClearConceptThumbnails()
    {
        _conceptThumbnails.Clear();
        _conceptThumbnailInFlight.Clear();
        ConceptThumbnailsVersion++;
    }

    private async Task LoadConceptThumbnailsAsync(Guid projectId, IReadOnlyList<CharacterConceptDto> concepts)
    {
        foreach (var concept in concepts)
        {
            if (SelectedProject?.Id != projectId) return;
            if (concept.RepresentativeAssetId is not Guid assetId) continue;
            if (_conceptThumbnails.ContainsKey(assetId)) continue;
            if (!_conceptThumbnailInFlight.TryAdd(assetId, 1)) continue;
            try
            {
                var bytes = await _api.DownloadImageBytesAsync(projectId, assetId);
                if (bytes == null || SelectedProject?.Id != projectId) continue;
                var thumb = await DecodeImageAsync(bytes, decodeWidth: 240);
                _conceptThumbnails[assetId] = thumb;
                ConceptThumbnailsVersion++;
            }
            catch
            {
                // Ignore individual thumbnail failures and keep fallback placeholder.
            }
            finally
            {
                _conceptThumbnailInFlight.TryRemove(assetId, out _);
            }
        }
    }
}

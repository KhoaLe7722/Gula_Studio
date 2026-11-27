using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media.Imaging;
using CharacterConceptAI.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CharacterConceptAI.Desktop.ViewModels;

public sealed partial class ChatTurnImageItem : ObservableObject
{
    public Guid? AssetId { get; }
    public long? Seed { get; }
    public int Width { get; }
    public int Height { get; }
    public string PromptSnapshot { get; }
    public DateTimeOffset CreatedAt { get; }
    public bool IsFullQuality { get; }

    [ObservableProperty]
    private string? _localImagePath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpscaled))]
    [NotifyPropertyChangedFor(nameof(ModeBadge))]
    private string? _upscaledImagePath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayImage))]
    [NotifyPropertyChangedFor(nameof(HasImage))]
    private BitmapImage? _originalImage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayImage))]
    [NotifyPropertyChangedFor(nameof(HasUpscaled))]
    [NotifyPropertyChangedFor(nameof(ModeBadge))]
    [NotifyPropertyChangedFor(nameof(ResolutionLabel))]
    private BitmapImage? _upscaledImage;

    [ObservableProperty]
    private bool _isUpscaling;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FavoriteGlyph))]
    [NotifyPropertyChangedFor(nameof(FavoriteTooltip))]
    private bool _isFavorite;

    public ChatTurnImageItem(
        Guid? assetId,
        long? seed,
        int width,
        int height,
        string promptSnapshot,
        DateTimeOffset createdAt,
        bool isFullQuality = false)
    {
        AssetId = assetId;
        Seed = seed;
        Width = width;
        Height = height;
        PromptSnapshot = promptSnapshot;
        CreatedAt = createdAt;
        IsFullQuality = isFullQuality;
    }

    public BitmapImage? DisplayImage => UpscaledImage ?? OriginalImage;
    public bool HasImage => DisplayImage != null;
    public bool HasUpscaled => UpscaledImage != null || !string.IsNullOrWhiteSpace(UpscaledImagePath);
    public bool CanUpscale => AssetId.HasValue && !HasUpscaled && !IsFullQuality && !IsUpscaling && Width <= 1536 && Height <= 1536;
    public bool HasSeed => Seed.HasValue;
    public string SeedLabel => Seed.HasValue ? $"Seed {Seed}" : string.Empty;
    public string ResolutionLabel => UpscaledImage != null
        ? $"{UpscaledImage.PixelWidth} × {UpscaledImage.PixelHeight}"
        : $"{Width} × {Height}";
    public string ModeBadge => (HasUpscaled || IsFullQuality) ? "Full 2×" : "Chuẩn";
    public string FavoriteGlyph => IsFavorite ? "★" : "☆";
    public string FavoriteTooltip => IsFavorite ? "Bỏ yêu thích" : "Lưu vào Yêu thích";

    partial void OnIsUpscalingChanged(bool value) => OnPropertyChanged(nameof(CanUpscale));
    partial void OnUpscaledImageChanged(BitmapImage? value) => OnPropertyChanged(nameof(CanUpscale));
    partial void OnUpscaledImagePathChanged(string? value) => OnPropertyChanged(nameof(CanUpscale));
}

public sealed partial class ChatTurnItem : ObservableObject
{
    public Guid Id { get; }
    public string UserPrompt { get; }
    public int Width { get; }
    public int Height { get; }
    public bool FullQuality { get; }
    public DateTimeOffset CreatedAt { get; }
    public string? StylePreset { get; }
    public string? OverlayText { get; }

    public ObservableCollection<ChatTurnImageItem> Images { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SeedLabel))]
    [NotifyPropertyChangedFor(nameof(HasSeed))]
    private long? _seed;

    [ObservableProperty]
    private Guid? _assetId;

    [ObservableProperty]
    private string? _localImagePath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImage))]
    private BitmapImage? _image;

    [ObservableProperty]
    private bool _isGenerating;

    [ObservableProperty]
    private string _statusText = "Đang khởi tạo…";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    public ChatTurnItem(
        Guid id,
        string userPrompt,
        int width,
        int height,
        bool fullQuality,
        DateTimeOffset createdAt,
        string? stylePreset = null,
        string? overlayText = null)
    {
        Id = id;
        UserPrompt = userPrompt;
        Width = width;
        Height = height;
        FullQuality = fullQuality;
        CreatedAt = createdAt;
        StylePreset = stylePreset;
        OverlayText = overlayText;
        Images.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasImages));
            OnPropertyChanged(nameof(IsSingleImage));
            OnPropertyChanged(nameof(IsMultiImage));
            OnPropertyChanged(nameof(PrimaryImage));
            OnPropertyChanged(nameof(HasImage));
        };
    }

    public string ResolutionLabel => $"{Width} × {Height}";
    public string ModeLabel => FullQuality ? "Full 2×" : "Chuẩn";
    public double GeneratingCardWidth => ComputeGeneratingDisplaySize().W;
    public double GeneratingCardHeight => ComputeGeneratingDisplaySize().H;

    private (double W, double H) ComputeGeneratingDisplaySize()
    {
        const double maxW = 1120.0;
        const double maxH = 720.0;
        double w = Math.Max(256, Width);
        double h = Math.Max(256, Height);
        double scale = Math.Min(1.0, Math.Min(maxW / w, maxH / h));
        return (Math.Round(w * scale), Math.Round(h * scale));
    }
    public string TimeLabel => CreatedAt.ToLocalTime().ToString("HH:mm");
    public string SeedLabel => Seed.HasValue ? $"Seed {Seed}" : string.Empty;
    public bool HasSeed => Seed.HasValue;
    public bool HasImage => Image != null || Images.Count > 0;
    public bool HasImages => Images.Count > 0;
    public bool IsSingleImage => Images.Count == 1;
    public bool IsMultiImage => Images.Count > 1;
    public ChatTurnImageItem? PrimaryImage => Images.FirstOrDefault();
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasStyleBadge => !string.IsNullOrWhiteSpace(StylePreset);
    public bool HasOverlayBadge => !string.IsNullOrWhiteSpace(OverlayText);
    public string OverlayBadgeText => string.IsNullOrWhiteSpace(OverlayText) ? string.Empty : $"Aa \"{OverlayText}\"";
}

public sealed partial class ChatThreadItem : ObservableObject
{
    public Guid Id { get; }

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeBadge))]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    private DateTimeOffset _updatedAt;

    [ObservableProperty]
    private bool _isSelected;

    public DateTimeOffset CreatedAt { get; }
    public ObservableCollection<ChatTurnItem> Turns { get; } = [];

    public ChatThreadItem(Guid id, string title, DateTimeOffset createdAt, DateTimeOffset updatedAt)
    {
        Id = id;
        _title = title;
        CreatedAt = createdAt;
        _updatedAt = updatedAt;
        Turns.CollectionChanged += (_, _) => OnPropertyChanged(nameof(Subtitle));
    }

    public bool HasFavorites => Turns.Any(t => t.Images.Any(img => img.IsFavorite));

    public string TimeBadge
    {
        get
        {
            var local = UpdatedAt.ToLocalTime().Date;
            var today = DateTime.Today;
            if (local == today) return "Hôm nay";
            if (local == today.AddDays(-1)) return "Hôm qua";
            return local.ToString("dd/MM");
        }
    }

    public string Subtitle
    {
        get
        {
            var imgCount = Turns.Sum(t => t.Images.Count);
            return imgCount > 0 ? $"{TimeBadge} · {imgCount} ảnh" : $"{TimeBadge} · {Turns.Count} lượt";
        }
    }
}

public partial class MainViewModel
{
    private ChatTurnItem? _activeGeneratingTurn;
    private ChatTurnItem? _lightboxTurn;

    public ObservableCollection<ChatThreadItem> ChatThreads { get; } = [];
    public ObservableCollection<ChatThreadItem> FilteredChatThreads { get; } = [];
    public ObservableCollection<ChatTurnImageItem> FavoriteImages { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveChatTurns))]
    [NotifyPropertyChangedFor(nameof(IsChatEmpty))]
    [NotifyPropertyChangedFor(nameof(HasActiveChatTurns))]
    [NotifyPropertyChangedFor(nameof(ActiveChatTitle))]
    private ChatThreadItem? _activeChatThread;

    [ObservableProperty]
    private string _chatSearchText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChatEmpty))]
    [NotifyPropertyChangedFor(nameof(HasActiveChatTurns))]
    [NotifyPropertyChangedFor(nameof(ShowFavoritesGallery))]
    [NotifyPropertyChangedFor(nameof(IsStudioChatTabSelected))]
    [NotifyPropertyChangedFor(nameof(IsFavoritesTabSelected))]
    private bool _showFavoritesOnly;

    public event Action? ChatScrollRequested;

    public ObservableCollection<ChatTurnItem>? ActiveChatTurns => ActiveChatThread?.Turns;
    public bool ShowFavoritesGallery => ShowFavoritesOnly;
    public bool IsStudioChatTabSelected => CurrentTabIndex == 0 && !ShowFavoritesOnly;
    public bool IsFavoritesTabSelected => CurrentTabIndex == 0 && ShowFavoritesOnly;
    public bool IsChatEmpty => !ShowFavoritesOnly && (ActiveChatThread == null || ActiveChatThread.Turns.Count == 0);
    public bool HasActiveChatTurns => !ShowFavoritesOnly && ActiveChatThread != null && ActiveChatThread.Turns.Count > 0;
    public bool HasChatHistory => FilteredChatThreads.Count > 0;
    public bool HasAnyChatThreads => ChatThreads.Count > 0;
    public bool HasFavoriteImages => FavoriteImages.Count > 0;
    public string FavoritesCountLabel
    {
        get
        {
            var label = Infrastructure.Settings.LocalizationService.Instance.Get("Str.Nav.Favorites", "Yêu thích");
            return FavoriteImages.Count == 0 ? label : $"{label} ({FavoriteImages.Count})";
        }
    }
    public string ActiveChatTitle => ShowFavoritesOnly
        ? $"{Infrastructure.Settings.LocalizationService.Instance.Get("Str.Nav.Favorites", "Ảnh yêu thích")} ({FavoriteImages.Count})"
        : ActiveChatThread?.Title ?? Infrastructure.Settings.LocalizationService.Instance.Get("Str.Nav.NewConversationTitle", "Cuộc trò chuyện mới");

    private bool IsRealSession =>
        !_api.BaseUrl.Contains(".invalid", StringComparison.OrdinalIgnoreCase) &&
        !GenerationProviderLabel.Contains("SIMULATED", StringComparison.OrdinalIgnoreCase);

    partial void OnActiveChatThreadChanged(ChatThreadItem? oldValue, ChatThreadItem? newValue)
    {
        if (oldValue != null) oldValue.IsSelected = false;
        if (newValue != null) newValue.IsSelected = true;
        NotifyChatStateChanged();
    }

    partial void OnChatSearchTextChanged(string value) => RefreshFilteredChatThreads();
    partial void OnShowFavoritesOnlyChanged(bool value)
    {
        RefreshFavoritesGallery();
        NotifyChatStateChanged();
    }

    private void RefreshFilteredChatThreads()
    {
        var query = ChatSearchText?.Trim() ?? string.Empty;
        FilteredChatThreads.Clear();
        foreach (var thread in ChatThreads)
        {
            if (query.Length > 0 &&
                !thread.Title.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !thread.Turns.Any(t => t.UserPrompt.Contains(query, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            FilteredChatThreads.Add(thread);
        }
        OnPropertyChanged(nameof(HasChatHistory));
        OnPropertyChanged(nameof(HasAnyChatThreads));
    }

    private void RefreshFavoritesGallery()
    {
        var collected = new List<ChatTurnImageItem>();
        var seen = new HashSet<ChatTurnImageItem>();
        foreach (var thread in ChatThreads)
        {
            foreach (var turn in thread.Turns)
            {
                foreach (var img in turn.Images)
                {
                    if (img.IsFavorite && seen.Add(img))
                        collected.Add(img);
                }
            }
        }
        FavoriteImages.Clear();
        foreach (var img in collected.OrderByDescending(i => i.CreatedAt))
            FavoriteImages.Add(img);
        OnPropertyChanged(nameof(HasFavoriteImages));
        OnPropertyChanged(nameof(FavoritesCountLabel));
        OnPropertyChanged(nameof(ActiveChatTitle));
    }

    private void NotifyChatStateChanged()
    {
        OnPropertyChanged(nameof(ActiveChatTurns));
        OnPropertyChanged(nameof(ShowFavoritesGallery));
        OnPropertyChanged(nameof(IsStudioChatTabSelected));
        OnPropertyChanged(nameof(IsFavoritesTabSelected));
        OnPropertyChanged(nameof(IsChatEmpty));
        OnPropertyChanged(nameof(HasActiveChatTurns));
        OnPropertyChanged(nameof(HasChatHistory));
        OnPropertyChanged(nameof(HasAnyChatThreads));
        OnPropertyChanged(nameof(HasFavoriteImages));
        OnPropertyChanged(nameof(FavoritesCountLabel));
        OnPropertyChanged(nameof(ActiveChatTitle));
    }

    private void LoadLocalChatHistory()
    {
        if (!IsRealSession) return;
        LoadStudioPreferences();
        ChatThreads.Clear();
        var records = ChatHistoryStore.Load();
        foreach (var rec in records.OrderByDescending(r => r.UpdatedAt))
        {
            var thread = new ChatThreadItem(rec.Id, rec.Title, rec.CreatedAt, rec.UpdatedAt);
            foreach (var turnRec in rec.Turns)
            {
                var turn = new ChatTurnItem(
                    turnRec.Id, turnRec.UserPrompt, turnRec.Width, turnRec.Height,
                    turnRec.FullQuality, turnRec.CreatedAt, turnRec.StylePreset, turnRec.OverlayText)
                {
                    Seed = turnRec.Seed,
                    AssetId = turnRec.AssetId,
                    LocalImagePath = turnRec.LocalImagePath,
                    StatusText = turnRec.StatusText,
                    ErrorMessage = turnRec.ErrorMessage,
                    IsGenerating = false
                };

                if (turnRec.Images is { Count: > 0 })
                {
                    foreach (var imgRec in turnRec.Images)
                    {
                        turn.Images.Add(new ChatTurnImageItem(
                            imgRec.AssetId, imgRec.Seed, imgRec.Width, imgRec.Height,
                            turnRec.UserPrompt, turnRec.CreatedAt, turnRec.FullQuality)
                        {
                            LocalImagePath = imgRec.LocalImagePath,
                            UpscaledImagePath = imgRec.UpscaledImagePath,
                            IsFavorite = imgRec.IsFavorite
                        });
                    }
                }
                else if (turnRec.AssetId.HasValue || !string.IsNullOrWhiteSpace(turnRec.LocalImagePath))
                {
                    // Backwards-compatible migration for single-image turns created before multi-image batching.
                    turn.Images.Add(new ChatTurnImageItem(
                        turnRec.AssetId, turnRec.Seed, turnRec.Width, turnRec.Height,
                        turnRec.UserPrompt, turnRec.CreatedAt, turnRec.FullQuality)
                    {
                        LocalImagePath = turnRec.LocalImagePath
                    });
                }

                thread.Turns.Add(turn);
            }
            ChatThreads.Add(thread);
        }
        RefreshFilteredChatThreads();
        RefreshFavoritesGallery();
        NotifyChatStateChanged();
    }

    private ChatThreadItem? _activeGeneratingThread;
    private CancellationTokenSource? _chatThreadHydrateCts;

    public void FlushAllLocalStores()
    {
        if (!IsRealSession) return;
        SaveLocalChatHistory();
        SaveLocalStories();
        ChatHistoryStore.FlushPendingSaves();
        StoryStore.FlushPendingSaves();
    }

    public void DisposeAllResources()
    {
        FlushAllLocalStores();
        DisposeVoiceResources();
        CancelAndDispose(ref _setupDownloadCts);
        _setupService.Dispose();
        CancelAndDispose(ref _chatThreadHydrateCts);
        CancelAndDispose(ref _characterQueryCts);
        CancelAndDispose(ref _outfitGalleryQueryCts);
        CancelAndDispose(ref _outfitGalleryImagesCts);
        CancelAndDispose(ref _outfitGalleryActiveLoadCts);
        CancelAndDispose(ref _poseGalleryQueryCts);
        CancelAndDispose(ref _poseGalleryImagesCts);
        CancelAndDispose(ref _poseGalleryActiveLoadCts);
        CancelAndDispose(ref _storyBatchCts);
        CancelAndDispose(ref _storyHydrateCts);
        CancelAndDispose(ref _queryCts);
        CancelAndDispose(ref _generationMonitorCts);
        CancelAndDispose(ref _gradeDebounceCts);
    }

    private static void CancelAndDispose(ref CancellationTokenSource? cts)
    {
        var current = Interlocked.Exchange(ref cts, null);
        if (current == null) return;
        try { current.Cancel(); } catch { }
        try { current.Dispose(); } catch { }
    }

    private void SaveLocalChatHistory()
    {
        if (!IsRealSession) return;
        var records = ChatThreads.Select(t => new ChatThreadRecord(
            t.Id,
            t.Title,
            t.CreatedAt,
            t.UpdatedAt,
            t.Turns.Select(turn => new ChatTurnRecord(
                turn.Id,
                turn.UserPrompt,
                turn.Width,
                turn.Height,
                turn.FullQuality,
                turn.Seed,
                turn.AssetId,
                turn.LocalImagePath,
                turn.IsGenerating ? "Đã dừng" : turn.StatusText,
                turn.ErrorMessage,
                turn.CreatedAt,
                turn.Images.Select(img => new ChatTurnImageRecord(
                    img.AssetId,
                    img.Seed,
                    img.Width,
                    img.Height,
                    img.LocalImagePath,
                    img.UpscaledImagePath,
                    img.IsFavorite)).ToList(),
                turn.StylePreset,
                turn.OverlayText)).ToList())).ToList();
        ChatHistoryStore.SaveDebounced(records);
    }

    private static string BuildThreadTitle(string prompt)
    {
        var clean = string.Join(' ', prompt.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (string.IsNullOrWhiteSpace(clean)) return "Tác phẩm AI mới";
        return clean.Length <= 42 ? clean : clean[..41].TrimEnd() + "…";
    }

    private ChatTurnItem BeginChatTurn(string userPrompt, int width, int height, bool fullQuality, string? styleLabel = null, string? overlayText = null)
    {
        ShowFavoritesOnly = false;
        var now = DateTimeOffset.Now;
        if (ActiveChatThread == null)
        {
            var thread = new ChatThreadItem(Guid.NewGuid(), BuildThreadTitle(userPrompt), now, now);
            ChatThreads.Insert(0, thread);
            ActiveChatThread = thread;
        }
        else
        {
            ActiveChatThread.UpdatedAt = now;
            var idx = ChatThreads.IndexOf(ActiveChatThread);
            if (idx > 0) ChatThreads.Move(idx, 0);
        }

        var turn = new ChatTurnItem(Guid.NewGuid(), userPrompt, width, height, fullQuality, now, styleLabel, overlayText)
        {
            IsGenerating = true,
            StatusText = "Đang gửi yêu cầu đến GPU…"
        };
        ActiveChatThread.Turns.Add(turn);
        _activeGeneratingThread = ActiveChatThread;
        _activeGeneratingTurn = turn;
        RefreshFilteredChatThreads();
        NotifyChatStateChanged();
        ChatScrollRequested?.Invoke();
        return turn;
    }

    private void UpdateActiveChatTurnProgress(string statusText)
    {
        if (_activeGeneratingTurn != null)
            _activeGeneratingTurn.StatusText = statusText;
    }

    private void CompleteActiveChatTurnBatch(
        IReadOnlyList<(Guid AssetId, long Seed, byte[] Bytes, BitmapImage Image)> results,
        string statusText,
        bool isSimulated)
    {
        if (_activeGeneratingTurn is not { } turn || results.Count == 0) return;

        var owningThread = _activeGeneratingThread ?? ActiveChatThread;
        var first = results[0];
        turn.AssetId = first.AssetId;
        turn.Seed = first.Seed;
        turn.Image = first.Image;
        turn.Images.Clear();

        var pendingDiskSaves = new List<(ChatTurnImageItem Item, Guid AssetId, long Seed, int W, int H, byte[] Bytes)>();
        foreach (var (assetId, seed, bytes, img) in results)
        {
            var item = new ChatTurnImageItem(assetId, seed, turn.Width > 0 ? turn.Width : img.PixelWidth, turn.Height > 0 ? turn.Height : img.PixelHeight, turn.UserPrompt, turn.CreatedAt, turn.FullQuality)
            {
                OriginalImage = img
            };
            turn.Images.Add(item);
            if (!isSimulated && IsRealSession)
                pendingDiskSaves.Add((item, assetId, seed, item.Width, item.Height, bytes));
        }

        turn.IsGenerating = false;
        turn.StatusText = statusText;
        turn.ErrorMessage = null;
        if (owningThread != null) owningThread.UpdatedAt = DateTimeOffset.Now;
        _activeGeneratingTurn = null;
        _activeGeneratingThread = null;
        RefreshFilteredChatThreads();
        ChatScrollRequested?.Invoke();

        // Offload multi-MB PNG writes & auto-export off the WPF UI thread!
        if (pendingDiskSaves.Count > 0)
        {
            _ = PersistCompletedChatBatchAsync(turn, pendingDiskSaves);
        }
        else if (!isSimulated)
        {
            SaveLocalChatHistory();
        }
    }

    private async Task PersistCompletedChatBatchAsync(
        ChatTurnItem turn,
        List<(ChatTurnImageItem Item, Guid AssetId, long Seed, int W, int H, byte[] Bytes)> pending)
    {
        var savedPaths = await Task.Run(() =>
        {
            var list = new List<(ChatTurnImageItem Item, string? Path)>(pending.Count);
            foreach (var p in pending)
            {
                var path = ChatHistoryStore.SaveImageBytes(p.AssetId, p.Bytes);
                if (AutoExportEnabled && !string.IsNullOrWhiteSpace(AutoExportFolder))
                    ChatHistoryStore.AutoExportImage(AutoExportFolder, p.Seed, p.W, p.H, turn.FullQuality, p.Bytes);
                list.Add((p.Item, path));
            }
            return list;
        });

        foreach (var (item, path) in savedPaths)
            item.LocalImagePath = path;
        if (savedPaths.Count > 0)
            turn.LocalImagePath = savedPaths[0].Path;
        SaveLocalChatHistory();
    }

    private void FailActiveChatTurn(string errorText, bool isSimulated = false)
    {
        if (_activeGeneratingTurn is { } turn)
        {
            turn.IsGenerating = false;
            turn.StatusText = "Không hoàn thành";
            turn.ErrorMessage = errorText;
            _activeGeneratingTurn = null;
            _activeGeneratingThread = null;
            if (!isSimulated) SaveLocalChatHistory();
            ChatScrollRequested?.Invoke();
        }
    }

    [RelayCommand]
    private void NewChat()
    {
        ShowFavoritesOnly = false;
        ActiveChatThread = null;
        _lightboxTurn = null;
        LightboxItem = null;
        IsLightboxOpen = false;
        IsLightboxCompareMode = false;
        GeneratedImage = null;
        GenerationStatusText = "Sẵn sàng sáng tạo";
        if (CurrentTabIndex != 0) NavigateTo(0);
        NotifyChatStateChanged();
    }

    [RelayCommand]
    private async Task ToggleFavoritesModeAsync()
    {
        if (CurrentTabIndex != 0)
        {
            NavigateTo(0);
            ShowFavoritesOnly = true;
        }
        else
        {
            ShowFavoritesOnly = !ShowFavoritesOnly;
        }

        if (ShowFavoritesOnly)
        {
            RefreshFavoritesGallery();
            await HydrateFavoriteImagesAsync();
        }
    }

    [RelayCommand]
    private void ExitFavoritesMode()
    {
        ShowFavoritesOnly = false;
        if (CurrentTabIndex != 0) NavigateTo(0);
    }

    private async Task HydrateFavoriteImagesAsync()
    {
        var items = FavoriteImages.ToList();
        if (items.Count == 0) return;
        using var gate = new SemaphoreSlim(4, 4);
        var tasks = items.Select(async img =>
        {
            await gate.WaitAsync();
            try { await HydrateChatImageItemAsync(img, decodeWidth: 512); }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks);
    }

    private async Task HydrateChatImageItemAsync(ChatTurnImageItem item, int decodeWidth = 768, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested) return;
        if (item.OriginalImage == null)
        {
            if (!string.IsNullOrWhiteSpace(item.LocalImagePath) && File.Exists(item.LocalImagePath))
            {
                try
                {
                    var bytes = await File.ReadAllBytesAsync(item.LocalImagePath, ct);
                    item.OriginalImage = await DecodeImageAsync(bytes, decodeWidth: decodeWidth);
                }
                catch { }
            }
            else if (item.AssetId is Guid assetId && SelectedProject is { } proj && IsRealSession)
            {
                try
                {
                    var bytes = await _api.DownloadImageBytesAsync(proj.Id, assetId, ct);
                    if (bytes != null)
                    {
                        item.OriginalImage = await DecodeImageAsync(bytes, decodeWidth: decodeWidth);
                        item.LocalImagePath = await ChatHistoryStore.SaveImageBytesAsync(assetId, bytes);
                    }
                }
                catch { }
            }
        }

        if (item.UpscaledImage == null && !string.IsNullOrWhiteSpace(item.UpscaledImagePath) && File.Exists(item.UpscaledImagePath))
        {
            try
            {
                var upBytes = await File.ReadAllBytesAsync(item.UpscaledImagePath, ct);
                item.UpscaledImage = await DecodeImageAsync(upBytes, decodeWidth: decodeWidth);
            }
            catch { }
        }
    }

    private async Task EnsureFullResolutionChatImageAsync(ChatTurnImageItem item)
    {
        // Lazily upgrade thumbnail-decoded images to native full resolution ONLY when opened in 1:1 Lightbox
        if (!string.IsNullOrWhiteSpace(item.LocalImagePath) && File.Exists(item.LocalImagePath))
        {
            if (item.OriginalImage == null || (item.Width > 0 && item.OriginalImage.PixelWidth < item.Width))
            {
                try
                {
                    var bytes = await File.ReadAllBytesAsync(item.LocalImagePath);
                    var full = await DecodeImageAsync(bytes, decodeWidth: 0);
                    if (full != null) item.OriginalImage = full;
                }
                catch { }
            }
        }
        else if (item.OriginalImage == null)
        {
            await HydrateChatImageItemAsync(item, decodeWidth: 0);
        }

        if (!string.IsNullOrWhiteSpace(item.UpscaledImagePath) && File.Exists(item.UpscaledImagePath))
        {
            var expectedUpW = item.Width > 0 ? item.Width * 2 : 0;
            if (item.UpscaledImage == null || (expectedUpW > 0 && item.UpscaledImage.PixelWidth < expectedUpW))
            {
                try
                {
                    var upBytes = await File.ReadAllBytesAsync(item.UpscaledImagePath);
                    var fullUp = await DecodeImageAsync(upBytes, decodeWidth: 0);
                    if (fullUp != null) item.UpscaledImage = fullUp;
                }
                catch { }
            }
        }
    }

    private readonly List<Guid> _hydratedChatThreadOrder = [];
    private const int MaxHydratedChatThreadsInRam = 3;

    private void MarkThreadHydratedAndTrimRam(ChatThreadItem active)
    {
        _hydratedChatThreadOrder.Remove(active.Id);
        _hydratedChatThreadOrder.Add(active.Id);
        while (_hydratedChatThreadOrder.Count > MaxHydratedChatThreadsInRam)
        {
            var oldestId = _hydratedChatThreadOrder[0];
            _hydratedChatThreadOrder.RemoveAt(0);
            if (oldestId == active.Id) continue;
            var oldThread = ChatThreads.FirstOrDefault(t => t.Id == oldestId);
            if (oldThread == null) continue;
            foreach (var turn in oldThread.Turns)
            {
                if (turn.IsGenerating) continue;
                var allBackedOnDisk = turn.Images.Count > 0 && turn.Images.All(img =>
                    !string.IsNullOrWhiteSpace(img.LocalImagePath) && File.Exists(img.LocalImagePath));
                if (!allBackedOnDisk) continue;
                foreach (var img in turn.Images)
                {
                    if (img == LightboxItem || (ShowFavoritesOnly && img.IsFavorite)) continue;
                    img.OriginalImage = null;
                    img.UpscaledImage = null;
                }
                if (turn != _lightboxTurn && turn.Images.All(i => i.DisplayImage == null))
                    turn.Image = null;
            }
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SelectChatThreadAsync(ChatThreadItem? thread)
    {
        if (thread == null) return;
        _chatThreadHydrateCts?.Cancel();
        _chatThreadHydrateCts?.Dispose();
        _chatThreadHydrateCts = new CancellationTokenSource();
        var ct = _chatThreadHydrateCts.Token;

        ShowFavoritesOnly = false;
        ActiveChatThread = thread;
        IsLightboxOpen = false;
        IsLightboxCompareMode = false;
        if (CurrentTabIndex != 0) NavigateTo(0);

        // Hydrate thumbnails in bounded parallel batches (cancels cleanly if user clicks another thread)
        var imageJobs = thread.Turns
            .SelectMany(t => t.Images.Select(img => (Img: img, Width: t.Images.Count > 1 ? 640 : 1024)))
            .ToList();
        using (var gate = new SemaphoreSlim(4, 4))
        {
            var tasks = imageJobs.Select(async entry =>
            {
                try
                {
                    await gate.WaitAsync(ct);
                    try { await HydrateChatImageItemAsync(entry.Img, entry.Width, ct); }
                    finally { gate.Release(); }
                }
                catch (OperationCanceledException) { }
            });
            await Task.WhenAll(tasks);
        }

        if (ct.IsCancellationRequested || ActiveChatThread != thread) return;

        foreach (var turn in thread.Turns)
        {
            if (turn.Image == null && turn.Images.FirstOrDefault()?.DisplayImage is { } firstImg)
            {
                turn.Image = firstImg;
            }
        }

        var latestImg = thread.Turns.SelectMany(t => t.Images).LastOrDefault(i => i.DisplayImage != null);
        if (latestImg?.DisplayImage != null)
        {
            GeneratedImage = latestImg.DisplayImage;
        }
        MarkThreadHydratedAndTrimRam(thread);
        ChatScrollRequested?.Invoke();
    }

    [RelayCommand]
    private void DeleteChatThread(ChatThreadItem? thread)
    {
        if (thread == null) return;
        if (_activeGeneratingThread == thread)
        {
            _activeGeneratingTurn = null;
            _activeGeneratingThread = null;
        }
        if (ActiveChatThread == thread)
        {
            ActiveChatThread = null;
            GeneratedImage = null;
            IsLightboxOpen = false;
        }
        ChatThreads.Remove(thread);
        SaveLocalChatHistory();
        RefreshFilteredChatThreads();
        RefreshFavoritesGallery();
        NotifyChatStateChanged();
    }

    [RelayCommand]
    private void OpenTurnLightbox(ChatTurnItem? turn)
    {
        if (turn == null) return;
        if (turn.PrimaryImage is { } imgItem)
        {
            _ = OpenImageLightboxAsync(imgItem);
            return;
        }
        if (turn.Image == null) return;
        _lightboxTurn = turn;
        LightboxItem = null;
        IsLightboxCompareMode = false;
        GeneratedImage = turn.Image;
        NotifyLightboxChanged();
        IsLightboxOpen = true;
    }

    [RelayCommand]
    private void SaveTurnImage(ChatTurnItem? turn)
    {
        var img = turn?.PrimaryImage?.DisplayImage ?? turn?.Image;
        if (img == null) return;
        GeneratedImage = img;
        SaveCanvasImage();
    }

    [RelayCommand]
    private void CopyTurnImage(ChatTurnItem? turn)
    {
        var img = turn?.PrimaryImage?.DisplayImage ?? turn?.Image;
        if (img == null) return;
        GeneratedImage = img;
        CopyCanvasImage();
    }

    [RelayCommand]
    private void UseTurnSeed(ChatTurnItem? turn)
    {
        var seed = turn?.PrimaryImage?.Seed ?? turn?.Seed;
        if (seed is not long value) return;
        GenerationSeed = value;
        StatusMessage = $"Đã khóa Seed {value} cho lượt tạo tiếp theo.";
    }

    [RelayCommand]
    private void CopyTurnPrompt(ChatTurnItem? turn)
    {
        if (turn == null || string.IsNullOrWhiteSpace(turn.UserPrompt)) return;
        try
        {
            System.Windows.Clipboard.SetText(turn.UserPrompt);
            StatusMessage = "Đã sao chép prompt vào Clipboard!";
        }
        catch (Exception ex) { ReportError(ex); }
    }

    [RelayCommand]
    private void RemixTurn(ChatTurnItem? turn)
    {
        if (turn == null) return;
        RawPromptInput = turn.UserPrompt;
        ImageWidth = turn.Width;
        ImageHeight = turn.Height;
        IsLightboxOpen = false;
        StatusMessage = "Đã nạp lại prompt vào khung chat để chỉnh sửa hoặc tạo biến thể.";
    }

    [RelayCommand]
    private void ApplyDreaminaShowcase(string? key)
    {
        OverlayText = string.Empty;
        switch (key)
        {
            // ─── Bento Cluster 1 (7 signature hero styles) ───
            case "frost-mage":
                ImageWidth = 896; ImageHeight = 1152;
                SelectedStylePresetKey = "photoreal";
                RawPromptInput = "chân dung nữ pháp sư băng giá phong cách RPG giả tưởng cao cấp, mái tóc nâu hạt dẻ uốn sóng, áo choàng nhung xanh hoàng gia thêu hoa văn bạc và cổ lông thú trắng, bàn tay nâng tinh thể băng phát sáng lơ lửng giữa ban công lâu đài tuyết mờ sương, ánh sáng điện ảnh sắc nét";
                StatusMessage = "Đã nạp mẫu Nữ pháp sư băng giá (3:4 · RPG Giả tưởng). Nhấn Tạo ảnh để vẽ!";
                break;
            case "mars-climax":
                ImageWidth = 1280; ImageHeight = 704;
                SelectedStylePresetKey = "photoreal";
                RawPromptInput = "toàn cảnh khoa học viễn tưởng điện ảnh trên bề mặt đá đỏ Sao Hỏa, một phi hành gia đứng quay lưng đơn độc giữa vùng đất hoang, phía chân trời bầu trời đen sâu thẳm với Trái Đất có vành đai bụi bùng nổ ánh lửa rực rỡ chiếu bóng dài xuống nền cát đỏ, tương phản ánh sáng hoành tráng";
                StatusMessage = "Đã nạp mẫu Chân trời Sao Hỏa (16:9 · Sci-Fi Vũ trụ). Nhấn Tạo ảnh để vẽ!";
                break;
            case "ice-dragon":
                ImageWidth = 1152; ImageHeight = 896;
                SelectedStylePresetKey = "3d";
                RawPromptInput = "rồng băng pha lê trong suốt phát sáng sắc xanh lam huyền ảo với đôi cánh màng tinh thể lấp lánh ánh sao, sải mình nổi lên từ những con sóng biển tung bọt trắng xóa dưới ánh trăng tròn và bầu trời mây đêm kịch tính, chi tiết vảy rồng sắc nét như dựng hình 3D điện ảnh";
                StatusMessage = "Đã nạp mẫu Rồng Pha Lê dưới trăng (4:3 · Sinh vật 3D). Nhấn Tạo ảnh để vẽ!";
                break;
            case "anime-sakura":
                ImageWidth = 1024; ImageHeight = 1024;
                SelectedStylePresetKey = "anime";
                RawPromptInput = "minh họa anime điện ảnh cô gái trẻ tóc đen dài tung bay trong gió, ngoái nhìn dịu dàng bên lan can gỗ ven dòng kênh phản chiếu bầu trời xanh và hai hàng hoa anh đào nở rộ rực rỡ dưới nắng xuân trong trẻo, cánh hoa bay lơ lửng, nét vẽ sắc sảo rạng rỡ";
                StatusMessage = "Đã nạp mẫu Mùa hoa anh đào (1:1 · Minh họa Anime). Nhấn Tạo ảnh để vẽ!";
                break;
            case "venice-oil":
                ImageWidth = 1024; ImageHeight = 1024;
                SelectedStylePresetKey = "oilpaint";
                RawPromptInput = "tranh sơn dầu nghệ thuật cảnh kênh đào Venice lúc hoàng hôn vàng rực, những dãy nhà cổ mái ngói cam ấm áp soi bóng lấp lánh xuống mặt nước gợn sóng nhẹ với thuyền gondola lướt qua dưới bầu trời mây hồng cam, nét cọ đắp nổi giàu chiều sâu cổ điển";
                StatusMessage = "Đã nạp mẫu Hoàng hôn kênh Venice (1:1 · Tranh Sơn dầu). Nhấn Tạo ảnh để vẽ!";
                break;
            case "arch-villa":
                ImageWidth = 1280; ImageHeight = 704;
                SelectedStylePresetKey = "photoreal";
                RawPromptInput = "nhiếp ảnh kiến trúc đương đại biệt thự kính và gỗ tối giản vươn ra khỏi vách núi rừng thông lúc chạng vạng, hồ bơi vô cực phẳng lặng như gương phản chiếu ánh đèn nội thất vàng ấm, phía xa thung lũng mây sương giăng kín giữa rặng núi xanh thẫm, bố cục rộng cân đối";
                StatusMessage = "Đã nạp mẫu Biệt thự kính vách núi (16:9 · Kiến trúc). Nhấn Tạo ảnh để vẽ!";
                break;
            case "cyber-neon":
                ImageWidth = 896; ImageHeight = 1152;
                SelectedStylePresetKey = "cyberpunk";
                RawPromptInput = "chân dung nhân vật cyberpunk hiện đại, cô gái trẻ tóc đen thẳng mặc áo khoác da đen bó sát có các đường viền phát sáng neon xanh chanh nổi bật, đứng trên sân thượng ướt mưa giữa thành phố tương lai rực rỡ ánh đèn bokeh xanh lam và tím về đêm, tương phản điện ảnh sắc nét";
                StatusMessage = "Đã nạp mẫu Chiến binh Neon đêm mưa (3:4 · Cyberpunk). Nhấn Tạo ảnh để vẽ!";
                break;

            // ─── Bento Cluster 2 (7 additional diverse styles) ───
            case "watch-macro":
                ImageWidth = 1024; ImageHeight = 1024;
                SelectedStylePresetKey = "photoreal";
                RawPromptInput = "nhiếp ảnh sản phẩm cận cảnh cao cấp chiếc đồng hồ cơ lộ máy khung titan đen nhám với mặt số bánh răng xanh lam và vàng đồng tinh xảo, đặt trên phiến đá đen ẩm nước phản chiếu ánh sáng studio mềm mượt, độ sắc nét bề mặt kim loại cực cao";
                StatusMessage = "Đã nạp mẫu Đồng hồ cơ lộ máy (1:1 · Sản phẩm Macro). Nhấn Tạo ảnh để vẽ!";
                break;
            case "japan-train":
                ImageWidth = 1280; ImageHeight = 704;
                SelectedStylePresetKey = "film35mm";
                RawPromptInput = "khung hình phim nhựa 35mm bên trong toa tàu điện Nhật Bản vào buổi chiều muộn, ánh nắng vàng ấm xiên qua khung cửa kính chiếu dọc dãy ghế nỉ và hàng tay nắm treo, vài hành khách ngồi yên bình nhìn ra phố xa, không khí đời thường hoài niệm và giàu chiều sâu";
                StatusMessage = "Đã nạp mẫu Chuyến tàu chiều Tokyo (16:9 · Phim 35mm). Nhấn Tạo ảnh để vẽ!";
                break;
            case "aodai-lotus":
                ImageWidth = 896; ImageHeight = 1152;
                SelectedStylePresetKey = "photoreal";
                RawPromptInput = "chân dung cô gái Việt Nam duyên dáng mặc áo dài lụa trắng ngà thêu hoa văn tinh tế, ôm đóa hoa sen trắng đứng bên bờ hồ tĩnh lặng dưới tán liễu rủ và cầu gỗ đỏ xa xa trong ánh nắng sớm dịu nhẹ, tông màu tự nhiên trong trẻo";
                StatusMessage = "Đã nạp mẫu Áo dài lụa bên hồ sen (3:4 · Chân thực Việt Nam). Nhấn Tạo ảnh để vẽ!";
                break;
            case "mars-poster":
                ImageWidth = 896; ImageHeight = 1152;
                SelectedStylePresetKey = "poster";
                OverlayText = "MARS HORIZON";
                OverlayTextStyle = "modern";
                RawPromptInput = "áp phích phim điện ảnh khoa học viễn tưởng hoành tráng, chân dung chính diện nam phi hành gia trẻ mặc bộ đồ không gian trắng chi tiết giữa hẻm núi đá đỏ Sao Hỏa dưới bầu trời sao sâu thẳm, dòng chữ trắng lớn sắc nét \"MARS HORIZON\" ở phía trên và \"2026\" ở phía dưới, bố cục đồ họa điện ảnh chuyên nghiệp";
                StatusMessage = "Đã nạp mẫu Áp phích Mars Horizon (3:4 · Poster Điện ảnh). Nhấn Tạo ảnh để vẽ!";
                break;
            case "luxury-suit":
                ImageWidth = 1024; ImageHeight = 1024;
                SelectedStylePresetKey = "photoreal";
                RawPromptInput = "ảnh thời trang lookbook cao cấp nam người mẫu trẻ mặc bộ vest xanh navy may đo tinh tế kèm cà vạt lụa, tựa dáng tự tin bên chiếc xe coupe thể thao sơn xám ánh kim ngoài trời trong ánh sáng tự nhiên dịu nhẹ, chất liệu vải và phản xạ thân xe chân thực";
                StatusMessage = "Đã nạp mẫu Thời trang Suit quý ông (1:1 · Thời trang Editorial). Nhấn Tạo ảnh để vẽ!";
                break;
            case "knight-armor":
                ImageWidth = 896; ImageHeight = 1152;
                SelectedStylePresetKey = "photoreal";
                RawPromptInput = "chân dung nữ hiệp sĩ trung cổ khí chất mạnh mẽ, mái tóc đen dài buông vai, khoác bộ giáp tấm thép bạc đánh bóng tinh xảo kèm áo choàng xanh thẫm đứng trước tường thành đá cổ dưới ánh nắng chiều dịu ấm, phản chiếu kim loại chân thực và chi tiết sắc nét";
                StatusMessage = "Đã nạp mẫu Nữ hiệp sĩ thành cổ (3:4 · Giả tưởng Trung cổ). Nhấn Tạo ảnh để vẽ!";
                break;
            case "mountain-dawn":
                ImageWidth = 1280; ImageHeight = 704;
                SelectedStylePresetKey = "film35mm";
                RawPromptInput = "khung cảnh điện ảnh bình minh trên đỉnh núi cao phủ rêu xanh ẩm sương, cô gái mặc váy trắng và áo khoác len nhẹ đang bước men theo triền đá, phía sau là biển sương mờ và rừng cây chìm trong ánh mặt trời hồng cam rực rỡ lúc rạng đông, màu sắc tự nhiên giàu cảm xúc";
                StatusMessage = "Đã nạp mẫu Bình minh sương mờ đỉnh núi (16:9 · Phong cảnh điện ảnh). Nhấn Tạo ảnh để vẽ!";
                break;

            // ─── Legacy keys retained for backward compatibility ───
            case "tokyo-rain":
                ImageWidth = 896; ImageHeight = 1152;
                SelectedStylePresetKey = "photoreal";
                RawPromptInput = "chân dung điện ảnh cô gái trẻ cầm ô trong suốt giữa phố đêm Tokyo mưa nhẹ, ánh đèn neon xanh lam và hồng phản chiếu trên mặt đường ướt, bokeh mềm mượt, ánh sáng tự nhiên chân thực";
                StatusMessage = "Đã chọn mẫu Chân dung mưa Tokyo (3:4 · 896×1152). Nhấn Tạo ảnh để vẽ!";
                break;
            case "anime-knight":
                ImageWidth = 832; ImageHeight = 1216;
                SelectedStylePresetKey = "anime";
                RawPromptInput = "nữ kiếm sĩ anime tóc bạc dài bay trong gió, mặc giáp trắng viền vàng thanh lịch và áo choàng xanh thẫm, đứng trên đỉnh tháp đá lúc hoàng hôn, ánh sáng ngược vàng ấm rực rỡ, phong cách minh họa anime điện ảnh độ chi tiết cực cao";
                StatusMessage = "Đã chọn mẫu Nữ kiếm sĩ Anime (2:3 · 832×1216). Nhấn Tạo ảnh để vẽ!";
                break;
            case "cyber-mage":
                ImageWidth = 1024; ImageHeight = 1024;
                SelectedStylePresetKey = "cyberpunk";
                RawPromptInput = "pháp sư cyberpunk mặc áo khoác công nghệ tối màu phát sáng mạch rune xanh ngọc, đôi mắt phát sáng nhẹ, vòng tròn phép thuật hologram lơ lửng quanh tay, hậu cảnh thành phố tương lai mờ ảo, ánh sáng studio sắc nét";
                StatusMessage = "Đã chọn mẫu Pháp sư Cyberpunk (1:1 · 1024×1024). Nhấn Tạo ảnh để vẽ!";
                break;
            case "vn-poster":
                ImageWidth = 896; ImageHeight = 1152;
                SelectedStylePresetKey = "poster";
                OverlayText = "CÀ PHÊ SÀI GÒN";
                OverlayTextStyle = "modern";
                RawPromptInput = "áp phích quảng cáo quán cà phê hiện đại phong cách Sài Gòn, cô gái trẻ cười tươi cầm ly cà phê sữa đá trong quán ánh nắng chiều ấm áp, thiết kế đồ họa chuyên nghiệp";
                StatusMessage = "Đã chọn mẫu Poster Cà phê Sài Gòn (3:4 · 896×1152). Nhấn Tạo ảnh để vẽ!";
                break;
            case "expression-3x3":
                ApplyExpressionSheetPreset();
                break;
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowManualLogin))]
    [NotifyPropertyChangedFor(nameof(ShowLocalReconnectButton))]
    private bool _isLocalAutoConnecting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGenerate))]
    [NotifyPropertyChangedFor(nameof(GenerationBlockReason))]
    [NotifyPropertyChangedFor(nameof(CanEditProject))]
    [NotifyPropertyChangedFor(nameof(CanEditCharacter))]
    private bool _isLocalStudioMode;

    public bool ShowManualLogin => false;
    public bool ShowLocalReconnectButton => !IsLoggedIn && !IsLocalAutoConnecting;

    [RelayCommand]
    private async Task ReconnectLocalStudioAsync()
    {
        if (Uri.TryCreate(ServerUrl.TrimEnd('/'), UriKind.Absolute, out var uri))
            App.EnsureLocalBackendServicesStarted(uri);
        await StartLocalStudioAsync();
    }

    public async Task StartLocalStudioAsync(Action<double, string>? reportProgress = null)
    {
        if (IsLoggedIn) return;
        IsLocalAutoConnecting = true;
        StatusMessage = "Đang khởi động máy chủ AI cục bộ…";
        reportProgress?.Invoke(12, "Đang khởi động máy chủ Gula Studio & GPU Engine…");
        try
        {
            _api.BaseUrl = ServerUrl.TrimEnd('/');
            var serverUri = Uri.TryCreate(_api.BaseUrl, UriKind.Absolute, out var parsed) ? parsed : null;

            // Phase 1: Fast 250ms health probes (up to 35s for cold SQL wake) — cuts 2-3.5s cold startup wait!
            var healthy = false;
            var swBoot = System.Diagnostics.Stopwatch.StartNew();
            for (var attempt = 0; swBoot.Elapsed.TotalSeconds < 35; attempt++)
            {
                if (await _api.IsServerHealthyAsync())
                {
                    healthy = true;
                    break;
                }
                if (serverUri is { IsLoopback: true } && attempt > 0 && attempt % 32 == 0)
                    App.EnsureLocalBackendServicesStarted(serverUri);
                var secs = (int)swBoot.Elapsed.TotalSeconds;
                StatusMessage = secs < 3
                    ? "Đang khởi động máy chủ AI cục bộ…"
                    : $"Đang chờ máy chủ AI cục bộ khởi động… ({secs + 1}s)";
                reportProgress?.Invoke(Math.Min(32, 12 + secs * 2.5), StatusMessage);
                await Task.Delay(250);
            }

            if (healthy)
            {
                reportProgress?.Invoke(38, "Đã kết nối máy chủ · Đang xác thực phiên làm việc cục bộ…");
                // Phase 2: Acquire local session cleanly (retry up to 5 times in case DB pool is still warming up).
                for (var authAttempt = 0; authAttempt < 5; authAttempt++)
                {
                    try
                    {
                        if (await _session.TryLocalAutoLoginAsync() && _session.CurrentUser is { IsActive: true } user)
                        {
                            IsLocalStudioMode = true;
                            IsLoggedIn = true;
                            IsAdmin = _session.IsAdmin;
                            UserRoles = string.Join(", ", user.GlobalRoles);
                            CurrentUserName = user.DisplayName;
                            ApiConnectionStatus = "Đã kết nối";
                            CurrentTabIndex = 0;
                            reportProgress?.Invoke(54, "Đang nạp dự án, hồ sơ nhân vật, câu chuyện & kho LoRA…");
                            // Run capabilities & LoRA catalog discovery in parallel with workspace init
                            var capTask = LoadGenerationCapabilitiesAsync();
                            var loraTask = LoadLoraCatalogAsync();
                            await LoadProjectsAsync();
                            await EnsureAutoWorkspaceAsync();
                            await Task.WhenAll(capTask, loraTask);
                            LoadLocalChatHistory();
                            EnsureStoriesLoaded();
                            NotifyWorkspaceState();

                            // Phase 3: Real GPU model warmup (pre-loads Z-Image Turbo + Qwen3-4B + VAE during Splash!)
                            if (reportProgress != null)
                            {
                                reportProgress(74, "Đang làm nóng GPU (nạp sẵn Z-Image Turbo, Qwen3-4B & VAE)…");
                                using var warmupCts = new CancellationTokenSource(TimeSpan.FromSeconds(14));
                                var warmupTask = _api.WarmupStudioGpuAsync(warmupCts.Token);
                                // Gently advance splash bar while GPU warms up so UX feels responsive & alive
                                var warmSw = System.Diagnostics.Stopwatch.StartNew();
                                while (!warmupTask.IsCompleted && warmSw.Elapsed.TotalSeconds < 14)
                                {
                                    var pct = Math.Min(94, 74 + warmSw.Elapsed.TotalSeconds * 1.6);
                                    reportProgress(pct, $"Đang làm nóng GPU (nạp sẵn mô hình AI vào VRAM)… {(int)warmSw.Elapsed.TotalSeconds}s");
                                    await Task.WhenAny(warmupTask, Task.Delay(300));
                                }
                                if (warmupTask.IsCompletedSuccessfully && warmupTask.Result)
                                    reportProgress(98, "GPU đã nạp nóng xong — sẵn sàng vẽ ảnh đầu tiên cực nhanh!");
                                else
                                    reportProgress(98, "Hoàn tất khởi động không gian sáng tạo Gula Studio!");
                            }

                            StatusMessage = "Sẵn sàng sáng tạo — nhập ý tưởng để vẽ ngay!";
                            return;
                        }
                    }
                    catch
                    {
                        // Retry cleanly on transient DB warm-up
                    }
                    await Task.Delay(500);
                }
            }

            reportProgress?.Invoke(100, "Mở Gula Studio…");
            StatusMessage = "Máy chủ AI cục bộ chưa phản hồi. Nhấn nút bên dưới để khởi động lại và kết nối.";
        }
        finally
        {
            IsLocalAutoConnecting = false;
        }
    }

    private async Task EnsureAutoWorkspaceAsync()
    {
        if (!IsLoggedIn) return;

        // Never pick an Archived project; always resolve an Active workspace silently.
        if (SelectedProject == null || string.Equals(SelectedProject.Status, "Archived", StringComparison.OrdinalIgnoreCase))
        {
            var active = Projects.FirstOrDefault(p => !string.Equals(p.Status, "Archived", StringComparison.OrdinalIgnoreCase));
            if (active == null)
            {
                var paged = await _api.GetProjectsPagedAsync(1, 24, status: "Active");
                if (paged?.Items.Count > 0)
                {
                    foreach (var p in paged.Items)
                    {
                        if (Projects.All(existing => existing.Id != p.Id)) Projects.Insert(0, p);
                    }
                    active = paged.Items[0];
                }
            }
            if (active == null)
            {
                active = await _api.CreateProjectAsync("Gula Studio", "Concept", "Không gian sáng tạo AI cục bộ");
                if (active != null) Projects.Insert(0, active);
            }
            if (active != null)
            {
                SelectedProject = active;
            }
        }

        if (SelectedProject != null)
        {
            if (IsLocalStudioMode)
            {
                IsProjectArchived = false;
                IsProjectOwner = true;
                CanCreateInProject = true;
            }
            else
            {
                await LoadMembersAsync(SelectedProject.Id);
            }
            if (SelectedCharacter == null || SelectedCharacter.ProjectId != SelectedProject.Id)
            {
                SelectedCharacter = null;
                if (Characters.Count == 0 || Characters.All(c => c.ProjectId != SelectedProject.Id))
                    await LoadCharactersAsync(SelectedProject.Id);
                if (Characters.Count == 0)
                {
                    var createdChar = await _api.CreateCharacterAsync(SelectedProject.Id, "Tự do", "Protagonist", "Sáng tạo tự do");
                    if (createdChar != null)
                    {
                        Characters.Insert(0, createdChar);
                        SelectedCharacter = createdChar;
                    }
                }
                else if (SelectedCharacter == null)
                {
                    SelectedCharacter = Characters[0];
                }
            }
            NotifyWorkspaceState();
        }
    }
}

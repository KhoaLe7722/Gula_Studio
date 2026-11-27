using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;
using CharacterConceptAI.Shared.Contracts.Generations;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CharacterConceptAI.Desktop.ViewModels;

public sealed partial class OutfitGalleryViewVm : ObservableObject
{
    public OutfitGalleryViewDto Data { get; }
    public string ViewKey => Data.ViewKey;
    public string ViewLabel => Infrastructure.Settings.LocalizationService.Instance.CurrentLanguage switch
    {
        Infrastructure.Settings.AppLanguage.En => Data.ViewKey.ToLowerInvariant() switch
        {
            "front" => "Front",
            "three_quarter" or "3/4" or "threequarter" or "angle" => "3/4 View",
            "side" => "Side",
            "back" => "Back",
            _ => Data.ViewLabelVi switch
            {
                "Chính diện" => "Front",
                "Nghiêng 3/4" => "3/4 View",
                "Góc ngang" => "Side",
                "Phía sau" => "Back",
                _ => Data.ViewLabelVi
            }
        },
        Infrastructure.Settings.AppLanguage.Zh => Data.ViewLabelVi switch
        {
            "Chính diện" => "正面",
            "Nghiêng 3/4" => "3/4 侧面",
            "Góc ngang" => "正侧",
            "Phía sau" => "背面",
            _ => Data.ViewLabelVi
        },
        _ => Data.ViewLabelVi
    };

    [ObservableProperty]
    private bool _isActive;

    public OutfitGalleryViewVm(OutfitGalleryViewDto data, bool isActive)
    {
        Data = data;
        _isActive = isActive;
    }
}

public sealed partial class OutfitGalleryItemVm : ObservableObject
{
    public OutfitGalleryItemDto Data { get; }
    public ObservableCollection<OutfitGalleryViewVm> Views { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveViewKey))]
    [NotifyPropertyChangedFor(nameof(ActiveViewLabel))]
    private OutfitGalleryViewVm _activeView;

    [ObservableProperty]
    private BitmapImage? _previewImage;

    [ObservableProperty]
    private bool _isLoadingImage;

    public string Id => Data.Id;
    public string Code => Data.Code;
    public string NameVi => Data.NameVi;
    public string DisplayName => Infrastructure.Settings.LocalizationService.Instance.CurrentLanguage == Infrastructure.Settings.AppLanguage.En
        ? (!string.IsNullOrWhiteSpace(Data.NameOriginal) && Data.NameOriginal.All(c => c <= 127)
            ? Data.NameOriginal
            : StripVietnamesePrefix(Data.NameVi))
        : Data.NameVi;
    public string CategoryLabel => LocalizeOutfitCategory(Data.CategoryKey, Data.CategoryLabelVi);
    public string CollectionLabel => LocalizeOutfitCollection(Data.CollectionKey, Data.CollectionLabelVi);
    public string GenderLabel => LocalizeOutfitGender(Data.GenderKey, Data.GenderLabelVi);
    public bool IsMaleOutfit => string.Equals(Data.GenderKey, "male", StringComparison.OrdinalIgnoreCase);
    public bool IsNsfw => Data.IsNsfw;
    public int QualityScore => Data.QualityScore;
    public string QualityBadge => Infrastructure.Settings.LocalizationService.Instance.CurrentLanguage switch
    {
        Infrastructure.Settings.AppLanguage.En => Data.QualityScore >= 90 ? "★ Top Fidelity" : Data.QualityScore >= 78 ? "Stable" : "Experimental",
        Infrastructure.Settings.AppLanguage.Zh => Data.QualityScore >= 90 ? "★ 高还原" : Data.QualityScore >= 78 ? "稳定" : "易偏移",
        _ => string.IsNullOrWhiteSpace(Data.QualityBadge)
            ? (Data.QualityScore >= 90 ? "★ Chuẩn nhất" : Data.QualityScore >= 78 ? "Ổn định" : "Dễ lệch")
            : Data.QualityBadge
    };
    public bool ShowTopQualityBadge => Data.QualityScore >= 90;
    public bool ShowLowQualityBadge => Data.QualityScore < 78;
    public bool HasMultipleViews => Views.Count > 1;
    public string ActiveViewKey => ActiveView.ViewKey;
    public string ActiveViewLabel => ActiveView.ViewLabel;
    public string TagsSummary => Infrastructure.Settings.LocalizationService.Instance.CurrentLanguage == Infrastructure.Settings.AppLanguage.En
        ? $"{GenderLabel} · {CategoryLabel} · {Views.Count} angles"
        : (Data.TagsVi.Count == 0 ? string.Empty : string.Join(" · ", Data.TagsVi.Take(4)));

    private static string StripVietnamesePrefix(string nameVi)
    {
        var idx = nameVi.IndexOf('·');
        if (idx >= 0 && idx + 1 < nameVi.Length)
        {
            var tail = nameVi[(idx + 1)..].Trim();
            if (!string.IsNullOrWhiteSpace(tail)) return tail;
        }
        return nameVi;
    }

    internal static string LocalizeOutfitGender(string key, string fallbackVi) =>
        Infrastructure.Settings.LocalizationService.Instance.CurrentLanguage switch
        {
            Infrastructure.Settings.AppLanguage.En => key.ToLowerInvariant() switch
            {
                "" => "All",
                "male" => "Men",
                "female" => "Women",
                "unisex" => "Unisex",
                _ => fallbackVi
            },
            Infrastructure.Settings.AppLanguage.Zh => key.ToLowerInvariant() switch
            {
                "" => "全部",
                "male" => "男装",
                "female" => "女装",
                "unisex" => "中性",
                _ => fallbackVi
            },
            _ => fallbackVi
        };

    internal static string LocalizeOutfitCollection(string key, string fallbackVi) =>
        Infrastructure.Settings.LocalizationService.Instance.CurrentLanguage switch
        {
            Infrastructure.Settings.AppLanguage.En => key.ToLowerInvariant() switch
            {
                "" => "All Collections",
                "shisei_modern" => "Modern Multi-Style (CC0)",
                "shisei_fantasy" => "Fantasy & Historical (CC0)",
                "menswear_mit" or "men_modern" => "Modern Menswear (MIT)",
                "women_4seasons" or "modern_women" => "Women's 4 Seasons",
                _ => fallbackVi
            },
            _ => fallbackVi
        };

    internal static string LocalizeOutfitCategory(string key, string fallbackVi)
    {
        if (Infrastructure.Settings.LocalizationService.Instance.CurrentLanguage != Infrastructure.Settings.AppLanguage.En)
            return fallbackVi;
        return key.ToLowerInvariant() switch
        {
            "" => "All Categories",
            "shisei_md" => "Modern Fashion",
            "shisei_fm" => "Suits & Formal",
            "shisei_sp" => "Sports & Outdoor",
            "shisei_wk" => "Uniforms & Workwear",
            "shisei_in" => "Lounge & Swimwear",
            "shisei_fn" => "Fantasy · Wuxia · Sci-Fi",
            "shisei_hc" => "Historical Chinese",
            "shisei_we" => "World Traditional",
            "shisei_wh" => "Western Vintage",
            "shisei_fs" => "Festival & Stage",
            "shisei_lg" => "Lingerie (18+)",
            "spring" => "Spring · Chic",
            "summer" => "Summer · Casual",
            "autumn" => "Autumn · Layered",
            "winter" => "Winter · Outerwear",
            "party" => "Evening & Gala",
            "swim" => "Swim & Resort",
            "sexy" => "Glamour (18+)",
            "lingerie" => "Lingerie (18+)",
            _ => fallbackVi switch
            {
                "Áo khoác · Jacket Nam" => "Men's Jackets",
                "Vest · Công sở Nam" => "Men's Business Suits",
                "Streetwear · Hoodie Nam" => "Men's Streetwear & Hoodies",
                "Áo len · Thu Đông Nam" => "Men's Knitwear",
                "Sơ mi · Polo Nam" => "Men's Shirts & Polos",
                "Áo thun · Thường ngày Nam" => "Men's Casual Tees",
                "Thời trang Nam" => "Menswear",
                _ => fallbackVi
            }
        };
    }

    public OutfitGalleryItemVm(OutfitGalleryItemDto data)
    {
        Data = data;
        var srcViews = data.Views.Count > 0
            ? data.Views
            : [new OutfitGalleryViewDto("front", "Chính diện", data.ThumbUrl)];

        OutfitGalleryViewVm? firstActive = null;
        for (var i = 0; i < srcViews.Count; i++)
        {
            var vm = new OutfitGalleryViewVm(srcViews[i], i == 0);
            if (i == 0) firstActive = vm;
            Views.Add(vm);
        }
        _activeView = firstActive ?? Views[0];
    }

    public void SelectView(OutfitGalleryViewVm viewVm)
    {
        foreach (var v in Views) v.IsActive = v == viewVm;
        ActiveView = viewVm;
    }
}

public partial class MainViewModel
{
    private CancellationTokenSource? _outfitGalleryQueryCts;
    private CancellationTokenSource? _outfitGalleryImagesCts;
    private StorySceneVm? _outfitPickerTargetScene;

    // ─── Studio Chat Outfit Selection ───
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveOutfit))]
    [NotifyPropertyChangedFor(nameof(ActiveOutfitSummary))]
    private string? _activeOutfitId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveOutfitSummary))]
    private string _activeOutfitLabel = string.Empty;

    [ObservableProperty]
    private BitmapImage? _activeOutfitThumb;

    public bool HasActiveOutfit => !string.IsNullOrWhiteSpace(ActiveOutfitId);
    public string ActiveOutfitSummary => !HasActiveOutfit ? string.Empty : $"Trang phục: {ActiveOutfitLabel}";

    // ─── Outfit Gallery Modal (Curated Local Fashion & Historical Wardrobes) ───
    [ObservableProperty]
    private bool _isOutfitGalleryOpen;

    [ObservableProperty]
    private bool _isOutfitGalleryLoading;

    [ObservableProperty]
    private string _outfitGalleryTargetTitle = "Kho Trang phục · Áp dụng cho Tạo ảnh AI";

    [ObservableProperty]
    private string _outfitGallerySearchText = string.Empty;

    [ObservableProperty]
    private bool _outfitGalleryIncludeNsfw;

    [ObservableProperty]
    private string _outfitGalleryGenderFilter = string.Empty;

    [ObservableProperty]
    private string _outfitGalleryCollectionFilter = string.Empty;

    [ObservableProperty]
    private string _outfitGalleryCategoryFilter = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreviousOutfitPage))]
    [NotifyPropertyChangedFor(nameof(HasNextOutfitPage))]
    [NotifyPropertyChangedFor(nameof(OutfitGalleryPageLabel))]
    private int _outfitGalleryPage = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNextOutfitPage))]
    [NotifyPropertyChangedFor(nameof(OutfitGalleryPageLabel))]
    private int _outfitGalleryFilteredCount;

    [ObservableProperty]
    private string _outfitGalleryStatsLabel = "Kho Trang phục cục bộ";

    private const int OutfitGalleryPageSize = 18;

    public bool HasPreviousOutfitPage => OutfitGalleryPage > 1;
    public bool HasNextOutfitPage => OutfitGalleryPage * OutfitGalleryPageSize < OutfitGalleryFilteredCount;
    public bool IsOutfitGalleryEmpty => !IsOutfitGalleryLoading && OutfitGalleryItems.Count == 0;
    public string OutfitGalleryPageLabel => Infrastructure.Settings.LocalizationService.Instance.CurrentLanguage switch
    {
        Infrastructure.Settings.AppLanguage.En =>
            $"Page {OutfitGalleryPage} / {Math.Max(1, (int)Math.Ceiling(OutfitGalleryFilteredCount / (double)OutfitGalleryPageSize))} ({OutfitGalleryFilteredCount:N0} outfits)",
        Infrastructure.Settings.AppLanguage.Zh =>
            $"第 {OutfitGalleryPage} / {Math.Max(1, (int)Math.Ceiling(OutfitGalleryFilteredCount / (double)OutfitGalleryPageSize))} 页（共 {OutfitGalleryFilteredCount:N0} 套服装）",
        _ =>
            $"Trang {OutfitGalleryPage} / {Math.Max(1, (int)Math.Ceiling(OutfitGalleryFilteredCount / (double)OutfitGalleryPageSize))} ({OutfitGalleryFilteredCount:N0} trang phục)"
    };

    public event Action? OutfitGalleryScrollTopRequested;

    partial void OnIsOutfitGalleryLoadingChanged(bool value) => OnPropertyChanged(nameof(IsOutfitGalleryEmpty));

    public ObservableCollection<PoseGalleryFilterOption> OutfitGalleryGenders { get; } = [];
    public ObservableCollection<PoseGalleryFilterOption> OutfitGalleryCollections { get; } = [];
    public ObservableCollection<PoseGalleryFilterOption> OutfitGalleryCategories { get; } = [];
    public ObservableCollection<OutfitGalleryItemVm> OutfitGalleryItems { get; } = [];

    private bool _isUpdatingOutfitFilters;
    private int _outfitGalleryLoadRev;
    private CancellationTokenSource? _outfitGalleryActiveLoadCts;

    partial void OnOutfitGallerySearchTextChanged(string value)
    {
        if (!IsOutfitGalleryOpen || _isUpdatingOutfitFilters) return;
        OutfitGalleryPage = 1;
        ScheduleOutfitGalleryLoad();
    }

    partial void OnOutfitGalleryCollectionFilterChanged(string value)
    {
        if (!IsOutfitGalleryOpen || _isUpdatingOutfitFilters) return;
        OutfitGalleryPage = 1;
        _ = LoadOutfitGalleryAsync();
    }

    partial void OnOutfitGalleryCategoryFilterChanged(string value)
    {
        if (!IsOutfitGalleryOpen || _isUpdatingOutfitFilters) return;
        OutfitGalleryPage = 1;
        _ = LoadOutfitGalleryAsync();
    }

    partial void OnOutfitGalleryIncludeNsfwChanged(bool value)
    {
        if (value && !AllowNsfwContent)
        {
            OutfitGalleryIncludeNsfw = false;
            return;
        }
        if (!IsOutfitGalleryOpen) return;
        if (!value && OutfitGalleryCategoryFilter is "sexy" or "lingerie")
            OutfitGalleryCategoryFilter = string.Empty;
        OutfitGalleryPage = 1;
        _ = LoadOutfitGalleryAsync();
    }

    [RelayCommand]
    private async Task OpenOutfitGalleryForStudioAsync()
    {
        _outfitPickerTargetScene = null;
        OutfitGalleryTargetTitle = Infrastructure.Settings.LocalizationService.Instance.CurrentLanguage switch
        {
            Infrastructure.Settings.AppLanguage.En => "Outfit Library · Apply to AI Studio",
            Infrastructure.Settings.AppLanguage.Zh => "服装库 · 应用于 AI 绘图",
            _ => "Kho Trang phục · Áp dụng cho Tạo ảnh AI"
        };
        IsOutfitGalleryOpen = true;
        await LoadOutfitGalleryAsync();
    }

    [RelayCommand]
    private async Task OpenOutfitGalleryForSceneAsync(StorySceneVm? scene)
    {
        if (scene == null) return;
        _outfitPickerTargetScene = scene;
        OutfitGalleryTargetTitle = $"Chọn trang phục cho {scene.Title}";
        IsOutfitGalleryOpen = true;
        await LoadOutfitGalleryAsync();
    }

    [RelayCommand]
    private void CloseOutfitGallery()
    {
        IsOutfitGalleryOpen = false;
        _outfitPickerTargetScene = null;
    }

    [RelayCommand]
    private async Task SetOutfitGalleryGenderAsync(string? key)
    {
        OutfitGalleryGenderFilter = key ?? string.Empty;
        OutfitGalleryCollectionFilter = string.Empty;
        OutfitGalleryCategoryFilter = string.Empty;
        OutfitGalleryPage = 1;
        await LoadOutfitGalleryAsync();
    }

    [RelayCommand]
    private async Task SetOutfitGalleryCollectionAsync(string? key)
    {
        OutfitGalleryCollectionFilter = key ?? string.Empty;
        OutfitGalleryCategoryFilter = string.Empty;
        OutfitGalleryPage = 1;
        await LoadOutfitGalleryAsync();
    }

    [RelayCommand]
    private async Task SetOutfitGalleryCategoryAsync(string? key)
    {
        OutfitGalleryCategoryFilter = key ?? string.Empty;
        OutfitGalleryPage = 1;
        await LoadOutfitGalleryAsync();
    }

    [RelayCommand]
    private async Task ResetOutfitGalleryFiltersAsync()
    {
        OutfitGallerySearchText = string.Empty;
        OutfitGalleryGenderFilter = string.Empty;
        OutfitGalleryCollectionFilter = string.Empty;
        OutfitGalleryCategoryFilter = string.Empty;
        OutfitGalleryPage = 1;
        await LoadOutfitGalleryAsync();
    }

    [RelayCommand]
    private async Task PreviousOutfitGalleryPageAsync()
    {
        if (!HasPreviousOutfitPage) return;
        OutfitGalleryPage--;
        await LoadOutfitGalleryAsync();
    }

    [RelayCommand]
    private async Task NextOutfitGalleryPageAsync()
    {
        if (!HasNextOutfitPage) return;
        OutfitGalleryPage++;
        await LoadOutfitGalleryAsync();
    }

    [RelayCommand]
    private async Task SwitchOutfitCardViewAsync(OutfitGalleryViewVm? viewVm)
    {
        if (viewVm == null) return;
        var item = OutfitGalleryItems.FirstOrDefault(i => i.Views.Contains(viewVm));
        if (item == null) return;
        item.SelectView(viewVm);
        await LoadOutfitCardImageAsync(item, CancellationToken.None);
    }

    [RelayCommand]
    private async Task ApplyOutfitGalleryItemAsync(OutfitGalleryItemVm? itemVm)
    {
        if (itemVm == null) return;
        var item = itemVm.Data;
        var label = item.NameVi.Length <= 46 ? item.NameVi : item.NameVi[..45].TrimEnd() + "…";

        var thumb = itemVm.PreviewImage;
        if (thumb == null)
        {
            var bytes = await _api.DownloadOutfitGalleryImageBytesAsync(item.Id, "thumb", itemVm.ActiveViewKey);
            if (bytes != null) thumb = await DecodeSafeBitmapAsync(bytes, 240);
        }

        if (_outfitPickerTargetScene is { } targetScene)
        {
            targetScene.OutfitId = item.Id;
            targetScene.OutfitLabel = label;
            targetScene.OutfitThumb = thumb;
            SaveLocalStories();
            StatusMessage = $"Đã chọn trang phục '{label}' cho {targetScene.Title}.";
        }
        else
        {
            ActiveOutfitId = item.Id;
            ActiveOutfitLabel = label;
            ActiveOutfitThumb = thumb;

            var resizedHint = string.Empty;
            if (ImageWidth < 832 || ImageHeight < 832)
            {
                ImageWidth = 896;
                ImageHeight = 1152;
                resizedHint = " · Tự tối ưu khung 896×1152 (3:4)";
            }

            StatusMessage = $"Đã chọn trang phục '{label}' ({item.Code}){resizedHint} · Tự đồng bộ trang phục vào prompt!";
        }

        IsOutfitGalleryOpen = false;
        _outfitPickerTargetScene = null;
    }

    [RelayCommand]
    private void ClearOutfit()
    {
        ActiveOutfitId = null;
        ActiveOutfitLabel = string.Empty;
        ActiveOutfitThumb = null;
        StatusMessage = "Đã bỏ trang phục đã chọn.";
    }

    private async void ScheduleOutfitGalleryLoad()
    {
        _outfitGalleryQueryCts?.Cancel();
        _outfitGalleryQueryCts?.Dispose();
        _outfitGalleryQueryCts = new CancellationTokenSource();
        var ct = _outfitGalleryQueryCts.Token;
        try
        {
            await Task.Delay(220, ct);
            await LoadOutfitGalleryAsync(ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ReportError(ex); }
    }

    private async Task LoadOutfitGalleryAsync(CancellationToken ct = default)
    {
        _outfitGalleryActiveLoadCts?.Cancel();
        _outfitGalleryActiveLoadCts?.Dispose();
        _outfitGalleryActiveLoadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var linkedCt = _outfitGalleryActiveLoadCts.Token;
        var rev = ++_outfitGalleryLoadRev;

        IsOutfitGalleryLoading = true;
        try
        {
            var res = await _api.GetOutfitGalleryAsync(
                includeNsfw: AllowNsfwContent && OutfitGalleryIncludeNsfw,
                gender: OutfitGalleryGenderFilter,
                collection: OutfitGalleryCollectionFilter,
                category: OutfitGalleryCategoryFilter,
                query: OutfitGallerySearchText,
                page: OutfitGalleryPage,
                pageSize: OutfitGalleryPageSize,
                ct: linkedCt);

            if (linkedCt.IsCancellationRequested || rev != _outfitGalleryLoadRev || res == null) return;

            OutfitGalleryFilteredCount = res.FilteredOutfits;
            var lang = Infrastructure.Settings.LocalizationService.Instance.CurrentLanguage;
            OutfitGalleryStatsLabel = res.IsAvailable
                ? lang switch
                {
                    Infrastructure.Settings.AppLanguage.En => $"{res.TotalOutfits:N0} outfits (Men & Women)",
                    Infrastructure.Settings.AppLanguage.Zh => $"{res.TotalOutfits:N0} 套服装（男装与女装）",
                    _ => $"{res.TotalOutfits:N0} trang phục (Nam & Nữ)"
                }
                : lang == Infrastructure.Settings.AppLanguage.En ? "Local outfit library not found" : "Chưa tìm thấy kho trang phục cục bộ";

            var savedCollection = OutfitGalleryCollectionFilter;
            var savedCategory = OutfitGalleryCategoryFilter;
            _isUpdatingOutfitFilters = true;
            try
            {
                OutfitGalleryGenders.Clear();
                OutfitGalleryGenders.Add(new PoseGalleryFilterOption("", OutfitGalleryItemVm.LocalizeOutfitGender("", "Tất cả"), res.TotalOutfits));
                if (res.Genders != null)
                    foreach (var g in res.Genders)
                        OutfitGalleryGenders.Add(new PoseGalleryFilterOption(g.Key, OutfitGalleryItemVm.LocalizeOutfitGender(g.Key, g.Label), g.Count, g.IsNsfw));

                OutfitGalleryCollections.Clear();
                OutfitGalleryCollections.Add(new PoseGalleryFilterOption("", OutfitGalleryItemVm.LocalizeOutfitCollection("", "Mọi nguồn"), res.FilteredOutfits));
                foreach (var c in res.Collections)
                    OutfitGalleryCollections.Add(new PoseGalleryFilterOption(c.Key, OutfitGalleryItemVm.LocalizeOutfitCollection(c.Key, c.Label), c.Count, c.IsNsfw));
                OutfitGalleryCollectionFilter = OutfitGalleryCollections.Any(c => c.Key == savedCollection) ? savedCollection : string.Empty;

                OutfitGalleryCategories.Clear();
                OutfitGalleryCategories.Add(new PoseGalleryFilterOption("", OutfitGalleryItemVm.LocalizeOutfitCategory("", "Tất cả danh mục"), res.FilteredOutfits));
                foreach (var cat in res.Categories)
                    OutfitGalleryCategories.Add(new PoseGalleryFilterOption(cat.Key, OutfitGalleryItemVm.LocalizeOutfitCategory(cat.Key, cat.Label), cat.Count, cat.IsNsfw));
                OutfitGalleryCategoryFilter = OutfitGalleryCategories.Any(c => c.Key == savedCategory) ? savedCategory : string.Empty;
            }
            finally
            {
                _isUpdatingOutfitFilters = false;
            }
            OnPropertyChanged(nameof(OutfitGalleryCollectionFilter));
            OnPropertyChanged(nameof(OutfitGalleryCategoryFilter));

            OutfitGalleryItems.Clear();
            foreach (var it in res.Items)
                OutfitGalleryItems.Add(new OutfitGalleryItemVm(it));

            OnPropertyChanged(nameof(IsOutfitGalleryEmpty));
            OnPropertyChanged(nameof(HasPreviousOutfitPage));
            OnPropertyChanged(nameof(HasNextOutfitPage));
            OnPropertyChanged(nameof(OutfitGalleryPageLabel));
            OutfitGalleryScrollTopRequested?.Invoke();

            await RefreshOutfitGalleryImagesAsync(linkedCt);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusMessage = $"Không tải được kho trang phục: {ex.Message}"; }
        finally
        {
            if (rev == _outfitGalleryLoadRev)
                IsOutfitGalleryLoading = false;
        }
    }

    private async Task RefreshOutfitGalleryImagesAsync(CancellationToken ct = default)
    {
        _outfitGalleryImagesCts?.Cancel();
        _outfitGalleryImagesCts?.Dispose();
        _outfitGalleryImagesCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var linkedCt = _outfitGalleryImagesCts.Token;

        var snapshot = OutfitGalleryItems.ToList();
        using var gate = new SemaphoreSlim(6, 6);
        var tasks = snapshot.Select(async item =>
        {
            try { await gate.WaitAsync(linkedCt); }
            catch (OperationCanceledException) { return; }
            try { await LoadOutfitCardImageAsync(item, linkedCt); }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks);
    }

    private async Task LoadOutfitCardImageAsync(OutfitGalleryItemVm item, CancellationToken ct)
    {
        item.IsLoadingImage = true;
        try
        {
            var viewKey = item.ActiveViewKey;
            var bytes = await _api.DownloadOutfitGalleryImageBytesAsync(item.Id, "thumb", viewKey, ct);
            if (ct.IsCancellationRequested || bytes == null) return;

            var bmp = await DecodeSafeBitmapAsync(bytes, 280);
            if (!ct.IsCancellationRequested && item.ActiveViewKey == viewKey)
                item.PreviewImage = bmp;
        }
        catch (OperationCanceledException) { }
        catch { }
        finally { item.IsLoadingImage = false; }
    }
}

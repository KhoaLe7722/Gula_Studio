using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media.Imaging;
using CharacterConceptAI.Shared.Contracts.Generations;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CharacterConceptAI.Desktop.ViewModels;

public sealed partial class PoseGalleryAngleVm : ObservableObject
{
    public PoseGalleryItemDto Data { get; }
    public string Id => Data.Id;
    public string AngleKey => Data.AngleKey;
    public string AngleLabel => Data.AngleLabelVi;

    [ObservableProperty]
    private bool _isActive;

    public PoseGalleryAngleVm(PoseGalleryItemDto data, bool isActive)
    {
        Data = data;
        _isActive = isActive;
    }
}

public sealed partial class PoseGalleryGroupVm : ObservableObject
{
    public PoseGalleryGroupDto Data { get; }
    public ObservableCollection<PoseGalleryAngleVm> Angles { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActivePoseId))]
    [NotifyPropertyChangedFor(nameof(ActiveAngleLabel))]
    private PoseGalleryAngleVm _activeAngle;

    [ObservableProperty]
    private BitmapImage? _previewImage;

    [ObservableProperty]
    private bool _isLoadingImage;

    public string PoseCode => Data.PoseCode;
    public string NameVi => Data.NameVi;
    public string CategoryLabel => Data.CategoryLabelVi;
    public string SystemLabel => Data.SystemLabelVi;
    public bool IsNsfw => Data.IsNsfw;
    public int QualityScore => Data.QualityScore;
    public string QualityBadge => string.IsNullOrWhiteSpace(Data.QualityBadge) ? (Data.QualityScore >= 92 ? "★ Chuẩn nhất" : Data.QualityScore >= 82 ? "Ổn định" : "Dáng khó") : Data.QualityBadge;
    public bool ShowTopQualityBadge => Data.QualityScore >= 92;
    public bool ShowHardPoseBadge => Data.QualityScore < 82;
    public string ActivePoseId => ActiveAngle.Id;
    public string ActiveAngleLabel => ActiveAngle.AngleLabel;

    public PoseGalleryGroupVm(PoseGalleryGroupDto data, string? preferredAngleKey = null)
    {
        Data = data;
        var initial = !string.IsNullOrWhiteSpace(preferredAngleKey)
            ? data.Angles.FirstOrDefault(a => a.AngleKey.Equals(preferredAngleKey, StringComparison.OrdinalIgnoreCase)) ?? data.Angles[0]
            : data.Angles[0];

        PoseGalleryAngleVm? activeVm = null;
        foreach (var ang in data.Angles)
        {
            var isAct = ang.Id == initial.Id;
            var vm = new PoseGalleryAngleVm(ang, isAct);
            if (isAct) activeVm = vm;
            Angles.Add(vm);
        }
        _activeAngle = activeVm ?? Angles[0];
    }

    public void SelectAngle(PoseGalleryAngleVm angleVm)
    {
        foreach (var a in Angles) a.IsActive = a == angleVm;
        ActiveAngle = angleVm;
    }
}

public partial class MainViewModel
{
    private CancellationTokenSource? _poseGalleryQueryCts;
    private CancellationTokenSource? _poseGalleryImagesCts;
    private StorySceneVm? _posePickerTargetScene;
    private string? _activeStudioFaceFilePath;

    // ─── Studio Chat Depth Pose Selection ───
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveDepthPose))]
    [NotifyPropertyChangedFor(nameof(ActiveDepthPoseSummary))]
    private string? _activeDepthPoseId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveDepthPoseSummary))]
    private string _activeDepthPoseLabel = string.Empty;

    [ObservableProperty]
    private BitmapImage? _activeDepthPoseThumb;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveDepthPoseSummary))]
    private double _activeDepthStrength = 0.92;

    public bool HasActiveDepthPose => !string.IsNullOrWhiteSpace(ActiveDepthPoseId);
    public string ActiveDepthPoseSummary => !HasActiveDepthPose
        ? string.Empty
        : $"{ActiveDepthPoseLabel} · Depth {ActiveDepthStrength:0.00}";

    // ─── Studio Chat Shared Character Profile & Quick Local Face Identity Lock ───
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveStudioCharacter))]
    [NotifyPropertyChangedFor(nameof(HasStudioFaceLock))]
    [NotifyPropertyChangedFor(nameof(StudioFaceLockSummary))]
    private StoryCharacterVm? _activeStudioCharacter;

    [ObservableProperty]
    private bool _isStudioCharacterPickerOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StudioFaceLockSummary))]
    private bool _activeStudioLockBodyDna = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStudioFaceLock))]
    [NotifyPropertyChangedFor(nameof(StudioFaceLockSummary))]
    private Guid? _activeStudioFaceVersionId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StudioFaceLockSummary))]
    private string _activeStudioFaceName = string.Empty;

    [ObservableProperty]
    private BitmapImage? _activeStudioFacePreview;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StudioFaceLockSummary))]
    private double _activeStudioFaceStrength = 0.90;

    [ObservableProperty]
    private double _activeStudioFaceRestoreStrength = 0.40;

    public bool HasActiveStudioCharacter => ActiveStudioCharacter != null;
    public bool HasStudioFaceLock => ActiveStudioCharacter != null || ActiveStudioFaceVersionId.HasValue;
    public string StudioFaceLockSummary
    {
        get
        {
            if (ActiveStudioCharacter is { } ch)
            {
                var bodyTag = ActiveStudioLockBodyDna && ch.HasBodyDna ? " + Body DNA" : string.Empty;
                var faceTag = ch.HasFaceAnchor
                    ? $"Mặt ({ch.TotalFaceCount}/5 · {Math.Round(ActiveStudioFaceStrength * 100):0}%)"
                    : "Chưa có ảnh mặt";
                return $"{ch.Name} · {faceTag} + Tóc{bodyTag}";
            }
            return !HasStudioFaceLock
                ? string.Empty
                : $"Khóa mặt: {ActiveStudioFaceName} ({Math.Round(ActiveStudioFaceStrength * 100):0}%)";
        }
    }

    // ─── Depth Pose Gallery Modal (Curated 3D Poses x 5 Angles) ───
    [ObservableProperty]
    private bool _isPoseGalleryOpen;

    [ObservableProperty]
    private bool _isPoseGalleryLoading;

    [ObservableProperty]
    private string _poseGalleryTargetTitle = "Chọn tư thế & góc máy Dáng 3D";

    [ObservableProperty]
    private string _poseGallerySearchText = string.Empty;

    [ObservableProperty]
    private bool _poseGalleryIncludeNsfw;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PoseGalleryModeLabel))]
    private bool _poseGalleryDepthPreview;

    [ObservableProperty]
    private string _poseGallerySystemFilter = string.Empty;

    [ObservableProperty]
    private string _poseGalleryCategoryFilter = string.Empty;

    [ObservableProperty]
    private string _poseGalleryAngleFilter = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreviousPosePage))]
    [NotifyPropertyChangedFor(nameof(HasNextPosePage))]
    [NotifyPropertyChangedFor(nameof(PoseGalleryPageLabel))]
    private int _poseGalleryPage = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNextPosePage))]
    [NotifyPropertyChangedFor(nameof(PoseGalleryPageLabel))]
    private int _poseGalleryFilteredGroups;

    [ObservableProperty]
    private string _poseGalleryStatsLabel = "Kho Dáng 3D · 5 góc máy";

    private const int PoseGalleryPageSize = 18;

    public string PoseGalleryModeLabel => PoseGalleryDepthPreview ? "Đang xem: Bản đồ sâu (Depth)" : "Đang xem: Ma-nơ-canh 3D";
    public bool HasPreviousPosePage => PoseGalleryPage > 1;
    public bool HasNextPosePage => PoseGalleryPage * PoseGalleryPageSize < PoseGalleryFilteredGroups;
    public bool IsPoseGalleryEmpty => !IsPoseGalleryLoading && PoseGalleryGroups.Count == 0;
    public string PoseGalleryPageLabel =>
        $"Trang {PoseGalleryPage} / {Math.Max(1, (int)Math.Ceiling(PoseGalleryFilteredGroups / (double)PoseGalleryPageSize))} ({PoseGalleryFilteredGroups} nhóm dáng)";

    public event Action? PoseGalleryScrollTopRequested;

    partial void OnIsPoseGalleryLoadingChanged(bool value) => OnPropertyChanged(nameof(IsPoseGalleryEmpty));

    public ObservableCollection<PoseGalleryFilterOption> PoseGallerySystems { get; } = [];
    public ObservableCollection<PoseGalleryFilterOption> PoseGalleryCategories { get; } = [];
    public ObservableCollection<PoseGalleryFilterOption> PoseGalleryAngles { get; } = [];
    public ObservableCollection<PoseGalleryGroupVm> PoseGalleryGroups { get; } = [];

    private bool _isUpdatingPoseFilters;
    private int _poseGalleryLoadRev;
    private CancellationTokenSource? _poseGalleryActiveLoadCts;

    partial void OnPoseGallerySearchTextChanged(string value)
    {
        if (!IsPoseGalleryOpen || _isUpdatingPoseFilters) return;
        PoseGalleryPage = 1;
        SchedulePoseGalleryLoad();
    }

    partial void OnPoseGalleryCategoryFilterChanged(string value)
    {
        if (!IsPoseGalleryOpen || _isUpdatingPoseFilters) return;
        PoseGalleryPage = 1;
        _ = LoadPoseGalleryAsync();
    }

    partial void OnPoseGalleryIncludeNsfwChanged(bool value)
    {
        if (value && !AllowNsfwContent)
        {
            PoseGalleryIncludeNsfw = false;
            return;
        }
        if (!IsPoseGalleryOpen) return;
        if (!value && PoseGallerySystemFilter is "full_sexy" or "couple")
            PoseGallerySystemFilter = string.Empty;
        PoseGalleryPage = 1;
        _ = LoadPoseGalleryAsync();
    }

    partial void OnPoseGalleryDepthPreviewChanged(bool value)
    {
        if (!IsPoseGalleryOpen) return;
        _ = RefreshPoseGalleryGroupImagesAsync();
    }

    [RelayCommand]
    private async Task OpenPoseGalleryForStudioAsync()
    {
        _posePickerTargetScene = null;
        PoseGalleryTargetTitle = "Kho Dáng 3D & Góc máy Depth · Áp dụng cho Tạo ảnh AI";
        IsPoseGalleryOpen = true;
        await LoadPoseGalleryAsync();
    }

    [RelayCommand]
    private async Task OpenPoseGalleryForSceneAsync(StorySceneVm? scene)
    {
        if (scene == null) return;
        _posePickerTargetScene = scene;
        PoseGalleryTargetTitle = $"Chọn tư thế & góc máy cho {scene.Title}";
        IsPoseGalleryOpen = true;
        await LoadPoseGalleryAsync();
    }

    [RelayCommand]
    private void ClosePoseGallery()
    {
        IsPoseGalleryOpen = false;
        _posePickerTargetScene = null;
    }

    [RelayCommand]
    private async Task SetPoseGallerySystemAsync(string? key)
    {
        PoseGallerySystemFilter = key ?? string.Empty;
        PoseGalleryCategoryFilter = string.Empty;
        PoseGalleryPage = 1;
        await LoadPoseGalleryAsync();
    }

    [RelayCommand]
    private async Task SetPoseGalleryCategoryAsync(string? key)
    {
        PoseGalleryCategoryFilter = key ?? string.Empty;
        PoseGalleryPage = 1;
        await LoadPoseGalleryAsync();
    }

    [RelayCommand]
    private async Task SetPoseGalleryAngleAsync(string? key)
    {
        PoseGalleryAngleFilter = key ?? string.Empty;
        PoseGalleryPage = 1;
        await LoadPoseGalleryAsync();
    }

    [RelayCommand]
    private async Task ResetPoseGalleryFiltersAsync()
    {
        PoseGallerySearchText = string.Empty;
        PoseGallerySystemFilter = string.Empty;
        PoseGalleryCategoryFilter = string.Empty;
        PoseGalleryAngleFilter = string.Empty;
        PoseGalleryPage = 1;
        await LoadPoseGalleryAsync();
    }

    [RelayCommand]
    private void TogglePoseGalleryPreviewMode() => PoseGalleryDepthPreview = !PoseGalleryDepthPreview;

    [RelayCommand]
    private async Task PreviousPoseGalleryPageAsync()
    {
        if (!HasPreviousPosePage) return;
        PoseGalleryPage--;
        await LoadPoseGalleryAsync();
    }

    [RelayCommand]
    private async Task NextPoseGalleryPageAsync()
    {
        if (!HasNextPosePage) return;
        PoseGalleryPage++;
        await LoadPoseGalleryAsync();
    }

    [RelayCommand]
    private async Task SwitchPoseCardAngleAsync(PoseGalleryAngleVm? angleVm)
    {
        if (angleVm == null) return;
        var group = PoseGalleryGroups.FirstOrDefault(g => g.Angles.Contains(angleVm));
        if (group == null) return;
        group.SelectAngle(angleVm);
        await LoadPoseCardImageAsync(group, CancellationToken.None);
    }

    [RelayCommand]
    private async Task ApplyPoseGalleryGroupAsync(PoseGalleryGroupVm? group)
    {
        if (group == null) return;
        var item = group.ActiveAngle.Data;
        var label = $"{item.NameVi} ({item.AngleLabelVi})";

        var thumb = group.PreviewImage;
        if (thumb == null)
        {
            var bytes = await _api.DownloadPoseGalleryImageBytesAsync(item.Id, "thumb", "mannequin");
            if (bytes != null) thumb = await DecodeSafeBitmapAsync(bytes, 240);
        }

        if (_posePickerTargetScene is { } targetScene)
        {
            targetScene.DepthPoseId = item.Id;
            targetScene.DepthPoseLabel = label;
            targetScene.DepthPoseThumb = thumb;
            SaveLocalStories();
            StatusMessage = $"Đã gắn tư thế Depth '{label}' cho {targetScene.Title}.";
        }
        else
        {
            // Clear incompatible Img2Img mode cleanly
            if (HasAttachedReference) ClearAttachedReference();
            ActiveDepthPoseId = item.Id;
            ActiveDepthPoseLabel = label;
            ActiveDepthPoseThumb = thumb;

            // Auto-upgrade tiny resolutions (<832px) to native 3:4 (896x1152) so full-body faces & hands stay crisp
            var resizedHint = string.Empty;
            if (ImageWidth < 832 || ImageHeight < 832)
            {
                ImageWidth = 896;
                ImageHeight = 1152;
                resizedHint = " · Tự tối ưu khung 896×1152 (3:4)";
            }

            StatusMessage = item.SystemKey == "couple"
                ? $"Đã chọn dáng Cặp đôi '{label}'{resizedHint} — nhớ mô tả 2 nhân vật trong prompt!"
                : $"Đã chọn dáng 3D '{label}'{resizedHint} · Tự đồng bộ tư thế & góc máy với prompt!";
        }

        IsPoseGalleryOpen = false;
        _posePickerTargetScene = null;
    }

    [RelayCommand]
    private void ClearDepthPose()
    {
        ActiveDepthPoseId = null;
        ActiveDepthPoseLabel = string.Empty;
        ActiveDepthPoseThumb = null;
        StatusMessage = "Đã bỏ tư thế Depth.";
    }

    [RelayCommand]
    private async Task BrowseStudioFaceLockAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Chọn ảnh gương mặt nhân vật để khóa đồng nhất (Local 0-VRAM)",
            Filter = "Hình ảnh (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg"
        };
        if (dialog.ShowDialog() != true) return;
        await AttachStudioFaceLockFromFileAsync(dialog.FileName);
    }

    [RelayCommand]
    private void ToggleStudioCharacterPicker()
    {
        EnsureStoriesLoaded();
        RefreshAllSavedCharacters();
        IsStudioCharacterPickerOpen = !IsStudioCharacterPickerOpen;
    }

    [RelayCommand]
    private void SelectStudioCharacter(StoryCharacterVm? character)
    {
        if (character == null) return;
        ActiveStudioCharacter = character;
        SelectedPromptMode = PromptMode.Raw;
        ActiveStudioFaceVersionId = character.FaceConceptVersionId;
        ActiveStudioFaceName = character.Name;
        _activeStudioFaceFilePath = character.FaceImagePath;
        ActiveStudioFacePreview = character.FacePreview;
        ActiveStudioFaceStrength = character.FaceSwapStrength;
        ActiveStudioFaceRestoreStrength = character.FaceRestoreStrength;
        ActiveStudioLockBodyDna = character.EnableBodyDnaLock;
        IsStudioCharacterPickerOpen = false;
        StatusMessage = $"🔒 Đã khóa nhân vật '{character.Name}' (HyperSwap 256 + Tóc{(character.HasBodyDna && character.EnableBodyDnaLock ? " & Chỉ số cơ thể" : "")} DNA) cho mọi prompt tiếp theo!";
    }

    [RelayCommand]
    private void OpenActiveStudioCharacterInStory()
    {
        EnsureStoriesLoaded();
        if (ActiveStudioCharacter != null)
        {
            var ownerStory = Stories.FirstOrDefault(s => s.Characters.Contains(ActiveStudioCharacter));
            if (ownerStory != null) ActiveStory = ownerStory;
            SelectedStoryCharacter = ActiveStudioCharacter;
        }
        IsStudioCharacterPickerOpen = false;
        NavigateTo(12);
    }

    private Guid? _activeStudioFaceUploadedProjectId;

    private async Task EnsureActiveStudioFaceLockUploadedAsync(CancellationToken ct, bool forceReupload = false)
    {
        if (SelectedProject == null || SelectedCharacter == null || SelectedCharacter.ProjectId != SelectedProject.Id)
            await EnsureAutoWorkspaceAsync();
        if (SelectedProject == null || SelectedCharacter == null) return;

        var currentProjectId = SelectedProject.Id;
        var currentCharacterId = SelectedCharacter.Id;

        if (ActiveStudioCharacter is { EnableFaceLock: true } ch)
        {
            var needsPrimaryUpload = (forceReupload || ch.FaceConceptVersionId == null || ch.FaceUploadedProjectId != currentProjectId)
                && !string.IsNullOrWhiteSpace(ch.FaceImagePath) && File.Exists(ch.FaceImagePath);
            if (needsPrimaryUpload)
            {
                var bytes = await File.ReadAllBytesAsync(ch.FaceImagePath!, ct);
                var uploaded = await _api.UploadReferenceImageAsync(currentProjectId, currentCharacterId, bytes, Path.GetFileName(ch.FaceImagePath!), ct);
                if (uploaded != null)
                {
                    ch.FaceConceptVersionId = uploaded.ConceptVersionId;
                    ch.FaceUploadedProjectId = currentProjectId;
                    ActiveStudioFaceVersionId = uploaded.ConceptVersionId;
                    _activeStudioFaceUploadedProjectId = currentProjectId;
                    SaveLocalStories();
                }
            }
            else if (ch.FaceConceptVersionId.HasValue)
            {
                ActiveStudioFaceVersionId = ch.FaceConceptVersionId;
            }

            var extraUploaded = false;
            foreach (var extra in ch.ExtraFaces.Take(StoryCharacterVm.MaxExtraFaces))
            {
                var needsExtraUpload = (forceReupload || extra.ConceptVersionId == null || extra.UploadedProjectId != currentProjectId)
                    && !string.IsNullOrWhiteSpace(extra.ImagePath) && File.Exists(extra.ImagePath);
                if (needsExtraUpload)
                {
                    var bytes = await File.ReadAllBytesAsync(extra.ImagePath!, ct);
                    var uploaded = await _api.UploadReferenceImageAsync(currentProjectId, currentCharacterId, bytes, Path.GetFileName(extra.ImagePath!), ct);
                    if (uploaded != null)
                    {
                        extra.ConceptVersionId = uploaded.ConceptVersionId;
                        extra.UploadedProjectId = currentProjectId;
                        extraUploaded = true;
                    }
                }
            }
            if (extraUploaded) SaveLocalStories();
            return;
        }

        // Standalone face-lock image attached from local file (without StoryCharacter)
        if (!string.IsNullOrWhiteSpace(_activeStudioFaceFilePath) && File.Exists(_activeStudioFaceFilePath) &&
            (forceReupload || !ActiveStudioFaceVersionId.HasValue || _activeStudioFaceUploadedProjectId != currentProjectId))
        {
            var bytes = await File.ReadAllBytesAsync(_activeStudioFaceFilePath, ct);
            var uploaded = await _api.UploadReferenceImageAsync(currentProjectId, currentCharacterId, bytes, Path.GetFileName(_activeStudioFaceFilePath), ct);
            if (uploaded != null)
            {
                ActiveStudioFaceVersionId = uploaded.ConceptVersionId;
                _activeStudioFaceUploadedProjectId = currentProjectId;
            }
        }
    }

    private Guid? GetActiveStudioPrimaryFaceId()
    {
        if (ActiveStudioCharacter is { } ch)
            return ch.EnableFaceLock ? ch.FaceConceptVersionId : null;
        return ActiveStudioFaceVersionId;
    }

    private IReadOnlyList<Guid>? GetActiveStudioAdditionalFaceIds()
    {
        var primaryId = GetActiveStudioPrimaryFaceId();
        if (!primaryId.HasValue || ActiveStudioCharacter is not { EnableFaceLock: true, ExtraFaces.Count: > 0 } ch)
            return null;
        var extras = ch.ExtraFaces
            .Where(a => a.ConceptVersionId.HasValue && a.ConceptVersionId.Value != primaryId.Value)
            .Select(a => a.ConceptVersionId!.Value)
            .Distinct()
            .Take(StoryCharacterVm.MaxExtraFaces)
            .ToList();
        return extras.Count > 0 ? extras : null;
    }

    public async Task AttachStudioFaceLockFromFileAsync(string filePath)
    {
        if (!File.Exists(filePath)) return;
        if (SelectedProject == null || SelectedCharacter == null) await EnsureAutoWorkspaceAsync();
        if (SelectedProject == null || SelectedCharacter == null)
        {
            StatusMessage = "Chưa kết nối được máy chủ cục bộ.";
            return;
        }
        try
        {
            var bytes = await File.ReadAllBytesAsync(filePath);
            var uploaded = await _api.UploadReferenceImageAsync(SelectedProject.Id, SelectedCharacter.Id, bytes, Path.GetFileName(filePath));
            if (uploaded == null)
            {
                StatusMessage = ApiError("Không tải được ảnh gương mặt lên máy chủ.");
                return;
            }
            ActiveStudioCharacter = null;
            ActiveStudioFaceVersionId = uploaded.ConceptVersionId;
            _activeStudioFaceUploadedProjectId = SelectedProject.Id;
            ActiveStudioFaceName = Path.GetFileNameWithoutExtension(filePath);
            _activeStudioFaceFilePath = filePath;
            ActiveStudioFacePreview = await DecodeImageAsync(bytes, 220);
            ActiveStudioFaceRestoreStrength = 0.40;
            StatusMessage = $"Đã khóa gương mặt '{ActiveStudioFaceName}' (HyperSwap 256 Local · 0 VRAM) cho lượt vẽ tiếp theo!";
        }
        catch (Exception ex) { ReportError(ex); }
    }

    [RelayCommand]
    private async Task OpenStudioFaceLockLightboxAsync()
    {
        if (!HasStudioFaceLock) return;
        BitmapImage? fullImg = null;
        var path = ActiveStudioCharacter?.FaceImagePath ?? _activeStudioFaceFilePath;
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(path);
                fullImg = await DecodeSafeBitmapAsync(bytes, 0);
            }
            catch { }
        }
        fullImg ??= ActiveStudioCharacter?.FacePreview ?? ActiveStudioFacePreview;
        if (fullImg == null) return;

        _lightboxTurn = null;
        LightboxItem = null;
        IsLightboxCompareMode = false;
        GeneratedImage = fullImg;
        NotifyLightboxChanged();
        IsLightboxOpen = true;
    }

    [RelayCommand]
    private void ClearStudioFaceLock()
    {
        ActiveStudioCharacter = null;
        SecondaryStudioCharacter = null;
        ActiveStudioFaceVersionId = null;
        ActiveStudioFaceName = string.Empty;
        _activeStudioFaceFilePath = null;
        ActiveStudioFacePreview = null;
        StatusMessage = "Đã tắt khóa nhân vật & gương mặt trong khung chat.";
    }

    private async void SchedulePoseGalleryLoad()
    {
        _poseGalleryQueryCts?.Cancel();
        _poseGalleryQueryCts?.Dispose();
        _poseGalleryQueryCts = new CancellationTokenSource();
        var ct = _poseGalleryQueryCts.Token;
        try
        {
            await Task.Delay(220, ct);
            await LoadPoseGalleryAsync(ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ReportError(ex); }
    }

    private async Task LoadPoseGalleryAsync(CancellationToken ct = default)
    {
        _poseGalleryActiveLoadCts?.Cancel();
        _poseGalleryActiveLoadCts?.Dispose();
        _poseGalleryActiveLoadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var linkedCt = _poseGalleryActiveLoadCts.Token;
        var rev = ++_poseGalleryLoadRev;

        IsPoseGalleryLoading = true;
        try
        {
            var res = await _api.GetPoseGalleryAsync(
                includeNsfw: AllowNsfwContent && PoseGalleryIncludeNsfw,
                system: PoseGallerySystemFilter,
                category: PoseGalleryCategoryFilter,
                angle: PoseGalleryAngleFilter,
                query: PoseGallerySearchText,
                page: PoseGalleryPage,
                pageSize: PoseGalleryPageSize,
                ct: linkedCt);

            if (linkedCt.IsCancellationRequested || rev != _poseGalleryLoadRev || res == null) return;

            PoseGalleryFilteredGroups = res.FilteredGroups;
            PoseGalleryStatsLabel = res.IsAvailable
                ? $"{res.TotalPoses:N0} tư thế ({res.TotalGroups} nhóm × 5 góc máy)"
                : "Chưa tìm thấy kho tư thế cục bộ";

            var savedCategory = PoseGalleryCategoryFilter;
            _isUpdatingPoseFilters = true;
            try
            {
                PoseGallerySystems.Clear();
                PoseGallerySystems.Add(new PoseGalleryFilterOption("", "Tất cả hệ thống", res.TotalGroups));
                foreach (var s in res.Systems) PoseGallerySystems.Add(s);

                PoseGalleryCategories.Clear();
                PoseGalleryCategories.Add(new PoseGalleryFilterOption("", "Tất cả thể loại", res.FilteredGroups));
                foreach (var c in res.Categories) PoseGalleryCategories.Add(c);
                PoseGalleryCategoryFilter = PoseGalleryCategories.Any(c => c.Key == savedCategory) ? savedCategory : string.Empty;

                PoseGalleryAngles.Clear();
                PoseGalleryAngles.Add(new PoseGalleryFilterOption("", "Mọi góc máy", res.TotalPoses));
                foreach (var a in res.Angles) PoseGalleryAngles.Add(a);
            }
            finally
            {
                _isUpdatingPoseFilters = false;
            }
            OnPropertyChanged(nameof(PoseGalleryCategoryFilter));

            PoseGalleryGroups.Clear();
            foreach (var g in res.Groups)
                PoseGalleryGroups.Add(new PoseGalleryGroupVm(g, PoseGalleryAngleFilter));

            OnPropertyChanged(nameof(IsPoseGalleryEmpty));
            OnPropertyChanged(nameof(HasPreviousPosePage));
            OnPropertyChanged(nameof(HasNextPosePage));
            OnPropertyChanged(nameof(PoseGalleryPageLabel));
            PoseGalleryScrollTopRequested?.Invoke();

            await RefreshPoseGalleryGroupImagesAsync(linkedCt);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusMessage = $"Không tải được kho tư thế: {ex.Message}"; }
        finally
        {
            if (rev == _poseGalleryLoadRev)
                IsPoseGalleryLoading = false;
        }
    }

    private async Task RefreshPoseGalleryGroupImagesAsync(CancellationToken ct = default)
    {
        _poseGalleryImagesCts?.Cancel();
        _poseGalleryImagesCts?.Dispose();
        _poseGalleryImagesCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var linkedCt = _poseGalleryImagesCts.Token;

        var snapshot = PoseGalleryGroups.ToList();
        using var gate = new SemaphoreSlim(6, 6);
        var tasks = snapshot.Select(async g =>
        {
            try
            {
                await gate.WaitAsync(linkedCt);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            try
            {
                await LoadPoseCardImageAsync(g, linkedCt);
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks);
    }

    private async Task LoadPoseCardImageAsync(PoseGalleryGroupVm group, CancellationToken ct)
    {
        group.IsLoadingImage = true;
        try
        {
            var mode = PoseGalleryDepthPreview ? "depth" : "mannequin";
            var poseId = group.ActivePoseId;
            var bytes = await _api.DownloadPoseGalleryImageBytesAsync(poseId, "thumb", mode, ct);
            if (ct.IsCancellationRequested || bytes == null) return;

            var bmp = await DecodeSafeBitmapAsync(bytes, 280);
            if (bmp == null && mode == "depth")
            {
                // Fallback gracefully to JPG mannequin thumb if OS WIC lacks WebP codec
                var fallbackBytes = await _api.DownloadPoseGalleryImageBytesAsync(poseId, "thumb", "mannequin", ct);
                if (fallbackBytes != null) bmp = await DecodeSafeBitmapAsync(fallbackBytes, 280);
            }
            if (!ct.IsCancellationRequested && group.ActivePoseId == poseId)
                group.PreviewImage = bmp;
        }
        catch (OperationCanceledException) { }
        catch { }
        finally { group.IsLoadingImage = false; }
    }

    private static Task<BitmapImage?> DecodeSafeBitmapAsync(byte[] bytes, int decodeWidth) => Task.Run(() =>
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            if (decodeWidth > 0) bitmap.DecodePixelWidth = decodeWidth;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    });
}

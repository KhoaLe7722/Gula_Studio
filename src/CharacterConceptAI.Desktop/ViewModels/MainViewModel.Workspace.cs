using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using CharacterConceptAI.Desktop.Infrastructure.Settings;
using CharacterConceptAI.Shared.Contracts.Auth;
using CharacterConceptAI.Shared.Contracts.Characters;
using CharacterConceptAI.Shared.Contracts.Generations;
using CharacterConceptAI.Shared.Contracts.Projects;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CharacterConceptAI.Desktop.ViewModels;

// Workspace state is kept independently of the XAML layout, so changing views preserves filters and drafts.
public partial class MainViewModel
{
    private CancellationTokenSource? _queryCts;
    private CancellationTokenSource? _generationMonitorCts;
    private int _projectQueryRevision;
    private int _scopeVersion;
    private readonly Stack<int> _backHistory = new();
    private readonly Stack<int> _forwardHistory = new();
    private bool _historyNavigation;
    private bool _resettingEditor;
    private bool _restoringSelection;
    private string? _lastPreviewKey;
    private string _projectEditorInitialSignature = string.Empty;
    private string ProjectEditorSignature => $"{QuickProjectName}\u001f{QuickProjectType}\u001f{QuickProjectDescription}";
    [ObservableProperty] private long? _generationSeed;

    private string CurrentPromptKey() => $"{SelectedProject?.Id}/{SelectedCharacter?.Id}/{SelectedCharacter?.RowVersion}/{SelectedVariant?.Id}/{SelectedPromptMode}/{RawPromptInput}/{SelectedStylePresetKey}/{SelectedLightingPresetKey}/{SelectedCameraPresetKey}/{OverlayText}/{ActiveStudioCharacter?.Id}/{SecondaryStudioCharacter?.Id}/{ActiveOutfitId}/{ActiveDepthPoseId}/{ActiveLorasBadge}";

    public ICollectionView FilteredProjects { get; private set; } = null!;
    public ICollectionView FilteredCharacters { get; private set; } = null!;
    public ICollectionView FilteredJobs { get; private set; } = null!;
    public string[] JobStatusFilters { get; } = ["Tất cả", "Pending", "Running", "Completed", "Failed", "Cancelled", "Interrupted"];
    [ObservableProperty] private string _jobStatusFilter = "Tất cả";
    public string[] ProjectStatusFilters { get; } = ["Tất cả", "Đang hoạt động", "Đã lưu trữ"];
    public string[] ProjectSortOptions { get; } = ["Mới cập nhật", "Tên A–Z"];
    public string[] MemberRoleOptions { get; } = ["Owner", "Designer", "Viewer"];
    [ObservableProperty] private ProjectMemberDto? _selectedProjectMember;
    [ObservableProperty] private string _selectedMemberRole = "Designer";
    public string ProjectEmptyTitle => string.IsNullOrWhiteSpace(ProjectSearchText) && ProjectStatusFilter == "Tất cả" ? "Chưa có dự án" : "Không tìm thấy kết quả";
    public string ProjectEmptyDescription => ProjectEmptyTitle == "Chưa có dự án"
        ? "Tạo dự án đầu tiên để bắt đầu quản lý nhân vật và concept." : "Thử thay đổi từ khóa hoặc xóa bộ lọc để tìm dự án.";
    public ObservableCollection<ProjectDto> RecentProjects { get; } = [];
    public string[] AvailableAttributeTypes { get; } = ["Text", "Number", "Boolean", "SingleSelect", "MultiSelect"];

    [ObservableProperty] private string _projectSearchText = string.Empty;
    [ObservableProperty] private string _characterSearchText = string.Empty;
    [ObservableProperty] private string _projectStatusFilter = "Tất cả";
    [ObservableProperty] private string _projectSort = "Mới cập nhật";
    [ObservableProperty] private bool _isProjectListView;
    [ObservableProperty] private bool _isCharacterListView;
    [ObservableProperty] private int _projectPage = 1;
    [ObservableProperty] private int _projectTotalCount;
    [ObservableProperty] private bool _isEditingProject;
    [ObservableProperty] private string _projectRole = "Chưa chọn dự án";
    [ObservableProperty] private string _apiConnectionStatus = "Chưa kết nối";
    [ObservableProperty] private bool _isImg2ImgAvailable;
    [ObservableProperty] private string _img2ImgAvailabilityReason = "Chưa sẵn sàng trên cấu hình AI hiện tại. Cần kiểm thử workflow ảnh tham chiếu trước khi sử dụng.";
    [ObservableProperty] private string _generationProviderLabel = "Chưa kiểm tra cấu hình AI";
    [ObservableProperty] private string _editorError = string.Empty;

    public bool IsDarkTheme => ThemeService.Instance.CurrentTheme == AppTheme.Dark;
    public string ThemeLabel => IsDarkTheme
        ? LocalizationService.Instance.Get("Str.Theme.SwitchToLight", "Chuyển sang giao diện sáng")
        : LocalizationService.Instance.Get("Str.Theme.SwitchToDark", "Chuyển sang giao diện tối");
    public string CurrentThemeDisplayLabel => IsDarkTheme
        ? LocalizationService.Instance.Get("Str.Theme.DarkDesc", "Giao diện tối (Đen #000000 · #B2CB25)")
        : LocalizationService.Instance.Get("Str.Theme.LightDesc", "Giao diện sáng (Trắng · Đen #000000 · #B2CB25)");

    public string CurrentLanguageCode => LocalizationService.Instance.CurrentLanguageCode;
    public string CurrentLanguageShortBadge => LocalizationService.Instance.ShortBadge;
    public string CurrentLanguageDisplayLabel => LocalizationService.Instance.CurrentDisplayName;
    public bool IsLanguageVi => LocalizationService.Instance.CurrentLanguage == AppLanguage.Vi;
    public bool IsLanguageEn => LocalizationService.Instance.CurrentLanguage == AppLanguage.En;
    public bool IsLanguageZh => LocalizationService.Instance.CurrentLanguage == AppLanguage.Zh;

    [RelayCommand]
    private void SetLanguage(string? languageCode)
    {
        LocalizationService.Instance.SetLanguageByCode(languageCode, persist: IsRealSession);
        StatusMessage = LocalizationService.Instance.CurrentLanguage switch
        {
            AppLanguage.En => "Display language switched to English.",
            AppLanguage.Zh => "界面语言已切换为简体中文。",
            _ => "Đã chuyển ngôn ngữ giao diện sang Tiếng Việt."
        };
    }

    [RelayCommand]
    private void CycleLanguage()
    {
        LocalizationService.Instance.CycleLanguage();
        StatusMessage = LocalizationService.Instance.CurrentLanguage switch
        {
            AppLanguage.En => "Display language switched to English.",
            AppLanguage.Zh => "界面语言已切换为简体中文。",
            _ => "Đã chuyển ngôn ngữ giao diện sang Tiếng Việt."
        };
    }
    public bool CanLogin => !IsBusy && !IsLoggedIn;
    public bool CanSaveProjectDialog => IsLoggedIn && !IsBusy && (!IsEditingProject || IsProjectOwner);
    public bool CanEditProject => IsLoggedIn && (IsLocalStudioMode || CanCreateInProject) && !IsBusy;
    public bool CanEditCharacter => CanEditProject && SelectedCharacter != null;
    public bool HasSelectedCharacter => SelectedCharacter != null;
    public bool CanSubmitCharacter => CanEditProject && IsCreateCharacterDialogOpen && !string.IsNullOrWhiteSpace(NewCharacterName);
    public string ProjectEditHint => IsLocalStudioMode ? string.Empty
        : SelectedProject == null ? Infrastructure.Settings.LocalizationService.Instance.CurrentLanguage switch
        {
            Infrastructure.Settings.AppLanguage.En => "Open a project to continue.",
            Infrastructure.Settings.AppLanguage.Zh => "请先打开一个项目以继续。",
            _ => "Mở một dự án để tiếp tục."
        }
        : IsProjectArchived ? "Khôi phục dự án để chỉnh sửa."
        : !CanCreateInProject && ProjectRole == "Viewer" ? "Bạn có quyền xem. Chủ dự án có thể cấp quyền thiết kế."
        : !CanCreateInProject ? ProjectRole
        : string.Empty;
    public string GenerationBlockReason => IsLocalStudioMode
        ? (IsGenerating ? "Đang sáng tạo tác phẩm trên GPU…"
            : IsImg2ImgMode && !IsImg2ImgAvailable ? Img2ImgAvailabilityReason
            : string.Empty)
        : !string.IsNullOrEmpty(ProjectEditHint) ? ProjectEditHint
        : SelectedCharacter == null ? "Chọn hoặc tạo nhân vật để sinh concept."
        : IsGenerating ? "Đang theo dõi tác vụ sinh ảnh."
        : IsImg2ImgMode && !IsImg2ImgAvailable ? Img2ImgAvailabilityReason
        : string.Empty;
    public string CharacterSelectionHint => SelectedProject == null ? "Mở một dự án để xem nhân vật."
        : CharacterSearchText.Length > 0 ? "Hãy chọn kết quả phù hợp hoặc xóa từ khóa tìm kiếm."
        : "Chọn một hồ sơ trong danh sách hoặc tạo nhân vật mới.";
    public string StudioCharacterHint => SelectedProject == null ? "Mở một dự án để chọn nhân vật."
        : Characters.Count == 0 && CharacterSearchText.Length > 0 ? "Danh sách đang được lọc. Vào trang Nhân vật để xóa từ khóa."
        : Characters.Count == 0 ? "Tạo nhân vật đầu tiên để bắt đầu sinh concept."
        : "Chọn nhân vật hoặc tìm hồ sơ khác ở trang Nhân vật.";
    public bool CanReviewSelectedVersion => CanManageProject && SelectedVersion?.Status == CharacterConceptAI.Shared.Contracts.Concepts.ConceptVersionStatus.Submitted;
    public bool CanSubmitSelectedVersion => CanEditProject && SelectedVersion?.Status is CharacterConceptAI.Shared.Contracts.Concepts.ConceptVersionStatus.Draft or CharacterConceptAI.Shared.Contracts.Concepts.ConceptVersionStatus.Rejected;
    public bool CanSelectRepresentative => CanEditProject && SelectedVersion != null;
    public bool CanArchiveSelectedProject => CanManageProject && !IsProjectArchived;
    public bool CanRestoreSelectedProject => CanManageProject && IsProjectArchived;
    public bool CanManageProject => IsLoggedIn && IsProjectOwner && !IsBusy;
    public bool CanAdminister => IsLoggedIn && IsAdmin && !IsBusy;
    public bool HasPreviousProjectPage => ProjectPage > 1;
    public bool HasNextProjectPage => ProjectPage * 24 < ProjectTotalCount;
    public string ProjectPageLabel => $"Trang {ProjectPage} / {Math.Max(1, (int)Math.Ceiling(ProjectTotalCount / 24d))} · {ProjectTotalCount} dự án";
    public string ProjectEditorTitle => IsEditingProject ? "Chỉnh sửa dự án" : "Tạo dự án mới";
    public string PageTitle => CurrentTabIndex switch
    {
        0 => "AI Studio", 1 => "Nhân vật", 2 => "Bộ sưu tập", 3 => "Kịch bản & phân cảnh",
        4 => "Thành viên", 5 => "Cài đặt", 6 => "Dự án", 7 => "Hàng đợi tác vụ",
        8 => "Thành phần giao diện", 9 => "Quản trị tài khoản", 10 => "Gần đây", 11 => "Tổng quan dự án",
        12 => "Câu chuyện & Storyboard", _ => "Không gian sáng tạo"
    };
    public string Breadcrumb => SelectedProject != null && CurrentTabIndex is not (5 or 6 or 8 or 9 or 10)
        ? $"Dự án  /  {SelectedProject.Name}  /  {PageTitle}" : $"Không gian làm việc  /  {PageTitle}";

    private void InitializeWorkspace()
    {
        FilteredProjects = new ListCollectionView(Projects);
        FilteredCharacters = new ListCollectionView(Characters);
        FilteredJobs = new ListCollectionView(GenerationQueue);
        FilteredJobs.Filter = value => value is GenerationJobDto job && (JobStatusFilter == "Tất cả" || job.Status.ToString() == JobStatusFilter);
        FilteredCharacters.Filter = value => value is CharacterDto c && (string.IsNullOrWhiteSpace(CharacterSearchText)
            || c.Name.Contains(CharacterSearchText, StringComparison.CurrentCultureIgnoreCase)
            || c.Role.Contains(CharacterSearchText, StringComparison.CurrentCultureIgnoreCase));
        ThemeService.Instance.ThemeChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsDarkTheme));
            OnPropertyChanged(nameof(ThemeLabel));
            OnPropertyChanged(nameof(CurrentThemeDisplayLabel));
        };
        LocalizationService.Instance.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CurrentLanguageCode));
            OnPropertyChanged(nameof(CurrentLanguageShortBadge));
            OnPropertyChanged(nameof(CurrentLanguageDisplayLabel));
            OnPropertyChanged(nameof(IsLanguageVi));
            OnPropertyChanged(nameof(IsLanguageEn));
            OnPropertyChanged(nameof(IsLanguageZh));
            OnPropertyChanged(nameof(ThemeLabel));
            OnPropertyChanged(nameof(CurrentThemeDisplayLabel));
            OnPropertyChanged(nameof(FavoritesCountLabel));
            OnPropertyChanged(nameof(ActiveChatTitle));
            OnPropertyChanged(nameof(NsfwSafetyStatusTitle));
            OnPropertyChanged(nameof(NsfwSafetyStatusDetail));
            OnPropertyChanged(nameof(StudioOptionsSummary));
            OnPropertyChanged(nameof(AvailableStylePresets));
            OnPropertyChanged(nameof(AvailableLightingPresets));
            OnPropertyChanged(nameof(AvailableCameraPresets));
            OnPropertyChanged(nameof(AvailableFilmPresets));
            OnPropertyChanged(nameof(ActiveLorasBadge));
            OnPropertyChanged(nameof(StoryReaderButtonLabel));
            OnPropertyChanged(nameof(OutfitGalleryPageLabel));
            OnPropertyChanged(nameof(QualityModeHint));
            OnPropertyChanged(nameof(ProjectEditHint));
            RefreshSetupLocalizedStrings();
        };
        if (IsRealSession)
            LoadStudioPreferences();
        else
            ThemeService.Instance.SetFontScale(UiFontScale);
        _session.SessionChanged += () =>
        {
            if (!_session.IsAuthenticated && IsLoggedIn)
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    if (IsLocalStudioMode)
                    {
                        IsLoggedIn = false;
                        Observe(ReconnectLocalStudioAsync());
                    }
                    else
                    {
                        Logout();
                        StatusMessage = "Phiên làm việc đã ngắt kết nối. Nhấn kết nối lại để tiếp tục.";
                    }
                });
        };
        _api.RequestRejected += status =>
        {
            if (status != System.Net.HttpStatusCode.Forbidden || !IsLoggedIn) return;
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                // Permission loss invalidates cached project affordances immediately. The server remains authoritative.
                HasUnsavedChanges = HasUnsavedScriptChanges = HasUnsavedSceneChanges = false;
                SelectedProject = null;
                CanCreateInProject = IsProjectOwner = false;
                EditorError = StatusMessage = "Bạn không còn quyền thực hiện thao tác này. Hãy tải lại danh sách dự án.";
                CurrentTabIndex = 6;
            });
        };
        Projects.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ProjectPageLabel));
        Characters.CollectionChanged += (_, _) => OnPropertyChanged(nameof(StudioCharacterHint));
        RefreshLibraryViews();
    }

    partial void OnIsBusyChanged(bool value) => NotifyWorkspaceState();
    partial void OnIsLoggedInChanged(bool value) => NotifyWorkspaceState();
    partial void OnCanCreateInProjectChanged(bool value) => NotifyWorkspaceState();
    partial void OnIsProjectOwnerChanged(bool value) => NotifyWorkspaceState();
    partial void OnIsAdminChanged(bool value) => NotifyWorkspaceState();
    partial void OnIsGeneratingChanged(bool value) => NotifyWorkspaceState();
    partial void OnIsImg2ImgModeChanged(bool value) => NotifyWorkspaceState();
    partial void OnIsImg2ImgAvailableChanged(bool value) => NotifyWorkspaceState();
    partial void OnIsEditingProjectChanged(bool value) => OnPropertyChanged(nameof(ProjectEditorTitle));
    partial void OnProjectTotalCountChanged(int value) => NotifyPagination();
    partial void OnProjectPageChanged(int value) => NotifyPagination();
    partial void OnProjectSearchTextChanged(string value) => ScheduleProjectQuery();
    partial void OnProjectStatusFilterChanged(string value) => ScheduleProjectQuery();
    partial void OnProjectSortChanged(string value) { RefreshLibraryViews(); ScheduleProjectQuery(); }
    partial void OnCharacterSearchTextChanged(string value)
    {
        FilteredCharacters?.Refresh();
        OnPropertyChanged(nameof(CharacterSelectionHint));
        OnPropertyChanged(nameof(StudioCharacterHint));
        ScheduleCharacterQuery();
    }
    partial void OnProjectRoleChanged(string value)
    {
        OnPropertyChanged(nameof(ProjectEditHint));
        OnPropertyChanged(nameof(GenerationBlockReason));
    }

    partial void OnImg2ImgAvailabilityReasonChanged(string value) => OnPropertyChanged(nameof(GenerationBlockReason));
    partial void OnJobStatusFilterChanged(string value) => FilteredJobs?.Refresh();

    partial void OnCurrentTabIndexChanged(int oldValue, int newValue)
    {
        if (!_historyNavigation) { _backHistory.Push(oldValue); _forwardHistory.Clear(); }
        if (newValue != 0 && ShowFavoritesOnly) ShowFavoritesOnly = false;
        OnPropertyChanged(nameof(PageTitle)); OnPropertyChanged(nameof(Breadcrumb));
        OnPropertyChanged(nameof(IsStudioChatTabSelected)); OnPropertyChanged(nameof(IsFavoritesTabSelected));
        NavigateBackCommand.NotifyCanExecuteChanged(); NavigateForwardCommand.NotifyCanExecuteChanged();
        if (newValue == 9 && IsAdmin) Observe(LoadAccountsAsync());
        if (newValue == 7 && SelectedProject != null) Observe(LoadQueueAsync(SelectedProject.Id));
        if (newValue == 2 && SelectedProject != null) Observe(RefreshConceptsAsync());
        if (newValue == 12) EnsureStoriesLoaded();
    }

    private IRelayCommand[]? _cachedRelayCommands;

    private void NotifyWorkspaceState()
    {
        foreach (var property in new[] { nameof(CanLogin), nameof(CanEditProject), nameof(CanEditCharacter), nameof(HasSelectedCharacter), nameof(CanSubmitCharacter), nameof(ProjectEditHint), nameof(GenerationBlockReason), nameof(CharacterSelectionHint), nameof(StudioCharacterHint), nameof(CanManageProject), nameof(CanReviewSelectedVersion), nameof(CanSubmitSelectedVersion), nameof(CanSelectRepresentative), nameof(CanArchiveSelectedProject), nameof(CanRestoreSelectedProject), nameof(CanSaveAttributes), nameof(CanSaveVariantAttributes), nameof(CanAdminister), nameof(CanGenerate), nameof(Breadcrumb) })
            OnPropertyChanged(property);
        // Generated command getters lazily create commands. Cache resolved command instances to avoid repeated reflection.
        _cachedRelayCommands ??= GetType()
            .GetProperties()
            .Where(p => typeof(IRelayCommand).IsAssignableFrom(p.PropertyType))
            .Select(p => p.GetValue(this))
            .OfType<IRelayCommand>()
            .ToArray();
        foreach (var relay in _cachedRelayCommands)
            relay.NotifyCanExecuteChanged();
    }

    private void NotifyPagination()
    {
        OnPropertyChanged(nameof(ProjectPageLabel)); OnPropertyChanged(nameof(HasPreviousProjectPage)); OnPropertyChanged(nameof(HasNextProjectPage));
        PreviousProjectPageCommand.NotifyCanExecuteChanged(); NextProjectPageCommand.NotifyCanExecuteChanged();
    }

    private void RefreshLibraryViews()
    {
        if (FilteredProjects == null) return;
        using (FilteredProjects.DeferRefresh())
        {
            FilteredProjects.SortDescriptions.Clear();
            FilteredProjects.SortDescriptions.Add(ProjectSort == "Tên A–Z"
                ? new SortDescription(nameof(ProjectDto.Name), ListSortDirection.Ascending)
                : new SortDescription(nameof(ProjectDto.UpdatedAt), ListSortDirection.Descending));
        }
        FilteredCharacters?.Refresh();
    }

    private async void ScheduleProjectQuery()
    {
        OnPropertyChanged(nameof(ProjectEmptyTitle)); OnPropertyChanged(nameof(ProjectEmptyDescription));
        _queryCts?.Cancel(); _queryCts?.Dispose();
        _queryCts = new CancellationTokenSource();
        var ct = _queryCts.Token;
        ++_projectQueryRevision; // Invalidate an in-flight result immediately, before debounce.
        try { await Task.Delay(300, ct); if (!IsLoggedIn) return; ProjectPage = 1; await LoadProjectsAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ReportError(ex); }
    }

    [RelayCommand] private void SetProjectView(string? view) => IsProjectListView = view == "List";
    [RelayCommand] private void SetCharacterView(string? view) => IsCharacterListView = view == "List";
    [RelayCommand] private void ToggleProjectView() => IsProjectListView = !IsProjectListView;
    [RelayCommand] private void ToggleCharacterView() => IsCharacterListView = !IsCharacterListView;
    [RelayCommand] private void ToggleTheme() => ThemeService.Instance.ToggleTheme();
    [RelayCommand] private void DismissError() => EditorError = string.Empty;
    [RelayCommand] private Task RefreshProjectsAsync() => LoadProjectsAsync();
    [RelayCommand] private void ClearProjectFilters() { ProjectSearchText = string.Empty; ProjectStatusFilter = "Tất cả"; }
    [RelayCommand(CanExecute = nameof(HasPreviousProjectPage))]
    private async Task PreviousProjectPageAsync() { ProjectPage--; await LoadProjectsAsync(); }
    [RelayCommand(CanExecute = nameof(HasNextProjectPage))]
    private async Task NextProjectPageAsync() { ProjectPage++; await LoadProjectsAsync(); }

    [RelayCommand]
    private void OpenProject(ProjectDto? project)
    {
        if (project == null || !ConfirmDiscardChanges()) return;
        SelectedProject = project;
        var recent = RecentProjects.FirstOrDefault(p => p.Id == project.Id);
        if (recent != null) RecentProjects.Remove(recent);
        RecentProjects.Insert(0, project);
        while (RecentProjects.Count > 12) RecentProjects.RemoveAt(RecentProjects.Count - 1);
        NavigateTo(11);
    }

    [RelayCommand]
    private void OpenCharacter(CharacterDto? character)
    {
        if (character == null || !ConfirmDiscardChanges()) return;
        SelectedCharacter = character;
        NavigateTo(1);
    }

    [RelayCommand(CanExecute = nameof(CanManageProject))]
    private void OpenEditProject()
    {
        if (SelectedProject == null || !ConfirmDiscardChanges()) return;
        IsEditingProject = true; QuickProjectName = SelectedProject.Name; QuickProjectType = SelectedProject.ProjectType;
        QuickProjectDescription = SelectedProject.Description ?? string.Empty; EditorError = string.Empty;
        _projectEditorInitialSignature = ProjectEditorSignature;
        IsCreateProjectDialogOpen = true;
    }

    public void NavigateTo(int index)
    {
        if (index is < 0 or > 12 || (index == 9 && !IsAdmin) || !ConfirmDiscardChanges()) return;
        CurrentTabIndex = index;
    }
    private bool CanGoBack => _backHistory.Count > 0;
    private bool CanGoForward => _forwardHistory.Count > 0;
    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void NavigateBack()
    {
        if (!ConfirmDiscardChanges() || _backHistory.Count == 0) return;
        _forwardHistory.Push(CurrentTabIndex); _historyNavigation = true;
        try { CurrentTabIndex = _backHistory.Pop(); } finally { _historyNavigation = false; }
    }
    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void NavigateForward()
    {
        if (!ConfirmDiscardChanges() || _forwardHistory.Count == 0) return;
        _backHistory.Push(CurrentTabIndex); _historyNavigation = true;
        try { CurrentTabIndex = _forwardHistory.Pop(); } finally { _historyNavigation = false; }
    }

    public bool ConfirmDiscardChanges()
    {
        var projectDialogDirty = IsCreateProjectDialogOpen && ProjectEditorSignature != _projectEditorInitialSignature;
        var characterDialogDirty = IsCreateCharacterDialogOpen
            && (!string.IsNullOrWhiteSpace(NewCharacterName) || !string.IsNullOrWhiteSpace(NewCharacterDescription));
        if (!HasUnsavedChanges && !HasUnsavedScriptChanges && !HasUnsavedSceneChanges && !projectDialogDirty && !characterDialogDirty) return true;
        var result = MessageBox.Show("Có thay đổi chưa lưu. Bỏ các thay đổi và tiếp tục?", "Thay đổi chưa lưu", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (result != MessageBoxResult.Yes) return false;
        ResetCharacterEditor();
        ResetScriptAndSceneEditors();
        if (projectDialogDirty) _projectEditorInitialSignature = ProjectEditorSignature;
        return true;
    }

    private bool TryValidateServerUrl()
    {
        if (!Uri.TryCreate(ServerUrl, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0) return false;
        return uri.Scheme == "https" || (uri.Scheme == "http" && uri.IsLoopback);
    }

    [RelayCommand]
    private async Task RefreshConnectionAsync()
    {
        if (!TryValidateServerUrl()) { StatusMessage = "Địa chỉ máy chủ cần dùng HTTPS; HTTP chỉ dành cho máy cục bộ."; return; }
        IsBusy = true;
        try
        {
            if (!_api.BaseUrl.Contains(".invalid", StringComparison.OrdinalIgnoreCase))
                _api.BaseUrl = ServerUrl.TrimEnd('/');
            // Use an independent client; do not forward a signed-in token to a changed server address.
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            using var response = await http.GetAsync($"{ServerUrl.TrimEnd('/')}/api/v1/health");
            ApiConnectionStatus = response.IsSuccessStatusCode ? "Máy chủ có phản hồi" : $"Máy chủ phản hồi HTTP {(int)response.StatusCode}";
            StatusMessage = ApiConnectionStatus;
        }
        catch (Exception ex) { ReportError(ex); }
        finally { IsBusy = false; }
    }

    private async Task LoadGenerationCapabilitiesAsync()
    {
        var result = await _api.GetGenerationCapabilitiesAsync();
        if (!IsLoggedIn) return;
        if (result == null)
        {
            IsImg2ImgAvailable = false;
            IsFullQualityAvailable = false;
            if (_api.LastStatusCode == System.Net.HttpStatusCode.NotFound)
                EditorError = "Máy chủ đang dùng chưa hỗ trợ giao diện mới. Cần khởi động lại server bằng bản cập nhật để tải thư viện và hàng đợi đầy đủ.";
            return;
        }
        IsImg2ImgAvailable = result.Img2ImgAvailable;
        IsFullQualityAvailable = result.FullQualityAvailable;
        GenerationProviderLabel = result.IsSimulated ? $"{result.Provider} · SIMULATED" : result.Provider;
        Img2ImgAvailabilityReason = result.Img2ImgStatus;
    }

    [RelayCommand]
    private Task RefreshQueueAsync() => SelectedProject == null ? Task.CompletedTask : LoadQueueAsync(SelectedProject.Id);

    partial void OnSelectedProjectMemberChanged(ProjectMemberDto? value) => SelectedMemberRole = value?.Role ?? "Designer";

    [RelayCommand(CanExecute = nameof(CanManageProject))]
    private async Task ChangeMemberRoleAsync()
    {
        if (SelectedProject == null || SelectedProjectMember == null || !MemberRoleOptions.Contains(SelectedMemberRole)) return;
        var projectId = SelectedProject.Id; var member = SelectedProjectMember; var role = SelectedMemberRole;
        if (member.Role == role) return;
        if (MessageBox.Show($"Đổi vai trò của {member.DisplayName} thành {role}?", "Đổi quyền thành viên", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        IsBusy = true;
        try
        {
            if (await _api.UpdateProjectMemberRoleAsync(projectId,member.UserId,role)) { await LoadMembersAsync(projectId); StatusMessage = "Đã cập nhật quyền thành viên."; }
            else EditorError = StatusMessage = ApiError("Không đổi được quyền. Dự án phải còn ít nhất một Owner.");
        }
        catch (Exception ex) { ReportError(ex); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanManageProject))]
    private async Task TransferOwnershipAsync()
    {
        if (SelectedProject == null || SelectedProjectMember == null || SelectedProjectMember.UserId == _session.CurrentUser?.Id) return;
        var projectId = SelectedProject.Id; var target = SelectedProjectMember;
        if (MessageBox.Show($"Chuyển quyền chủ dự án cho {target.DisplayName}? Quyền của bạn sẽ được máy chủ đánh giá lại.", "Chuyển quyền chủ dự án", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        IsBusy = true;
        try
        {
            if (await _api.TransferOwnershipAsync(projectId,target.UserId)) { await LoadMembersAsync(projectId); StatusMessage = "Đã chuyển quyền chủ dự án."; }
            else EditorError = StatusMessage = ApiError("Không chuyển được quyền chủ dự án.");
        }
        catch (Exception ex) { ReportError(ex); }
        finally { IsBusy = false; }
    }

    private async void Observe(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ReportError(ex); }
    }

    private string ApiError(string fallback) => string.IsNullOrWhiteSpace(_api.LastErrorMessage) ? fallback : _api.LastErrorMessage;
    private void ReportError(Exception error)
    {
        StatusMessage = error is HttpRequestException or TaskCanceledException
            ? "Không kết nối được máy chủ. Kiểm tra mạng LAN hoặc địa chỉ máy chủ rồi thử lại."
            : "Không thực hiện được thao tác. Thông tin chưa lưu được giữ lại; hãy thử lại.";
        if (error is HttpRequestException or TaskCanceledException) ApiConnectionStatus = "Mất kết nối";
        EditorError = StatusMessage;
    }

    private static Task<BitmapImage> DecodeImageAsync(byte[] bytes, int decodeWidth = 1600) => Task.Run(() =>
    {
        var (nativeWidth, _) = TryReadPngDimensions(bytes);
        using var stream = new MemoryStream(bytes);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        if (decodeWidth > 0 && (nativeWidth == 0 || nativeWidth > decodeWidth))
            bitmap.DecodePixelWidth = decodeWidth;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    });

    internal static (int Width, int Height) TryReadPngDimensions(byte[] bytes)
    {
        if (bytes.Length >= 24 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            var w = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
            var h = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
            return (w, h);
        }
        return (0, 0);
    }

    public static string JobStatusLabel(GenerationStatus status) => status switch
    {
        GenerationStatus.Pending => "Đang chờ xử lý", GenerationStatus.Running => "Đang sinh ảnh",
        GenerationStatus.Completed => "Hoàn thành", GenerationStatus.Failed => "Thất bại",
        GenerationStatus.Cancelled => "Đã hủy", GenerationStatus.Interrupted => "Bị gián đoạn", _ => status.ToString()
    };

    public ObservableCollection<AccountDto> Accounts { get; } = [];
    [ObservableProperty] private string _accountEmail = string.Empty;
    [ObservableProperty] private string _accountDisplayName = string.Empty;
    [ObservableProperty] private string _accountPassword = string.Empty;
    [ObservableProperty] private AccountDto? _selectedAccount;
    [ObservableProperty] private string _resetAccountPassword = string.Empty;

    [RelayCommand(CanExecute = nameof(CanAdminister))]
    private async Task LoadAccountsAsync()
    {
        IsBusy = true;
        try
        {
            var result = await _api.GetAccountsAsync(1, 100);
            if (!IsLoggedIn || !IsAdmin) return;
            if (result == null) { StatusMessage = ApiError("Không tải được tài khoản."); return; }
            Accounts.Clear(); foreach (var account in result.Items) Accounts.Add(account);
        }
        catch (Exception ex) { ReportError(ex); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanAdminister))]
    private async Task CreateAccountAsync()
    {
        if (!AccountEmail.Contains('@') || string.IsNullOrWhiteSpace(AccountDisplayName) || AccountPassword.Length < 12)
        { StatusMessage = "Nhập email, tên hiển thị và mật khẩu ít nhất 12 ký tự (chữ hoa, thường, số, ký hiệu)."; return; }
        IsBusy = true;
        try
        {
            var result = await _api.CreateAccountAsync(new CreateAccountRequest(AccountEmail.Trim(), AccountDisplayName.Trim(), AccountPassword));
            AccountPassword = string.Empty;
            if (result == null) { StatusMessage = ApiError("Không tạo được tài khoản."); return; }
            Accounts.Add(result); AccountEmail = AccountDisplayName = string.Empty; StatusMessage = "Đã tạo tài khoản.";
        }
        catch (Exception ex) { ReportError(ex); }
        finally { AccountPassword = string.Empty; IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanAdminister))]
    private async Task ToggleAccountStatusAsync(AccountDto? account)
    {
        if (account == null || account.Id == _session.CurrentUser?.Id) { StatusMessage = "Không thể vô hiệu hóa chính tài khoản đang dùng."; return; }
        if (MessageBox.Show($"Đổi trạng thái tài khoản {account.DisplayName}?", "Trạng thái tài khoản", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        IsBusy = true;
        try
        {
            if (await _api.UpdateAccountStatusAsync(account.Id, !account.IsActive)) { await LoadAccountsAsync(); StatusMessage = "Đã cập nhật trạng thái tài khoản."; }
            else StatusMessage = ApiError("Không đổi được trạng thái tài khoản.");
        }
        catch (Exception ex) { ReportError(ex); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanAdminister))]
    private async Task ResetAccountPasswordAsync()
    {
        if (SelectedAccount == null || ResetAccountPassword.Length < 12) { StatusMessage = "Chọn tài khoản và nhập mật khẩu mới ít nhất 12 ký tự."; return; }
        IsBusy = true;
        try
        {
            StatusMessage = await _api.ResetPasswordAsync(SelectedAccount.Id, ResetAccountPassword) ? "Đã đặt lại mật khẩu." : ApiError("Không đặt lại được mật khẩu.");
        }
        catch (Exception ex) { ReportError(ex); }
        finally { ResetAccountPassword = string.Empty; IsBusy = false; }
    }
}

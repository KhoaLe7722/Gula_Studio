using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CharacterConceptAI.Desktop.Services;
using CharacterConceptAI.Shared.Contracts.Characters;
using CharacterConceptAI.Shared.Contracts.Generations;
using CharacterConceptAI.Shared.Contracts.Projects;
using CharacterConceptAI.Shared.Contracts.Scripts;
using CharacterConceptAI.Shared.Contracts.Concepts;

namespace CharacterConceptAI.Desktop.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly ApiClient _api;
    private readonly AuthSessionService _session;

    [ObservableProperty]
    private string _serverUrl = "http://127.0.0.1:5000";

    [ObservableProperty]
    private string _email = string.Empty;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowManualLogin))]
    [NotifyPropertyChangedFor(nameof(ShowLocalReconnectButton))]
    private bool _isLoggedIn;

    [ObservableProperty]
    private bool _isAdmin;

    [ObservableProperty]
    private string _userRoles = string.Empty;

    [ObservableProperty]
    private string _currentUserName = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = "Đang kết nối máy chủ AI cục bộ…";

    // Projects
    public ObservableCollection<ProjectDto> Projects { get; } = [];

    [ObservableProperty]
    private ProjectDto? _selectedProject;

    [ObservableProperty]
    private string _newProjectName = string.Empty;

    public string[] AvailableProjectTypes { get; } = ["Game", "Anime", "Manga", "Novel"];
    public string[] AvailableMemberRoles { get; } = ["Owner", "Designer", "Viewer"];
    public string[] AvailableCharacterRoles { get; } = ["Protagonist", "Lead Heroine", "Antagonist", "Supporting"];

    [ObservableProperty]
    private string _newProjectType = "Game";

    // Quick Project Creator Dialog Modal
    [ObservableProperty]
    private bool _isCreateProjectDialogOpen;

    [ObservableProperty]
    private string _quickProjectName = string.Empty;

    [ObservableProperty]
    private string _quickProjectType = "Game";

    [ObservableProperty]
    private string _quickProjectDescription = string.Empty;

    // Team & Members
    public ObservableCollection<ProjectMemberDto> ProjectMembers { get; } = [];

    [ObservableProperty]
    private string _newMemberEmail = string.Empty;

    [ObservableProperty]
    private string _newMemberRole = "Designer";

    [ObservableProperty]
    private bool _isProjectOwner;

    [ObservableProperty]
    private bool _canCreateInProject;

    [ObservableProperty]
    private bool _isProjectArchived;

    // Characters & Structured Attributes Profile
    public ObservableCollection<CharacterDto> Characters { get; } = [];

    [ObservableProperty]
    private CharacterDto? _selectedCharacter;

    public ObservableCollection<CharacterAttributeValueDto> SelectedCharacterAttributes { get; } = [];
    public ObservableCollection<CharacterVariantDto> SelectedCharacterVariants { get; } = [];
    public ObservableCollection<CharacterRevisionDto> SelectedCharacterRevisions { get; } = [];

    [ObservableProperty]
    private CharacterVariantDto? _selectedVariant;

    [ObservableProperty]
    private CharacterRevisionDto? _selectedRevision;

    [ObservableProperty]
    private string _revisionDiffSummary = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCharacterCommand))]
    private string _newCharacterName = string.Empty;

    [ObservableProperty]
    private string _newCharacterRole = "Protagonist";

    [ObservableProperty]
    private string _newCharacterDescription = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCharacterCommand))]
    private bool _isCreateCharacterDialogOpen;

    [ObservableProperty]
    private string _characterCreateError = string.Empty;

    private CancellationTokenSource? _characterQueryCtsOnCreate => _characterQueryCts;

    // Scripts & Scenes
    public ObservableCollection<ScriptDto> Scripts { get; } = [];

    [ObservableProperty]
    private ScriptDto? _selectedScript;

    [ObservableProperty]
    private string _newScriptTitle = string.Empty;

    public ObservableCollection<SceneDto> Scenes { get; } = [];

    [ObservableProperty]
    private SceneDto? _selectedScene;

    [ObservableProperty]
    private string _newSceneTitle = string.Empty;

    [ObservableProperty]
    private string _newSceneSetting = string.Empty;

    [ObservableProperty]
    private string _newSceneContent = string.Empty;

    // Concept Art & Approvals
    public ObservableCollection<CharacterConceptDto> CharacterConcepts { get; } = [];

    [ObservableProperty]
    private CharacterConceptDto? _selectedConcept;

    public ObservableCollection<ConceptVersionDto> ConceptVersions { get; } = [];

    [ObservableProperty]
    private ConceptVersionDto? _selectedVersion;

    [ObservableProperty]
    private ConceptVersionDto? _compareVersionLeft;

    [ObservableProperty]
    private ConceptVersionDto? _compareVersionRight;

    [ObservableProperty]
    private ConceptComparisonDto? _comparisonResult;

    [ObservableProperty]
    private string _newConceptTitle = "Concept Thiết Kế Nhân Vật";

    [ObservableProperty]
    private string _approvalComment = string.Empty;

    [ObservableProperty]
    private BitmapImage? _conceptVersionImage;

    [ObservableProperty]
    private BitmapImage? _compareLeftImage;

    [ObservableProperty]
    private BitmapImage? _compareRightImage;

    // Img2Img & Consistency
    [ObservableProperty]
    private bool _isImg2ImgMode;

    [ObservableProperty]
    private ConceptVersionDto? _selectedReferenceVersion;

    [ObservableProperty]
    private BitmapImage? _referencePreviewImage;

    [ObservableProperty]
    private double _denoise = 0.6;

    // Prompt & Generation
    public PromptMode[] AvailablePromptModes { get; } = [PromptMode.Hybrid, PromptMode.Structured, PromptMode.Raw];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedPromptModeDescription))]
    private PromptMode _selectedPromptMode = PromptMode.Raw;

    public string SelectedPromptModeDescription => SelectedPromptMode switch
    {
        PromptMode.Hybrid => "Hybrid (Khuyên Dùng): Tự động kết hợp đặc điểm nhận dạng khuôn mặt, tóc, mắt của nhân vật với mô tả ý tưởng trang phục mới của bạn.",
        PromptMode.Raw => "Raw (Tự Do): AI chỉ vẽ theo chính xác những gì bạn gõ vào khung mô tả bên dưới (bỏ qua hồ sơ nhân vật).",
        PromptMode.Structured => "Structured (Mẫu Chuẩn): AI chỉ vẽ theo hồ sơ thuộc tính chuẩn đã lưu của nhân vật trong tab Hồ Sơ Nhân Vật.",
        _ => string.Empty
    };

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyPromptCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearPromptCommand))]
    private string _rawPromptInput = string.Empty;

    [ObservableProperty]
    private bool _isPromptPreviewOpen;

    [ObservableProperty]
    private string _structuredPromptPreview = string.Empty;

    [ObservableProperty]
    private string _hybridPromptPreview = string.Empty;

    [ObservableProperty]
    private string _promptWarnings = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyPromptCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearPromptCommand))]
    private string _prompt = string.Empty;

    partial void OnRawPromptInputChanged(string value)
    {
        Prompt = value;
    }

    [ObservableProperty]
    private int _imageWidth = 1024;

    [ObservableProperty]
    private int _imageHeight = 1024;

    [ObservableProperty]
    private string _selectedAspectRatio = "Square";

    partial void OnSelectedAspectRatioChanged(string value)
    {
        if (value == "Portrait") { ImageWidth = 512; ImageHeight = 768; }
        else if (value == "Square") { ImageWidth = 512; ImageHeight = 512; }
        else if (value == "Landscape") { ImageWidth = 768; ImageHeight = 512; }
        StatusMessage = $"Đã đặt kích thước {value} ({ImageWidth}x{ImageHeight}px)";
    }

    [ObservableProperty]
    private int _requestedImageCount = 1;

    public ObservableCollection<GenerationJobDto> GenerationQueue { get; } = [];

    [ObservableProperty]
    private GenerationJobDto? _selectedQueueJob;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LightboxDisplayImage))]
    [NotifyCanExecuteChangedFor(nameof(SaveCanvasImageCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyCanvasImageCommand))]
    private BitmapImage? _generatedImage;

    [ObservableProperty]
    private string _lastJobInfo = string.Empty;

    // Server-issued status only: no invented percentage, inference steps, or ETA.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGenerate))]
    [NotifyPropertyChangedFor(nameof(IsNotGenerating))]
    private bool _isGenerating;

    public bool CanGenerate => IsLoggedIn && (IsLocalStudioMode || (CanCreateInProject && SelectedCharacter != null)) && !IsGenerating && !IsBusy && (!IsImg2ImgMode || IsImg2ImgAvailable);
    public bool IsNotGenerating => !IsGenerating;

    [ObservableProperty]
    private string _generationStatusText = "Sẵn sàng sinh ảnh";

    [ObservableProperty]
    private string _generationPhaseText = "Sẵn sàng";

    [ObservableProperty]
    private int _elapsedSeconds;

    // Navigation (0=Studio, 1=Characters, 2=Concepts, 3=Scripts, 4=Team, 5=Settings)
    [ObservableProperty]
    private int _currentTabIndex = 6;

    [RelayCommand]
    private void SetTab(string tabIndexStr)
    {
        if (!int.TryParse(tabIndexStr, out var index)) return;
        if (index == 0 && ShowFavoritesOnly) ShowFavoritesOnly = false;
        NavigateTo(index);
    }

    [RelayCommand]
    private void AppendPromptTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(RawPromptInput))
        {
            RawPromptInput = tag;
        }
        else if (!RawPromptInput.Contains(tag, StringComparison.OrdinalIgnoreCase))
        {
            RawPromptInput = RawPromptInput.Trim().TrimEnd(',') + ", " + tag;
        }
        Prompt = RawPromptInput;
        StatusMessage = $"Đã thêm thẻ: {tag}";
    }

    [RelayCommand]
    private void SetQuickAspectRatio(string ratio)
    {
        SelectedAspectRatio = ratio;
    }

    // Credentials are entered explicitly; there is no seeded desktop login.

    [RelayCommand]
    private void CopyConceptImage()
    {
        if (ConceptVersionImage != null)
        {
            try
            {
                Clipboard.SetImage(ConceptVersionImage);
                StatusMessage = "Đã sao chép ảnh từ Thư Viện vào Clipboard!";
            }
            catch (Exception ex) { ReportError(ex); }
        }
        else
        {
            StatusMessage = "Không có ảnh nào trong Thư Viện để sao chép.";
        }
    }

    [RelayCommand]
    private void SaveConceptImage()
    {
        if (ConceptVersionImage == null)
        {
            StatusMessage = "Chưa có ảnh nào trong Thư Viện để lưu.";
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Lưu Tác Phẩm Từ Thư Viện Ra Máy Tính",
            Filter = "PNG Image (*.png)|*.png|All Files (*.*)|*.*",
            FileName = $"Gallery_{SelectedCharacter?.Name ?? "Concept"}_{SelectedVersion?.VersionNumber ?? 1}_{DateTime.Now:yyyyMMdd_HHmmss}.png"
        };

        if (dlg.ShowDialog() == true)
        {
            try
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(ConceptVersionImage));
                using var stream = File.Create(dlg.FileName);
                encoder.Save(stream);
                StatusMessage = $"Đã lưu ảnh Thư Viện thành công: {Path.GetFileName(dlg.FileName)}";
            }
            catch (Exception ex)
            {
                ReportError(ex);
            }
        }
    }

    public MainViewModel(ApiClient api, AuthSessionService session)
    {
        _api = api;
        _session = session;
        ServerUrl = api.BaseUrl;
        InitializeWorkspace();
    }

    [RelayCommand(CanExecute = nameof(CanLogin))]
    private async Task LoginAsync()
    {
        if (!TryValidateServerUrl() || string.IsNullOrWhiteSpace(Email) || string.IsNullOrWhiteSpace(Password))
        {
            StatusMessage = "Nhập địa chỉ máy chủ hợp lệ, email và mật khẩu để đăng nhập.";
            return;
        }
        IsBusy = true;
        StatusMessage = "Đang xác thực tài khoản…";
        try
        {
            _api.BaseUrl = ServerUrl.TrimEnd('/');
            var success = await _session.LoginAsync(Email.Trim(), Password);
            Password = string.Empty;
            if (!success || _session.CurrentUser is not { IsActive: true } user)
            {
                _session.Logout();
                StatusMessage = ApiError("Không đăng nhập được. Kiểm tra email, mật khẩu và kết nối máy chủ.");
                return;
            }
            IsLoggedIn = true;
            IsAdmin = _session.IsAdmin;
            UserRoles = string.Join(", ", user.GlobalRoles);
            CurrentUserName = user.DisplayName;
            ApiConnectionStatus = "Đã xác thực";
            CurrentTabIndex = 0;
            await LoadProjectsAsync();
            if (SelectedProject == null)
                SelectedProject = Projects.FirstOrDefault(p => !string.Equals(p.Status, "Archived", StringComparison.OrdinalIgnoreCase)) ?? Projects.FirstOrDefault();
            await LoadGenerationCapabilitiesAsync();
            LoadLocalChatHistory();
        }
        catch (Exception ex) { ReportError(ex); }
        finally { Password = string.Empty; IsBusy = false; }
    }

    [RelayCommand]
    private void Logout()
    {
        if (_session.IsAuthenticated && !ConfirmDiscardChanges()) return;
        HasUnsavedChanges = HasUnsavedScriptChanges = HasUnsavedSceneChanges = false;
        _generationMonitorCts?.Cancel();
        _queryCts?.Cancel();
        _session.Logout();
        IsLoggedIn = false;
        IsBusy = false;
        IsAdmin = false;
        Password = string.Empty;
        UserRoles = string.Empty;
        CurrentUserName = string.Empty;
        SelectedProject = null;
        Projects.Clear();
        RecentProjects.Clear();
        Accounts.Clear();
        ProjectTotalCount = 0;
        IsCreateProjectDialogOpen = false;
        IsCreateCharacterDialogOpen = false;
        GeneratedImage = ConceptVersionImage = CompareLeftImage = CompareRightImage = ReferencePreviewImage = null;
        ActiveChatThread = null;
        _activeGeneratingTurn = null;
        _lightboxTurn = null;
        _chatHistoryLoaded = false;
        _favoritesLoaded = false;
        _favoriteRecords.Clear();
        _standaloneFavoriteItems.Clear();
        FavoriteImages.Clear();
        ChatThreads.Clear();
        NotifyChatStateChanged();
        ClearSessionGenerations();
        ClearConceptThumbnails();
        ClearReference();
        IsGenerating = false;
        IsImg2ImgAvailable = false;
        _backHistory.Clear(); _forwardHistory.Clear();
        CurrentTabIndex = 6;
        ApiConnectionStatus = "Chưa kết nối";
        StatusMessage = "Đã đăng xuất. Dữ liệu phiên làm việc đã được xóa khỏi giao diện.";
    }

    [RelayCommand]
    private async Task RefreshTokenAsync()
    {
        IsBusy = true;
        StatusMessage = "Đang làm mới phiên xác thực...";
        try
        {
            var success = await _session.RefreshAsync();
            if (success)
            {
                StatusMessage = "Phiên làm việc đã được gia hạn thành công.";
            }
            else
            {
                Logout();
                StatusMessage = "Phiên làm việc hết hạn. Vui lòng đăng nhập lại.";
            }
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task LoadProjectsAsync()
    {
        if (!IsLoggedIn) return;
        var revision = ++_projectQueryRevision;
        var selectedId = SelectedProject?.Id;
        IsBusy = true;
        try
        {
            var status = ProjectStatusFilter == "Đã lưu trữ" ? "Archived" : ProjectStatusFilter == "Đang hoạt động" ? "Active" : null;
            var result = await _api.GetProjectsPagedAsync(ProjectPage, 24, ProjectSearchText.Trim(), status, sort: ProjectSort == "Tên A–Z" ? "name" : "updated");
            if (!IsLoggedIn || revision != _projectQueryRevision) return;
            if (result == null) { StatusMessage = ApiError("Không tải được danh sách dự án."); return; }
            Projects.Clear();
            foreach (var project in result.Items) Projects.Add(project);
            ProjectTotalCount = result.TotalCount;
            if (selectedId.HasValue && Projects.FirstOrDefault(p => p.Id == selectedId) is { } selected) SelectedProject = selected;
            ApiConnectionStatus = "Đã kết nối";
            StatusMessage = result.TotalCount == 0 ? "Chưa có dự án phù hợp. Tạo dự án hoặc thay đổi bộ lọc." : $"Đã tải {result.Items.Count} / {result.TotalCount} dự án.";
            RefreshLibraryViews();
        }
        catch (Exception ex) { if (revision == _projectQueryRevision) ReportError(ex); }
        finally { if (revision == _projectQueryRevision) IsBusy = false; }
    }

    partial void OnSelectedProjectChanged(ProjectDto? oldValue, ProjectDto? newValue)
    {
        var value = newValue;
        if (_restoringSelection) return;
        if (!ConfirmDiscardChanges()) { _restoringSelection = true; try { SelectedProject = oldValue; } finally { _restoringSelection = false; } return; }
        _scopeVersion++;
        IsCreateCharacterDialogOpen = false;
        CharacterPage = ConceptPage = 1; CharacterTotalCount = ConceptTotalCount = 0;
        _characterQueryCts?.Cancel();
        _generationMonitorCts?.Cancel();
        IsGenerating = false;
        SelectedCharacter = null; SelectedScript = null; SelectedScene = null; SelectedQueueJob = null;
        Characters.Clear(); ProjectMembers.Clear(); Scripts.Clear(); Scenes.Clear(); GenerationQueue.Clear();
        AttributeDefinitions.Clear(); Templates.Clear(); Relationships.Clear();
        GeneratedImage = null; LastJobInfo = string.Empty;
        ClearSessionGenerations();
        ClearReference();
        IsProjectOwner = IsLocalStudioMode; CanCreateInProject = IsLocalStudioMode;
        SelectedProjectMember = null;
        IsProjectArchived = !IsLocalStudioMode && value?.Status == "Archived";
        ProjectRole = "Đang tải quyền…";
        if (value != null)
        {
            Observe(LoadCharactersAsync(value.Id));
            Observe(LoadMembersAsync(value.Id));
            Observe(LoadScriptsAsync(value.Id));
            Observe(LoadQueueAsync(value.Id));
            Observe(LoadAdvancedProjectDataAsync(value.Id));
        }
        else ProjectRole = "Chưa chọn dự án";
        NotifyWorkspaceState();
    }

    partial void OnSelectedCharacterChanged(CharacterDto? oldValue, CharacterDto? newValue)
    {
        var value = newValue;
        if (_restoringSelection) return;
        if (!ConfirmDiscardChanges()) { _restoringSelection = true; try { SelectedCharacter = oldValue; } finally { _restoringSelection = false; } return; }
        _lastPreviewKey = null;
        GeneratedImage = null;
        IsPromptPreviewOpen = false;
        StructuredPromptPreview = HybridPromptPreview = PromptWarnings = string.Empty;
        SelectedCharacterAttributes.Clear();
        SelectedCharacterVariants.Clear();
        SelectedCharacterRevisions.Clear();
        CharacterConcepts.Clear();
        ConceptVersions.Clear();
        SelectedVariant = null;
        SelectedRevision = null;
        SelectedConcept = null;
        SelectedVersion = null;
        RevisionDiffSummary = string.Empty;
        ConceptVersionImage = CompareLeftImage = CompareRightImage = null;
        ClearReference();
        ResetCharacterEditor();
        NotifyWorkspaceState();

        if (value != null && SelectedProject != null)
        {
            foreach (var attr in value.Attributes) SelectedCharacterAttributes.Add(attr);
            foreach (var v in value.Variants) SelectedCharacterVariants.Add(v);
            SelectedVariant = value.Variants.FirstOrDefault(v => v.IsDefault) ?? value.Variants.FirstOrDefault();
            Observe(LoadRevisionsAsync(SelectedProject.Id, value.Id));
            Observe(LoadCharacterConceptsAsync(SelectedProject.Id, value.Id));
        }
    }

    partial void OnSelectedConceptChanged(CharacterConceptDto? value)
    {
        NotifyWorkspaceState();
        ConceptVersions.Clear();
        SelectedVersion = null;
        ConceptVersionImage = null;
        if (value != null && SelectedProject != null)
        {
            Observe(LoadConceptDetailAsync(SelectedProject.Id, value.Id));
        }
    }

    partial void OnSelectedVersionChanged(ConceptVersionDto? value)
    {
        NotifyWorkspaceState();
        if (value != null && SelectedProject != null)
        {
            Observe(LoadVersionImageAsync(SelectedProject.Id, value.ImageAssetId));
        }
        else
        {
            ConceptVersionImage = null;
        }
    }

    private async Task LoadRevisionsAsync(Guid projectId, Guid characterId)
    {
        var revisions = await _api.GetCharacterRevisionsAsync(projectId, characterId);
        if (SelectedProject?.Id != projectId || SelectedCharacter?.Id != characterId) return;
        SelectedCharacterRevisions.Clear();
        foreach (var revision in revisions) SelectedCharacterRevisions.Add(revision);
        SelectedRevision = revisions.LastOrDefault();
        RevisionFrom = revisions.FirstOrDefault(); RevisionTo = revisions.LastOrDefault();
    }

    [RelayCommand]
    private async Task CompareRevisionsAsync()
    {
        var originalScope = _scopeVersion;
        var originalCharacterId = SelectedCharacter?.Id;
        if (SelectedProject == null || SelectedCharacter == null || SelectedCharacterRevisions.Count < 2)
        {
            RevisionDiffSummary = "Cần ít nhất 2 phiên bản để so sánh khác biệt (diff).";
            return;
        }
        var r1 = RevisionFrom ?? SelectedCharacterRevisions.OrderBy(r => r.RevisionNumber).First();
        var rLast = RevisionTo ?? SelectedCharacterRevisions.OrderBy(r => r.RevisionNumber).Last();
        var diff = await _api.GetRevisionDiffAsync(SelectedProject.Id, SelectedCharacter.Id, r1.Id, rLast.Id);
            if (originalScope != _scopeVersion || !IsLoggedIn || SelectedCharacter?.Id != originalCharacterId) return;
        if (diff != null)
        {
            RevisionDiffSummary = $"Diff Rev #{diff.FromRevisionNumber} -> Rev #{diff.ToRevisionNumber} ({diff.Changes.Count} thay đổi):\n" +
                string.Join("\n", diff.Changes.Select(c => $"• [{c.ChangeType}] {c.FieldPath}: '{c.OldValue}' => '{c.NewValue}'"));
        }
    }

    partial void OnSelectedScriptChanged(ScriptDto? oldValue, ScriptDto? newValue)
    {
        var value = newValue;
        if (_restoringSelection) return;
        if (!ConfirmDiscardChanges()) { _restoringSelection = true; try { SelectedScript = oldValue; } finally { _restoringSelection = false; } return; }
        _resettingEditor = true;
        SelectedScene = null;
        Scenes.Clear();
        ScriptEditorTitle = value?.Title ?? string.Empty;
        ScriptEditorSummary = value?.Summary ?? string.Empty;
        HasUnsavedScriptChanges = false;
        _resettingEditor = false;
        if (SelectedProject != null && value != null)
        {
            Observe(LoadScenesAsync(SelectedProject.Id, value.Id));
        }
        else
        {
            Scenes.Clear();
            SelectedScene = null;
        }
    }

    [RelayCommand]
    private async Task LoadMembersAsync(Guid projectId)
    {
        var scope = _scopeVersion;
        try
        {
            var members = await _api.GetProjectMembersAsync(projectId);
            if (scope != _scopeVersion || SelectedProject?.Id != projectId || !IsLoggedIn) return;
            if (members == null)
            {
                ProjectRole = "Không tải được quyền. Hãy mở lại dự án để thử lại.";
                return;
            }
            ProjectMembers.Clear();
            foreach (var member in members) ProjectMembers.Add(member);
            var membership = members.FirstOrDefault(m => m.UserId == _session.CurrentUser?.Id);
            IsProjectOwner = IsLocalStudioMode || membership?.Role == "Owner";
            CanCreateInProject = IsLocalStudioMode || (membership?.Role is "Owner" or "Designer" && !IsProjectArchived);
            ProjectRole = membership?.Role ?? "Không có quyền truy cập";
            NotifyWorkspaceState();
        }
        catch (Exception ex)
        {
            if (scope != _scopeVersion || !IsLoggedIn) return;
            ProjectRole = "Không tải được quyền. Hãy mở lại dự án để thử lại.";
            ReportError(ex);
        }
    }

    [RelayCommand(CanExecute = nameof(CanManageProject))]
    private async Task AddMemberAsync()
    {
        var originalScope = _scopeVersion;
        if (SelectedProject == null || string.IsNullOrWhiteSpace(NewMemberEmail)) return;
        IsBusy = true;
        StatusMessage = $"Đang thêm thành viên {NewMemberEmail}...";
        try
        {
            var added = await _api.AddProjectMemberAsync(SelectedProject.Id, new AddProjectMemberRequest(NewMemberEmail.Trim(), NewMemberRole));
            if (originalScope != _scopeVersion || !IsLoggedIn) return;
            if (added != null)
            {
                ProjectMembers.Add(added);
                NewMemberEmail = string.Empty;
                StatusMessage = $"Đã thêm thành viên {added.DisplayName} ({added.Role}) vào dự án!";
            }
            else
            {
                StatusMessage = "Không thể thêm thành viên. Vui lòng kiểm tra email hoặc quyền hạn.";
            }
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanManageProject))]
    private async Task RemoveMemberAsync(ProjectMemberDto? member)
    {
        var originalScope = _scopeVersion;
        if (SelectedProject == null || member == null) return;
        if (MessageBox.Show($"Xóa {member.DisplayName} khỏi dự án?", "Xóa thành viên", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        IsBusy = true;
        StatusMessage = $"Đang xóa thành viên {member.DisplayName}...";
        try
        {
            var ok = await _api.RemoveProjectMemberAsync(SelectedProject.Id, member.UserId);
            if (originalScope != _scopeVersion || !IsLoggedIn) return;
            if (ok)
            {
                ProjectMembers.Remove(member);
                StatusMessage = $"Đã xóa thành viên {member.DisplayName} khỏi dự án.";
            }
            else
            {
                StatusMessage = "Không thể xóa thành viên (Bảo vệ Last Owner Invariant hoặc lỗi quyền hạn).";
            }
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanArchiveSelectedProject))]
    private async Task ArchiveProjectAsync()
    {
        var originalScope = _scopeVersion;
        if (SelectedProject == null) return;
        if (MessageBox.Show($"Lưu trữ dự án {SelectedProject.Name}? Các thao tác chỉnh sửa sẽ bị hạn chế.", "Lưu trữ dự án", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        IsBusy = true;
        StatusMessage = $"Đang lưu trữ dự án {SelectedProject.Name}...";
        try
        {
            var ok = await _api.ArchiveProjectAsync(SelectedProject.Id);
            if (originalScope != _scopeVersion || !IsLoggedIn) return;
            if (ok)
            {
                StatusMessage = $"Dự án '{SelectedProject.Name}' đã được chuyển sang trạng thái Archived.";
                await LoadProjectsAsync();
            }
            else
            {
                StatusMessage = "Không thể lưu trữ dự án.";
            }
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRestoreSelectedProject))]
    private async Task RestoreProjectAsync()
    {
        var originalScope = _scopeVersion;
        if (SelectedProject == null) return;
        IsBusy = true;
        StatusMessage = $"Đang kích hoạt lại dự án {SelectedProject.Name}...";
        try
        {
            var ok = await _api.RestoreProjectAsync(SelectedProject.Id);
            if (originalScope != _scopeVersion || !IsLoggedIn) return;
            if (ok)
            {
                StatusMessage = $"Dự án '{SelectedProject.Name}' đã được kích hoạt lại thành công!";
                await LoadProjectsAsync();
            }
            else
            {
                StatusMessage = "Không thể kích hoạt lại dự án.";
            }
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenCreateProjectDialog()
    {
        if (!IsLoggedIn || !ConfirmDiscardChanges()) return;
        IsEditingProject = false;
        QuickProjectName = string.Empty;
        QuickProjectType = "Game";
        QuickProjectDescription = string.Empty;
        _projectEditorInitialSignature = ProjectEditorSignature;
        IsCreateProjectDialogOpen = true;
    }

    [RelayCommand]
    private void CloseCreateProjectDialog()
    {
        if (IsBusy) return;
        if (ProjectEditorSignature != _projectEditorInitialSignature && MessageBox.Show("Bỏ thông tin dự án chưa lưu?", "Thay đổi chưa lưu", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        IsCreateProjectDialogOpen = false;
        EditorError = string.Empty;
    }

    [RelayCommand(CanExecute = nameof(CanSaveProjectDialog))]
    private async Task SubmitQuickCreateProjectAsync()
    {
        if (string.IsNullOrWhiteSpace(QuickProjectName))
        {

            EditorError = "Tên dự án không được để trống.";
            return;
        }

        IsBusy = true;
        var name = QuickProjectName.Trim();
        StatusMessage = $"Đang tạo dự án '{name}'...";
        try
        {
            if (IsEditingProject && !IsProjectOwner) { EditorError = "Chỉ chủ dự án có thể chỉnh sửa dự án."; return; }
            var p = IsEditingProject && SelectedProject is { } editing
                ? await _api.UpdateProjectAsync(editing.Id, new UpdateProjectRequest(name, QuickProjectType, QuickProjectDescription, editing.RowVersion ?? string.Empty))
                : await _api.CreateProjectAsync(name, QuickProjectType, QuickProjectDescription);
            if (p != null)
            {
                var previous = Projects.FirstOrDefault(item => item.Id == p.Id);
                if (previous != null) Projects.Remove(previous);
                else ProjectTotalCount++;
                Projects.Insert(0, p);
                SelectedProject = p;
                IsCreateProjectDialogOpen = false;
                StatusMessage = $"Đã lưu dự án {p.Name}.";

            }
            else
            {
                var msg = string.IsNullOrWhiteSpace(_api.LastErrorMessage) ? "Máy chủ không thể tạo dự án. Vui lòng kiểm tra quyền hạn hoặc kết nối Server." : _api.LastErrorMessage;
                StatusMessage = $"Không thể tạo dự án: {msg}";

            }
        }
        catch (Exception ex)
        {
            ReportError(ex);

        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CreateProjectAsync()
    {
        if (string.IsNullOrWhiteSpace(NewProjectName))
        {

            return;
        }

        IsBusy = true;
        var name = NewProjectName.Trim();
        var type = string.IsNullOrWhiteSpace(NewProjectType) ? "Game" : NewProjectType.Trim();
        StatusMessage = $"Đang tạo dự án '{name}'...";
        try
        {
            var project = await _api.CreateProjectAsync(name, type, "Dự án tạo từ Gula Studio");
            if (project != null)
            {
                Projects.Insert(0, project);
                SelectedProject = project;
                NewProjectName = string.Empty;
                StatusMessage = $"Đã tạo dự án '{project.Name}' thành công!";

            }
            else
            {
                var msg = string.IsNullOrWhiteSpace(_api.LastErrorMessage) ? "Máy chủ không thể tạo dự án. Vui lòng kiểm tra quyền hạn hoặc kết nối Server." : _api.LastErrorMessage;
                StatusMessage = $"Không thể tạo dự án: {msg}";

            }
        }
        catch (Exception ex)
        {
            ReportError(ex);

        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadCharactersAsync(Guid projectId, CharacterDto? createdCharacter = null)
    {
        var revision = ++_characterQueryRevision;
        var currentSelectedId = SelectedCharacter?.Id;
        var previousId = createdCharacter?.Id ?? currentSelectedId;
        var result = await _api.GetCharactersPagedAsync(projectId, CharacterPage, 24, CharacterSearchText.Trim());
        if (SelectedProject?.Id != projectId || !IsLoggedIn || revision != _characterQueryRevision) return;
        if (result == null) { EditorError = StatusMessage = ApiError("Không tải được danh sách nhân vật."); return; }

        var nextItems = new List<CharacterDto>(result.Items);
        if (createdCharacter != null && !nextItems.Any(item => item.Id == createdCharacter.Id))
            nextItems.Insert(0, createdCharacter);
        var nextSelected = nextItems.FirstOrDefault(c => c.Id == previousId) ?? nextItems.FirstOrDefault();
        var preserveSameSelection = currentSelectedId.HasValue && nextSelected != null && nextSelected.Id == currentSelectedId.Value;

        if (preserveSameSelection) _restoringSelection = true;
        try
        {
            Characters.Clear();
            foreach (var character in nextItems) Characters.Add(character);
            CharacterTotalCount = result.TotalCount;
            SelectedCharacter = nextSelected;
        }
        finally
        {
            if (preserveSameSelection) _restoringSelection = false;
        }
        RefreshLibraryViews();
    }

    [RelayCommand(CanExecute = nameof(CanEditProject))]
    private void OpenCreateCharacterDialog()
    {
        if (!CanEditProject || !ConfirmDiscardChanges()) return;
        _characterQueryCts?.Cancel();
        NewCharacterName = NewCharacterDescription = CharacterCreateError = string.Empty;
        NewCharacterRole = "Protagonist";
        IsCreateCharacterDialogOpen = true;
    }

    [RelayCommand]
    private void CloseCreateCharacterDialog()
    {
        if (!IsBusy) IsCreateCharacterDialogOpen = false;
    }

    [RelayCommand(CanExecute = nameof(CanSubmitCharacter))]
    private async Task CreateCharacterAsync()
    {
        if (!CanSubmitCharacter || SelectedProject is not { } project) return;
        var scope = _scopeVersion;
        var name = NewCharacterName.Trim();
        var role = NewCharacterRole;
        var description = string.IsNullOrWhiteSpace(NewCharacterDescription) ? null : NewCharacterDescription.Trim();
        CharacterCreateError = string.Empty;
        IsBusy = true;
        try
        {
            var character = await _api.CreateCharacterAsync(project.Id, name, role, description);
            if (scope != _scopeVersion || !IsLoggedIn) return;
            if (character == null)
            {
                CharacterCreateError = ApiError("Không tạo được nhân vật. Vui lòng thử lại.");
                return;
            }

            // Invalidate an outstanding filtered query before selecting the new item.
            if (CharacterSearchText.Length > 0) CharacterSearchText = string.Empty;
            _characterQueryCts?.Cancel();
            ++_characterQueryRevision;
            CharacterPage = 1;
            IsCreateCharacterDialogOpen = false;
            Characters.Insert(0, character);
            CharacterTotalCount++;
            SelectedCharacter = character;
            if (CurrentTabIndex == 0 && string.IsNullOrWhiteSpace(RawPromptInput))
                RawPromptInput = character.Description ?? string.Empty;
            StatusMessage = $"Đã tạo nhân vật {character.Name}.";
            await LoadCharactersAsync(project.Id, character);
        }
        catch (Exception ex)
        {
            if (scope != _scopeVersion || !IsLoggedIn) return;
            ReportError(ex);
            if (IsCreateCharacterDialogOpen) CharacterCreateError = EditorError;
        }
        finally { IsBusy = false; }
    }

    private async Task LoadScriptsAsync(Guid projectId)
    {
        var list = await _api.GetScriptsAsync(projectId);
        if (SelectedProject?.Id != projectId || !IsLoggedIn) return;
        Scripts.Clear(); foreach (var script in list) Scripts.Add(script);
        SelectedScript = list.FirstOrDefault(s => s.Id == SelectedScript?.Id);
    }

    [RelayCommand(CanExecute = nameof(CanEditProject))]
    private async Task CreateScriptAsync()
    {
        if (!ConfirmDiscardChanges()) return;
        var originalScope = _scopeVersion;
        if (SelectedProject == null) { StatusMessage = "Chọn một dự án trước khi tạo kịch bản."; return; }

        if (string.IsNullOrWhiteSpace(NewScriptTitle))
        {
            NewScriptTitle = $"Hồi {Scripts.Count + 1}: Kịch Bản Mới";
        }

        IsBusy = true;
        var title = NewScriptTitle.Trim();
        StatusMessage = $"Đang tạo kịch bản '{title}'...";
        try
        {
            var s = await _api.CreateScriptAsync(SelectedProject.Id, new CreateScriptRequest(title, "Kịch bản tạo từ Gula Studio"));
            if (originalScope != _scopeVersion || !IsLoggedIn) return;
            if (s != null)
            {
                Scripts.Insert(0, s);
                SelectedScript = s;
                NewScriptTitle = string.Empty;
                StatusMessage = $"Đã tạo kịch bản '{s.Title}'!";

            }
            else
            {
                StatusMessage = "Không thể tạo kịch bản. Dự án có thể đã bị lưu trữ.";

            }
        }
        catch (Exception ex)
        {
            ReportError(ex);

        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditProject))]
    private async Task ArchiveScriptAsync(ScriptDto? script)
    {
        var originalScope = _scopeVersion;
        if (SelectedProject == null || script == null) return;
        IsBusy = true;
        StatusMessage = $"Đang lưu trữ kịch bản '{script.Title}'...";
        try
        {
            var ok = await _api.ArchiveScriptAsync(SelectedProject.Id, script.Id);
            if (originalScope != _scopeVersion || !IsLoggedIn) return;
            if (ok)
            {
                StatusMessage = $"Đã lưu trữ kịch bản '{script.Title}'.";
                await LoadScriptsAsync(SelectedProject.Id);
            }
            else
            {
                StatusMessage = "Không thể lưu trữ kịch bản.";
            }
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadScenesAsync(Guid projectId, Guid scriptId)
    {
        var list = await _api.GetScenesAsync(projectId, scriptId);
        if (SelectedProject?.Id != projectId || SelectedScript?.Id != scriptId) return;
        var selectedId = SelectedScene?.Id;
        Scenes.Clear();
        foreach (var scene in list) Scenes.Add(scene);
        SelectedScene = list.FirstOrDefault(scene => scene.Id == selectedId) ?? list.FirstOrDefault();
    }

    [RelayCommand(CanExecute = nameof(CanEditProject))]
    private async Task CreateSceneAsync()
    {
        if (!ConfirmDiscardChanges()) return;
        var originalScope = _scopeVersion;
        var originalScriptId = SelectedScript?.Id;
        if (SelectedProject == null || SelectedScript == null)
        { StatusMessage = "Chọn dự án và kịch bản trước khi thêm phân cảnh."; return; }

        var sceneTitle = string.IsNullOrWhiteSpace(NewSceneTitle) ? $"Phân Cảnh {Scenes.Count + 1}" : NewSceneTitle.Trim();
        var sceneSetting = string.IsNullOrWhiteSpace(NewSceneSetting) ? "Khung cảnh tự nhiên" : NewSceneSetting.Trim();
        var sceneContent = string.IsNullOrWhiteSpace(NewSceneContent) ? "Mô tả phân cảnh nhân vật xuất hiện..." : NewSceneContent.Trim();

        IsBusy = true;
        StatusMessage = $"Đang thêm phân cảnh '{sceneTitle}' vào '{SelectedScript.Title}'...";
        try
        {
            var nextOrder = Scenes.Count > 0 ? Scenes.Max(x => x.OrderIndex) + 1 : 1;
            var characterIds = SelectedCharacter != null ? new List<Guid> { SelectedCharacter.Id } : new List<Guid>();
            var sc = await _api.CreateSceneAsync(
                SelectedProject.Id,
                SelectedScript.Id,
                new CreateSceneRequest(sceneTitle, sceneSetting, sceneContent, nextOrder, characterIds)
            );
            if (originalScope != _scopeVersion || !IsLoggedIn || SelectedScript?.Id != originalScriptId) return;

            if (sc != null)
            {
                Scenes.Add(sc);
                SelectedScene = sc;
                NewSceneTitle = string.Empty;
                NewSceneSetting = string.Empty;
                NewSceneContent = string.Empty;
                StatusMessage = $"Đã tạo phân cảnh #{sc.OrderIndex}: '{sc.Title}'!";

            }
            else
            {
                StatusMessage = "Không thể tạo phân cảnh. Kịch bản có thể đã bị lưu trữ.";

            }
        }
        catch (Exception ex)
        {
            ReportError(ex);

        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditProject))]
    private async Task MoveSceneUpAsync(SceneDto? scene)
    {
        if (!ConfirmDiscardChanges()) return;
        var originalScope = _scopeVersion;
        var originalScriptId = SelectedScript?.Id;
        if (SelectedProject == null || SelectedScript == null || scene == null) return;
        var idx = Scenes.IndexOf(scene);
        if (idx <= 0) return;

        var prev = Scenes[idx - 1];
        var items = new List<SceneOrderItem>
        {
            new SceneOrderItem(scene.Id, prev.OrderIndex),
            new SceneOrderItem(prev.Id, scene.OrderIndex)
        };

        IsBusy = true;
        try
        {
            var ok = await _api.ReorderScenesAsync(SelectedProject.Id, SelectedScript.Id, new ReorderScenesRequest(items));
            if (originalScope != _scopeVersion || !IsLoggedIn || SelectedScript?.Id != originalScriptId) return;
            if (ok)
            {
                if (SelectedProject is not { } currentProject || SelectedScript is not { } currentScript) return;
                await LoadScenesAsync(currentProject.Id, currentScript.Id);
                StatusMessage = $"Đã thay đổi thứ tự phân cảnh '{scene.Title}' thành công.";
            }
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditProject))]
    private async Task MoveSceneDownAsync(SceneDto? scene)
    {
        if (!ConfirmDiscardChanges()) return;
        var originalScope = _scopeVersion;
        var originalScriptId = SelectedScript?.Id;
        if (SelectedProject == null || SelectedScript == null || scene == null) return;
        var idx = Scenes.IndexOf(scene);
        if (idx < 0 || idx >= Scenes.Count - 1) return;

        var next = Scenes[idx + 1];
        var items = new List<SceneOrderItem>
        {
            new SceneOrderItem(scene.Id, next.OrderIndex),
            new SceneOrderItem(next.Id, scene.OrderIndex)
        };

        IsBusy = true;
        try
        {
            var ok = await _api.ReorderScenesAsync(SelectedProject.Id, SelectedScript.Id, new ReorderScenesRequest(items));
            if (originalScope != _scopeVersion || !IsLoggedIn || SelectedScript?.Id != originalScriptId) return;
            if (ok)
            {
                if (SelectedProject is not { } currentProject || SelectedScript is not { } currentScript) return;
                await LoadScenesAsync(currentProject.Id, currentScript.Id);
                StatusMessage = $"Đã thay đổi thứ tự phân cảnh '{scene.Title}' thành công.";
            }
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanPreviewPrompt => IsLoggedIn && !IsBusy && SelectedProject != null && SelectedCharacter != null;

    [RelayCommand(CanExecute = nameof(CanPreviewPrompt))]
    private async Task PreviewPromptAsync()
    {
        var originalScope = _scopeVersion;
        var originalCharacterId = SelectedCharacter?.Id;
        if (SelectedProject == null || SelectedCharacter == null)
        {
            StatusMessage = "Vui lòng chọn Dự án và Nhân vật để xem trước prompt.";
            return;
        }

        var requestKey = CurrentPromptKey();
        var requestMode = SelectedPromptMode;
        IsBusy = true;
        StatusMessage = "Đang tổng hợp prompt theo 3 chế độ (Raw / Structured / Hybrid)...";
        try
        {
            var req = new PromptPreviewRequest(
                VariantId: SelectedVariant?.Id,
                RawPrompt: RawPromptInput
            );

            var preview = await _api.PreviewPromptAsync(SelectedProject.Id, SelectedCharacter.Id, req);
            if (originalScope != _scopeVersion || !IsLoggedIn || SelectedCharacter?.Id != originalCharacterId) return;
            if (requestKey != CurrentPromptKey()) return;
            if (preview != null)
            {
                StructuredPromptPreview = preview.StructuredPrompt;
                HybridPromptPreview = preview.HybridPrompt;
                PromptWarnings = preview.Warnings.Count > 0
                    ? string.Join("\n", preview.Warnings)
                    : "Không phát hiện mâu thuẫn.";

                Prompt = requestMode switch
                {
                    PromptMode.Raw => preview.RawPrompt,
                    PromptMode.Structured => preview.StructuredPrompt,
                    PromptMode.Hybrid => preview.HybridPrompt,
                    _ => preview.HybridPrompt
                };

                _lastPreviewKey = requestKey;
                IsPromptPreviewOpen = true;
                StatusMessage = $"Đã tạo bản xem trước prompt (Chế độ: {SelectedPromptMode})!";
            }
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task LoadQueueAsync(Guid projectId)
    {
        var paged = await _api.GetGenerationsPagedAsync(projectId, null, 1, 50);
        if (SelectedProject?.Id != projectId || !IsLoggedIn) return;
        GenerationQueue.Clear();
        if (paged != null)
        {
            foreach (var j in paged.Items) GenerationQueue.Add(j);
        }
    }

    [RelayCommand]
    private async Task CancelJobAsync(GenerationJobDto? job)
    {
        if (SelectedProject == null || job == null) return;
        IsBusy = true;
        StatusMessage = $"Đang gửi yêu cầu hủy tác vụ {job.Id:D}...";
        try
        {
            var ok = await _api.CancelGenerationAsync(SelectedProject.Id, job.Id);
            if (ok)
            {
                StatusMessage = "Đã hủy tác vụ thành công.";
                await LoadQueueAsync(SelectedProject.Id);
            }
            else
            {
                StatusMessage = "Không thể hủy tác vụ (có thể đã hoàn thành hoặc bạn không đủ quyền).";
            }
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditProject))]
    private async Task RetryJobAsync(GenerationJobDto? job)
    {
        if (SelectedProject == null || job == null) return;
        IsBusy = true;
        StatusMessage = $"Đang gửi yêu cầu thử lại tác vụ {job.Id:D}...";
        try
        {
            var ok = await _api.RetryGenerationAsync(SelectedProject.Id, job.Id);
            if (ok)
            {
                StatusMessage = "Tác vụ đã được đưa lại vào hàng đợi.";
                await LoadQueueAsync(SelectedProject.Id);
            }
            else
            {
                StatusMessage = "Không thể thử lại tác vụ này.";
            }
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private async Task GenerateAsync()
    {
        if (!CanGenerate) return;
        ReleaseVoiceModelMemory();
        if (IsLocalStudioMode && (SelectedProject == null || string.Equals(SelectedProject.Status, "Archived", StringComparison.OrdinalIgnoreCase) || SelectedCharacter == null || SelectedCharacter.ProjectId != SelectedProject.Id))
        {
            await EnsureAutoWorkspaceAsync();
        }
        if (SelectedProject == null || SelectedCharacter == null) return;
        var fullQuality = QualityModeIndex == 1;
        if (fullQuality && (!IsFullQualityAvailable || IsImg2ImgMode))
        {
            StatusMessage = "Chế độ Full dùng với tạo ảnh từ prompt. Kiểm tra cấu hình máy chủ và chế độ ảnh tham chiếu.";
            return;
        }
        if (SelectedPromptMode != PromptMode.Structured && string.IsNullOrWhiteSpace(RawPromptInput) && !HasActiveOutfit)
        { StatusMessage = "Nhập mô tả ý tưởng hoặc chọn một trang phục trước khi tạo ảnh."; return; }
        if (ImageWidth < 256 || ImageHeight < 256 || ImageWidth > 1536 || ImageHeight > 1536 || ImageWidth % 64 != 0 || ImageHeight % 64 != 0 || RequestedImageCount is < 1 or > 4)
        { StatusMessage = "Kích thước phải là bội số 64, từ 256 đến 1536; số ảnh từ 1 đến 4."; return; }
        var projectId = SelectedProject.Id; var characterId = SelectedCharacter.Id;
        var userVisiblePrompt = RawPromptInput.Trim();
        var effectivePrompt = IsLocalStudioMode
            ? ComposeEffectiveStudioPrompt(userVisiblePrompt)
            : userVisiblePrompt;
        var activeStyleLabel = IsLocalStudioMode && HasSelectedStylePreset ? SelectedStylePresetLabel : null;
        var activeOverlay = IsLocalStudioMode && HasOverlayText ? OverlayText.Trim() : null;
        _generationMonitorCts?.Cancel();
        _generationMonitorCts?.Dispose();
        _generationMonitorCts = new CancellationTokenSource();
        var ct = _generationMonitorCts.Token;
        IsGenerating = true; IsBusy = true;
        ElapsedSeconds = 0;
        GenerationStatusText = "Đang gửi yêu cầu đến máy chủ…";
        var displayTurnPrompt = !string.IsNullOrWhiteSpace(userVisiblePrompt)
            ? (HasActiveOutfit ? $"{userVisiblePrompt} · 👗 {ActiveOutfitLabel}" : userVisiblePrompt)
            : (HasActiveOutfit ? $"👗 {ActiveOutfitLabel}" : "Tạo tác phẩm AI");
        BeginChatTurn(displayTurnPrompt, ImageWidth, ImageHeight, fullQuality, activeStyleLabel, activeOverlay);
        try
        {
            if (HasStudioFaceLock)
                await EnsureActiveStudioFaceLockUploadedAsync(ct);
            if (HasSecondaryStudioCharacter)
                await EnsureSecondaryStudioFaceLockUploadedAsync(ct);
            if (IsImg2ImgMode && HasAttachedReference)
                await EnsureAttachedReferenceUploadedAsync(ct);

            var activeLoras = GetActiveLoraSelections();

            async Task<Guid?> SubmitOnceAsync()
            {
                var pId = SelectedProject!.Id;
                var cId = SelectedCharacter!.Id;
                var primaryFaceId = GetActiveStudioPrimaryFaceId();
                var extraFaceIds = GetActiveStudioAdditionalFaceIds();
                var secondaryFaceId = GetSecondaryStudioPrimaryFaceId();
                var secondaryExtraFaceIds = GetSecondaryStudioAdditionalFaceIds();

                // Prompt composition and the immutable snapshot remain authoritative on the server.
                return await _api.SubmitGenerationAsync(pId, cId, SelectedVariant?.Id,
                    SelectedPromptMode, effectivePrompt, _lastPreviewKey == CurrentPromptKey() ? Prompt.Trim() : null, ImageWidth, ImageHeight, RequestedImageCount,
                    "1.0", IsImg2ImgMode ? SelectedReferenceVersion?.Id : null, IsImg2ImgMode ? Denoise : null, ct,
                    seed: GenerationSeed, fullQuality: fullQuality,
                    depthPoseId: !IsImg2ImgMode ? ActiveDepthPoseId : null,
                    depthStrength: !IsImg2ImgMode && HasActiveDepthPose ? ActiveDepthStrength : null,
                    faceReferenceConceptVersionId: primaryFaceId,
                    faceSwapStrength: primaryFaceId.HasValue ? ActiveStudioFaceStrength : null,
                    faceRestoreStrength: primaryFaceId.HasValue ? ActiveStudioFaceRestoreStrength : null,
                    additionalFaceReferenceConceptVersionIds: extraFaceIds,
                    outfitId: ActiveOutfitId,
                    secondaryFaceReferenceConceptVersionId: secondaryFaceId,
                    secondaryFaceSwapStrength: secondaryFaceId.HasValue ? SecondaryStudioFaceStrength : null,
                    secondaryFaceRestoreStrength: secondaryFaceId.HasValue ? (SecondaryStudioCharacter?.FaceRestoreStrength ?? 0.40) : null,
                    secondaryAdditionalFaceReferenceConceptVersionIds: secondaryExtraFaceIds,
                    loras: activeLoras);
            }

            var jobId = await SubmitOnceAsync();
            if (jobId == null && _api.LastStatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Transparent 404 self-healing: stale face/reference ConceptVersionId from another project/DB (or stale character)
                UpdateActiveChatTurnProgress("Đang đồng bộ lại dữ liệu dự án & gương mặt…");
                if (IsLocalStudioMode && SelectedProject != null && SelectedCharacter != null)
                {
                    var charStillValid = await _api.GetCharacterAsync(SelectedProject.Id, SelectedCharacter.Id, ct) != null;
                    if (!charStillValid)
                    {
                        _restoringSelection = true;
                        try
                        {
                            SelectedProject = null;
                            SelectedCharacter = null;
                        }
                        finally { _restoringSelection = false; }
                        await EnsureAutoWorkspaceAsync();
                    }
                }
                if (SelectedProject != null && SelectedCharacter != null)
                {
                    projectId = SelectedProject.Id;
                    characterId = SelectedCharacter.Id;
                    if (HasStudioFaceLock)
                        await EnsureActiveStudioFaceLockUploadedAsync(ct, forceReupload: true);
                    if (HasSecondaryStudioCharacter)
                        await EnsureSecondaryStudioFaceLockUploadedAsync(ct, forceReupload: true);
                    if (IsImg2ImgMode && HasAttachedReference)
                        await EnsureAttachedReferenceUploadedAsync(ct, forceReupload: true);
                    jobId = await SubmitOnceAsync();
                }
            }
            if (jobId == null)
            {
                StatusMessage = ApiError("Máy chủ không chấp nhận yêu cầu.");
                GenerationStatusText = StatusMessage;
                FailActiveChatTurn(StatusMessage);
                return;
            }
            StatusMessage = $"Đã đưa yêu cầu vào hàng đợi. Mã tác vụ: {jobId:D}";
            UpdateActiveChatTurnProgress("Đã vào hàng đợi GPU…");
            IsBusy = false;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var startedRunning = false;
            using var tickerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var tickerTask = RunElapsedTickerAsync(clock, tickerCts.Token);
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var job = await _api.GetJobStatusAsync(projectId, jobId.Value, ct);
                    if (SelectedProject?.Id != projectId) return;
                    if (job == null)
                    {
                        StatusMessage = ApiError("Chưa đọc được trạng thái tác vụ. Có thể mở Hàng đợi để thử lại.");
                        FailActiveChatTurn(StatusMessage);
                        return;
                    }
                    var index = -1;
                    for (var qIdx = 0; qIdx < GenerationQueue.Count; qIdx++)
                    {
                        if (GenerationQueue[qIdx].Id == job.Id) { index = qIdx; break; }
                    }
                    if (index < 0) GenerationQueue.Insert(0, job); else GenerationQueue[index] = job;
                    SelectedQueueJob = job;
                    if (!startedRunning && job.Status == GenerationStatus.Running)
                    {
                        startedRunning = true;
                        clock.Restart();
                    }
                    var batchProgress = job.RequestedCount > 1 && job.Status == GenerationStatus.Running
                        ? $" ({Math.Min(job.CompletedCount + 1, job.RequestedCount)}/{job.RequestedCount})"
                        : string.Empty;
                    GenerationPhaseText = job.FullQuality && job.Status == GenerationStatus.Running
                        ? $"Đang tạo và hoàn thiện ảnh{batchProgress}" : $"{JobStatusLabel(job.Status)}{batchProgress}";
                    LastJobInfo = $"{job.Id:D} · {job.Width} × {job.Height} · Seed {job.Seed} · {(job.IsSimulated ? "SIMULATED" : "ComfyUI")} · {job.Status}";
                    if (job.Status == GenerationStatus.Completed)
                    {
                        tickerCts.Cancel();
                        var finalDuration = ComputeJobDuration(job, clock.Elapsed);
                        ElapsedSeconds = Math.Max(0, (int)Math.Round(finalDuration.TotalSeconds));
                        GenerationStatusText = $"{GenerationPhaseText} · {FormatElapsed(finalDuration, showDecimal: true)}";
                        var batchResults = new List<(Guid AssetId, long Seed, byte[] Bytes, BitmapImage Image)>();
                        if (job.OutputAssetIds.Count > 0)
                        {
                            // Download & decode multi-image batches in parallel (preserving exact index order);
                            // single image stays native resolution so StudioImageSize & 1:1 inspection show exact pixels.
                            var decodeWidth = job.OutputAssetIds.Count > 1 ? 768 : 0;
                            var downloadTasks = job.OutputAssetIds.Select(async (outAssetId, imgIdx) =>
                            {
                                var bytes = await _api.DownloadImageBytesAsync(projectId, outAssetId, ct);
                                if (bytes == null) return ((Guid AssetId, long Seed, byte[] Bytes, BitmapImage Image)?)null;
                                var decoded = await DecodeImageAsync(bytes, decodeWidth: decodeWidth);
                                if (decoded == null) return null;
                                return (outAssetId, job.Seed + imgIdx, bytes, decoded);
                            }).ToArray();

                            var downloaded = await Task.WhenAll(downloadTasks);
                            foreach (var item in downloaded)
                            {
                                if (item.HasValue) batchResults.Add(item.Value);
                            }
                        }

                        if (batchResults.Count > 0 && SelectedProject?.Id == projectId && SelectedCharacter?.Id == characterId)
                        {
                            var primary = batchResults[0];
                            GeneratedImage = primary.Image;
                            CompleteActiveChatTurnBatch(batchResults, GenerationStatusText, job.IsSimulated);
                            // Only clear RawPromptInput if user hasn't started typing a brand-new prompt while waiting
                            if (!job.IsSimulated && string.Equals(RawPromptInput.Trim(), userVisiblePrompt, StringComparison.Ordinal))
                                RawPromptInput = string.Empty;
                            StatusMessage = job.IsSimulated ? "Tác vụ mô phỏng đã hoàn tất (SIMULATED)."
                                : job.FullQuality ? "Đã tạo và hoàn thiện ảnh Full." : "Đã tạo ảnh xong! Nhấn vào ảnh để soi 1:1 hoặc tải về.";
                        }
                        else
                        {
                            StatusMessage = "Tác vụ hoàn tất nhưng không tải được ảnh kết quả từ máy chủ.";
                            FailActiveChatTurn(StatusMessage, job.IsSimulated);
                        }
                        break;
                    }
                    if (job.Status is GenerationStatus.Failed or GenerationStatus.Cancelled or GenerationStatus.Interrupted)
                    {
                        tickerCts.Cancel();
                        var finalDuration = ComputeJobDuration(job, clock.Elapsed);
                        ElapsedSeconds = Math.Max(0, (int)Math.Round(finalDuration.TotalSeconds));
                        GenerationStatusText = $"{GenerationPhaseText} · {FormatElapsed(finalDuration, showDecimal: true)}";
                        StatusMessage = $"{GenerationPhaseText}. {job.ErrorSummary}";
                        FailActiveChatTurn(StatusMessage, job.IsSimulated);
                        break;
                    }
                    // Adaptive loopback polling: 250ms first 12s, 450ms 12-30s, 750ms after (cuts ~500-750ms dead wait!)
                    var pollDelayMs = !_api.IsLoopbackBaseUrl ? 1000
                        : clock.Elapsed.TotalSeconds < 12 ? 250
                        : clock.Elapsed.TotalSeconds < 30 ? 450
                        : 750;
                    await Task.Delay(pollDelayMs, ct);
                }
            }
            finally
            {
                tickerCts.Cancel();
                try { await tickerTask; } catch (OperationCanceledException) { }
            }
        }
        catch (OperationCanceledException) { /* Stops local monitoring only; the server job is durable. */ }
        catch (Exception ex) { ReportError(ex); FailActiveChatTurn(ex.Message); }
        finally { if (!ct.IsCancellationRequested) { IsBusy = false; IsGenerating = false; } }
    }

    private async Task RunElapsedTickerAsync(System.Diagnostics.Stopwatch clock, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(ct))
        {
            if (string.IsNullOrWhiteSpace(GenerationPhaseText)) continue;
            var elapsed = clock.Elapsed;
            ElapsedSeconds = (int)elapsed.TotalSeconds;
            GenerationStatusText = $"{GenerationPhaseText} · {FormatElapsed(elapsed, showDecimal: false)}";
            UpdateActiveChatTurnProgress(GenerationStatusText);
        }
    }

    private static TimeSpan ComputeJobDuration(GenerationJobDto job, TimeSpan fallbackElapsed)
    {
        if (job.CompletedAt.HasValue)
        {
            var start = job.StartedAt ?? job.RequestedAt;
            var diff = job.CompletedAt.Value - start;
            if (diff >= TimeSpan.Zero) return diff;
        }
        return fallbackElapsed;
    }

    private static string FormatElapsed(TimeSpan elapsed, bool showDecimal)
    {
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        if (elapsed.TotalSeconds < 60)
            return showDecimal ? $"{elapsed.TotalSeconds:0.0}s" : $"{(int)elapsed.TotalSeconds}s";
        var minutes = (int)elapsed.TotalMinutes;
        var seconds = elapsed.Seconds;
        return showDecimal
            ? $"{minutes}p {seconds + elapsed.Milliseconds / 1000.0:00.0}s"
            : $"{minutes}p {seconds:D2}s";
    }

    private bool HasGeneratedImage => GeneratedImage != null;
    private bool HasPromptText => !string.IsNullOrWhiteSpace(Prompt) || !string.IsNullOrWhiteSpace(RawPromptInput);

    [RelayCommand(CanExecute = nameof(HasGeneratedImage))]
    private void SaveCanvasImage()
    {
        var image = GeneratedImage;
        if (image == null)
        {
            StatusMessage = "Chưa có ảnh nào trên Canvas để lưu!";

            return;
        }

        try
        {
            var charName = SelectedCharacter?.Name ?? "Character";
            var cleanChar = string.Concat(charName.Split(Path.GetInvalidFileNameChars())).Replace(" ", "_");
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Lưu ảnh concept",
                Filter = "PNG Image (*.png)|*.png|JPEG Image (*.jpg)|*.jpg",
                FileName = $"CharacterConcept_{cleanChar}_{DateTime.Now:yyyyMMdd_HHmmss}.png"
            };

            if (dialog.ShowDialog() == true)
            {
                BitmapEncoder encoder = dialog.FileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || dialog.FileName.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                    ? new JpegBitmapEncoder { QualityLevel = 95 }
                    : new PngBitmapEncoder();

                encoder.Frames.Add(BitmapFrame.Create(image));
                using (var fs = File.Create(dialog.FileName))
                {
                    encoder.Save(fs);
                }
                StatusMessage = $"Đã lưu ảnh thành công vào: {Path.GetFileName(dialog.FileName)}";

            }
        }
        catch (Exception ex)
        {
            ReportError(ex);

        }
    }

    [RelayCommand(CanExecute = nameof(HasGeneratedImage))]
    private void CopyCanvasImage()
    {
        var image = GeneratedImage;
        if (image == null)
        {
            StatusMessage = "Chưa có ảnh nào trên Canvas để sao chép!";
            return;
        }

        try
        {
            System.Windows.Clipboard.SetImage(image);
            StatusMessage = "Đã sao chép ảnh tác phẩm vào Clipboard!";
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
    }

    [RelayCommand(CanExecute = nameof(HasPromptText))]
    private void ClearPrompt()
    {
        RawPromptInput = string.Empty;
        Prompt = string.Empty;
        StatusMessage = "Đã xóa trắng khung mô tả prompt.";
    }

    [RelayCommand(CanExecute = nameof(HasPromptText))]
    private void CopyPrompt()
    {
        var text = !string.IsNullOrWhiteSpace(Prompt) ? Prompt : RawPromptInput;
        if (!string.IsNullOrWhiteSpace(text))
        {
            try
            {
                System.Windows.Clipboard.SetText(text);
                StatusMessage = "Đã sao chép prompt vào Clipboard!";
            }
            catch (Exception ex) { ReportError(ex); }
        }
    }

    private bool CanOpenStudio(CharacterDto? character) => character != null && SelectedProject != null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanOpenStudio))]
    private void SwitchToStudioWithCharacter(CharacterDto? character)
    {
        if (!CanOpenStudio(character) || !ConfirmDiscardChanges()) return;
        SelectedCharacter = character;
        if (string.IsNullOrWhiteSpace(RawPromptInput)) RawPromptInput = character?.Description ?? string.Empty;
        CurrentTabIndex = 0;
        StatusMessage = $"Đã chuyển sang Prompt Studio để vẽ nhân vật: {SelectedCharacter?.Name ?? "(Chưa chọn)"}";
    }

    [RelayCommand]
    private async Task RefreshConceptsAsync()
    {
        if (SelectedProject == null) return;
        if (SelectedCharacter != null) await LoadCharacterConceptsAsync(SelectedProject.Id, SelectedCharacter.Id);
        else await LoadProjectConceptsAsync(SelectedProject.Id);
    }

    private async Task LoadCharacterConceptsAsync(Guid projectId, Guid characterId)
    {
        var revision = ++_conceptQueryRevision;
        var all = await _api.GetCharacterConceptsAsync(projectId, characterId);
        if (revision != _conceptQueryRevision) return;
        var list = all.Where(c => ConceptServerStatus == null || (ConceptServerStatus == "PendingReview" ? c.Status.ToString() == "UnderReview" : c.Status.ToString() == ConceptServerStatus)).ToList();
        if (SelectedProject?.Id != projectId || SelectedCharacter?.Id != characterId) return;
        var selectedId = SelectedConcept?.Id;
        CharacterConcepts.Clear();
        foreach (var c in list) CharacterConcepts.Add(c);
        ConceptTotalCount = list.Count;
        SelectedConcept = list.FirstOrDefault(c => c.Id == selectedId) ?? list.FirstOrDefault();
        Observe(LoadConceptThumbnailsAsync(projectId, list));
    }

    private async Task LoadConceptDetailAsync(Guid projectId, Guid conceptId)
    {
        var detail = await _api.GetConceptDetailAsync(projectId, conceptId);
        if (SelectedProject?.Id != projectId || SelectedConcept?.Id != conceptId) return;
        var versionId = SelectedVersion?.Id;
        ConceptVersions.Clear();
        if (detail?.Versions != null)
        {
            foreach (var v in detail.Versions) ConceptVersions.Add(v);
            SelectedVersion = ConceptVersions.FirstOrDefault(v => v.Id == versionId) ?? ConceptVersions.OrderByDescending(v => v.VersionNumber).FirstOrDefault();
            if (ConceptVersions.Count >= 2)
            {
                CompareVersionLeft = ConceptVersions[0];
                CompareVersionRight = ConceptVersions[^1];
            }
        }
    }

    private async Task LoadVersionImageAsync(Guid projectId, Guid assetId)
    {
        try
        {
            ConceptVersionImage = null;
            var bytes = await _api.DownloadImageBytesAsync(projectId, assetId);
            if (bytes == null) { StatusMessage = ApiError("Không thể tải ảnh. Hãy làm mới bộ sưu tập để thử lại."); return; }
            var bitmap = await DecodeImageAsync(bytes);
            if (SelectedProject?.Id == projectId && SelectedVersion?.ImageAssetId == assetId) ConceptVersionImage = bitmap;
        }
        catch (Exception ex) { ReportError(ex); }
    }

    [RelayCommand]
    private async Task CompareVersionsAsync()
    {
        if (SelectedProject == null || SelectedConcept == null || CompareVersionLeft == null || CompareVersionRight == null)
        { StatusMessage = "Chọn hai phiên bản để so sánh."; return; }
        var projectId = SelectedProject.Id; var conceptId = SelectedConcept.Id;
        var leftId = CompareVersionLeft.Id; var rightId = CompareVersionRight.Id;
        IsBusy = true;
        try
        {
            var result = await _api.CompareConceptVersionsAsync(projectId, conceptId, leftId, rightId);
            if (result == null) { StatusMessage = ApiError("Không tải được phiên bản so sánh."); return; }
            var left = await _api.DownloadImageBytesAsync(projectId, result.Left.ImageAssetId);
            var right = await _api.DownloadImageBytesAsync(projectId, result.Right.ImageAssetId);
            var leftImage = left == null ? null : await DecodeImageAsync(left);
            var rightImage = right == null ? null : await DecodeImageAsync(right);
            if (SelectedProject?.Id != projectId || SelectedConcept?.Id != conceptId || CompareVersionLeft?.Id != leftId || CompareVersionRight?.Id != rightId) return;
            ComparisonResult = result; CompareLeftImage = leftImage; CompareRightImage = rightImage;
            StatusMessage = $"Đang so sánh phiên bản {result.Left.VersionNumber} và {result.Right.VersionNumber}.";
        }
        catch (Exception ex) { ReportError(ex); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void UseVersionAsReference(ConceptVersionDto? version)
    {
        var target = version ?? SelectedVersion;
        if (target == null) return;
        if (!IsImg2ImgAvailable) { StatusMessage = Img2ImgAvailabilityReason; return; }
        SelectedReferenceVersion = target;
        IsImg2ImgMode = true;
        StatusMessage = $"Đã kích hoạt Img2Img với ảnh mẫu v{target.VersionNumber} (Denoise: {Denoise:F2}).";
        _ = LoadReferencePreviewAsync(target);
    }

    [RelayCommand]
    private void ClearReference()
    {
        SelectedReferenceVersion = null;
        ReferencePreviewImage = null;
        IsImg2ImgMode = false;
        StatusMessage = "Đã tắt Img2Img (quay lại sinh ảnh Text-to-Image).";
    }

    // Z-Image Paper Fig. 20 (p.37): 3x3 single-pass expression sheet on clean studio background.
    [RelayCommand]
    private void ApplyExpressionSheetPreset()
    {
        var isAnimeMode = string.Equals(SelectedStylePresetKey, "anime", StringComparison.OrdinalIgnoreCase);
        var subject = ActiveStudioCharacter != null
            ? ActiveStudioCharacter.Name
            : (isAnimeMode ? "the same young anime character" : "the same young person");

        // Keep Raw mode so auto-workspace placeholder ("2D anime concept art, Character: Mèo") NEVER contaminates the sheet!
        SelectedPromptMode = PromptMode.Raw;
        ImageWidth = 1024;
        ImageHeight = 1024;
        RawPromptInput = isAnimeMode
            ? $"A 3x3 grid collage of nine studio close-up anime portraits of {subject} on a pure white background, consistent identity, hair, and outfit across all nine panels. " +
              "Top row, left to right: winking playfully with a slight smile; laughing joyfully with eyes tightly closed; surprised with wide eyes and blushing cheeks. " +
              "Middle row, left to right: calm neutral expression looking directly at the camera; confident smirk with head slightly tilted; frowning seriously with furrowed brows. " +
              "Bottom row, left to right: cheerful peace sign gesture with bright eyes; embarrassed shy expression looking slightly away; gentle thoughtful expression resting cheek on one hand. " +
              "Bright, even studio lighting across all panels, crisp clean linework, high detail."
            : $"A 3x3 grid collage of nine photorealistic real-life studio close-up portrait photographs of {subject} on a clean neutral light-gray studio background, identical real person, identical hair and lighting across all nine panels. " +
              "Top row, left to right: winking playfully with a slight smile; laughing joyfully with bright warm smile; surprised with wide eyes and softly parted lips. " +
              "Middle row, left to right: calm neutral expression looking directly at the camera; confident subtle smirk with head slightly tilted; pouting thoughtfully with slightly furrowed brows. " +
              "Bottom row, left to right: cheerful peace sign gesture with sparkling eyes; shy blushing smile looking slightly aside; gentle serene expression resting cheek on one hand. " +
              "Soft even studio photography lighting across all nine panels, natural realistic skin texture, sharp focus.";
        StatusMessage = ActiveStudioCharacter != null
            ? $"Đã áp dụng Bảng 9 biểu cảm chân thực 3×3 (1024×1024) cho {ActiveStudioCharacter.Name} — tự khóa cả 9 mặt bằng HyperSwap 256!"
            : "Đã áp dụng mẫu Bảng 9 biểu cảm 3×3 (1024×1024).";
    }

    private async Task LoadReferencePreviewAsync(ConceptVersionDto version)
    {
        if (SelectedProject == null) return;
        try
        {
            var bytes = await _api.DownloadImageBytesAsync(SelectedProject.Id, version.ImageAssetId);
            if (bytes != null)
            {
                var bitmap = await DecodeImageAsync(bytes, 512);
                if (SelectedReferenceVersion?.Id == version.Id) ReferencePreviewImage = bitmap;
            }
        }
        catch
        {
            // Ignore preview failure
        }
    }
}

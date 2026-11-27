using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls.Primitives;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CharacterConceptAI.Desktop.Features.Views;
using CharacterConceptAI.Desktop.Infrastructure.Settings;
using CharacterConceptAI.Shared.Contracts.Generations;
using CharacterConceptAI.Desktop.Services;
using CharacterConceptAI.Desktop.ViewModels;

namespace CharacterConceptAI.Desktop.Smoke;

internal static class Program
{
    private static readonly JsonSerializerOptions ReportOptions = new() { WriteIndented = true };

    [STAThread]
    public static int Main(string[] args)
    {
        var evidencePath = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine(Environment.CurrentDirectory, "evidence", "ui"));
        Directory.CreateDirectory(evidencePath);
        var report = new SmokeReport();
        var bindingListener = new BindingListener();
        PresentationTraceSources.DataBindingSource.Listeners.Add(bindingListener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        using var handler = new SimulatedApiHandler();
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var application = new App(suppressProductionBootstrap: true);
        var exitCode = 1;
        try
        {
            application.InitializeComponent();
            application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            ThemeService.Instance.Initialize(application);
            var originalTheme = ThemeService.Instance.CurrentTheme;
            var api = new ApiClient(client) { BaseUrl = "https://simulated.example.invalid" };
            var session = new AuthSessionService(api);
            var viewModel = new MainViewModel(api, session);
            var window = new MainWindow(viewModel)
            {
                Title = "CharacterConceptAI · SIMULATED UI smoke test",
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000,
                Top = -10000,
                Width = 1440,
                Height = 900
            };
            application.DispatcherUnhandledException += (_, eventArgs) =>
            {
                report.Failures.Add(eventArgs.Exception.ToString());
                eventArgs.Handled = true;
                application.Shutdown(1);
            };
            var started = false;
            window.ContentRendered += async (_, _) =>
            {
                if (started) return;
                started = true;
                try
                {
                    await ExerciseAsync(window, viewModel, handler, evidencePath, report);
                    await PumpAsync(window);
                    report.BindingErrors.AddRange(bindingListener.Messages.Distinct());
                    if (report.BindingErrors.Count > 0) report.Failures.Add($"{report.BindingErrors.Count} WPF binding warnings/errors.");
                    if (handler.UnhandledRequests.Count > 0) report.Failures.Add("Some requests did not match the SIMULATED API fixture.");
                    report.Checks.Add("Every HTTP request was intercepted in memory; no SQL, LAN server, or GPU was contacted.");
                    report.Checks.Add("All visual evidence was rendered from the real WPF visual tree with synthetic fixture data; native titlebar excluded.");
                    exitCode = report.Failures.Count == 0 ? 0 : 1;
                }
                catch (Exception exception) { report.Failures.Add(exception.ToString()); }
                finally
                {
                    ThemeService.Instance.SetTheme(originalTheme, persist: false);
                    application.Shutdown(exitCode);
                }
            };
            application.Run(window);
        }
        catch (Exception exception) { report.Failures.Add(exception.ToString()); }
        finally
        {
            report.BindingErrors.AddRange(bindingListener.Messages.Except(report.BindingErrors));
            report.Requests.AddRange(handler.Requests);
            report.UnhandledRequests.AddRange(handler.UnhandledRequests);
            report.Result = exitCode == 0 && report.Failures.Count == 0 ? "PASS_SIMULATED" : "FAIL";
            File.WriteAllText(Path.Combine(evidencePath, "wpf-smoke-report.json"), JsonSerializer.Serialize(report, ReportOptions), new UTF8Encoding(false));
            PresentationTraceSources.DataBindingSource.Listeners.Remove(bindingListener);
        }
        Console.WriteLine($"{report.Result}: {report.Captures.Count} WPF renders; {report.Checks.Count} checks; {report.BindingErrors.Count} binding messages; {report.Failures.Count} failures.");
        foreach (var failure in report.Failures.Take(4)) Console.WriteLine(failure.Length > 1500 ? failure[..1500] : failure);
        Console.WriteLine(Path.Combine(evidencePath, "wpf-smoke-report.json"));
        return report.Result == "PASS_SIMULATED" ? 0 : 1;
    }

    private static async Task ExerciseCharacterCreatorAsync(
        MainWindow window, MainViewModel vm, SimulatedApiHandler handler, string output, SmokeReport report)
    {
        vm.NavigateTo(1);
        await PumpAsync(window);
        var open = Descendants(window).OfType<Button>().Single(b =>
            b.IsVisible && AutomationProperties.GetName(b) == "Mở biểu mẫu tạo nhân vật");
        Require(open.IsEnabled, "Project owner can open the create-character dialog from the page header", report);
        ((IInvokeProvider)new ButtonAutomationPeer(open).GetPattern(PatternInterface.Invoke)).Invoke();
        await PumpAsync(window);
        Require(vm.IsCreateCharacterDialogOpen && !vm.CreateCharacterCommand.CanExecute(null),
            "Quick character creation opens immediately and requires only a nonblank name", report);

        var roleOptions = Descendants(window).OfType<Expander>().Single(e =>
            Equals(e.Header, "Đổi vai trò (mặc định: nhân vật chính)"));
        roleOptions.IsExpanded = true;
        await PumpAsync(window);
        var role = Descendants(window).OfType<ComboBox>().Single(box =>
            AutomationProperties.GetName(box) == "Vai trò nhân vật mới");
        role.IsDropDownOpen = true;
        await PumpAsync(window);
        role.ApplyTemplate();
        var popup = (Popup)role.Template.FindName("PART_Popup", role);
        Require(popup.IsOpen && role.Items.Count == 4 &&
            role.ItemContainerGenerator.ContainerFromIndex(0) is ComboBoxItem,
            "Role selector opens a real WPF popup that realizes the available options", report);
        role.SelectedIndex = 3;
        role.IsDropDownOpen = false;
        Require(vm.NewCharacterRole == "Supporting", "A dropdown selection updates the form binding", report);
        roleOptions.IsExpanded = false;

        vm.NewCharacterName = "  Nhân vật thử nhanh  ";
        vm.NewCharacterDescription = "  Tóc bạc và áo choàng xanh  ";
        handler.RejectCharacterCreate = true;
        await vm.CreateCharacterCommand.ExecuteAsync(null);
        Require(vm.IsCreateCharacterDialogOpen && vm.CharacterCreateError.Length > 0 &&
            vm.NewCharacterDescription.Contains("Tóc bạc"), "Creation failure keeps the form and shows an inline error", report);
        await CaptureAsync(window, vm, output, report, "light-character-create-error", 1100, 700);

        handler.RejectCharacterCreate = false;
        await vm.CreateCharacterCommand.ExecuteAsync(null);
        await PumpAsync(window);
        Require(!vm.IsCreateCharacterDialogOpen && vm.SelectedCharacter?.Name == "Nhân vật thử nhanh" &&
            handler.CreatedCharacterRequest is { Role: "Supporting", Description: "Tóc bạc và áo choàng xanh" },
            "One save sends the name, optional role and description and selects the created profile", report);

        vm.RawPromptInput = string.Empty;
        vm.SwitchToStudioWithCharacterCommand.Execute(vm.SelectedCharacter);
        Require(vm.CurrentTabIndex == 0 && vm.RawPromptInput == "Tóc bạc và áo choàng xanh",
            "Opening Studio after creation reuses the description in an empty prompt", report);

        await CaptureAsync(window, vm, output, report, "light-studio-after-quick-create-1100x700", 1100, 700);
        var generate = Descendants(window).OfType<Button>().Single(b =>
            AutomationProperties.GetName(b) == "Gửi tác vụ tạo concept");
        var buttonBounds = generate.TransformToAncestor(window).TransformBounds(new Rect(generate.RenderSize));
        Require(generate.IsVisible && generate.IsEnabled && buttonBounds.Bottom < window.ActualHeight - 25,
            "Generate action remains visible and enabled at the minimum supported window size", report);
        Require(!vm.SaveCanvasImageCommand.CanExecute(null) && !vm.CopyCanvasImageCommand.CanExecute(null),
            "Output actions require a generated image", report);
        await vm.PreviewPromptCommand.ExecuteAsync(null);
        Require(vm.IsPromptPreviewOpen && vm.Prompt.Length > 0,
            "Preview automatically reveals the final prompt", report);

        var empty = new ComboBox();
        empty.Style = (Style)window.FindResource(typeof(ComboBox));
        empty.ApplyTemplate();
        var emptyOptions = (TextBlock)empty.Template.FindName("EmptyOptions", empty);
        Require(emptyOptions.Visibility == Visibility.Visible && emptyOptions.Text.Length > 0,
            "Empty dropdown displays a message rather than a blank popup", report);
    }

    private static async Task ExerciseAsync(MainWindow window, MainViewModel vm, SimulatedApiHandler handler, string output, SmokeReport report)
    {
        foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
        {
            ThemeService.Instance.SetTheme(theme, persist: false);
            await CaptureAsync(window, vm, output, report, $"{theme.ToString().ToLowerInvariant()}-login-1440x900", 1440, 900);
        }
        vm.Email = "smoke@example.invalid";
        vm.Password = "SIMULATED-fixture-only";
        await vm.LoginCommand.ExecuteAsync(null);
        Require(vm.IsLoggedIn && vm.IsAdmin && vm.Projects.Count == 4, "Login command -> fixture bearer token/profile/projects/capabilities", report);
        Require(vm.Password.Length == 0, "Login clears the password from the view model", report);
        Require(vm.GenerationProviderLabel.Contains("SIMULATED", StringComparison.Ordinal) && !vm.IsImg2ImgAvailable,
            "Generation capability uses explicit SIMULATED provenance and gates unqualified Img2Img", report);

        vm.OpenProjectCommand.Execute(vm.Projects[0]);
        await PumpAsync(window);
        Require(vm.Characters.Count == 3 && vm.IsProjectOwner && vm.CanCreateInProject, "Project scope loads fixture characters, members, and Owner permissions", report);
        vm.SelectedCharacter = vm.Characters[0];
        vm.SelectedScript = vm.Scripts.FirstOrDefault();
        await PumpAsync(window);
        vm.SelectedScene = vm.Scenes.First();
        var visibleLink = vm.SceneCharacterLinks.Single(link => link.CharacterId == SimulatedApiHandler.CharacterId);
        Require(visibleLink.IsLinked && vm.SelectedScene.LinkedCharacterIds.Contains(SimulatedApiHandler.OffPageCharacterId),
            "Scene fixture has a visible linked character and an existing linked character outside the current page", report);
        visibleLink.IsLinked = false;
        await vm.SaveSceneLinksCommand.ExecuteAsync(null);
        Require(handler.SubmittedSceneCharacterIds is { } links && links.Contains(SimulatedApiHandler.OffPageCharacterId)
            && !links.Contains(SimulatedApiHandler.CharacterId),
            "Saving scene links preserves off-page character IDs and removes only an unchecked visible character", report);
        Require(!vm.HasUnsavedSceneChanges && vm.SelectedScene.LinkedCharacterIds.Contains(SimulatedApiHandler.OffPageCharacterId),
            "Scene link save reloads the fixture scene and resets the saved editor state", report);
        vm.RawPromptInput = "SIMULATED · Trang phục thám hiểm mùa đông, đường nét rõ ràng, giữ đặc điểm nhận dạng.";
        vm.StatusMessage = "SIMULATED · Dữ liệu tổng hợp để kiểm tra giao diện WPF. Không có tác vụ GPU nào được chạy.";

        foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
        {
            ThemeService.Instance.SetTheme(theme, persist: false);
            foreach (var route in new[] { 10, 6, 11, 1, 0, 12, 2, 3, 4, 7, 5, 8, 9 })
            {
                vm.NavigateTo(route);
                await PumpAsync(window);
                await CaptureAsync(window, vm, output, report, $"{theme.ToString().ToLowerInvariant()}-route-{route:00}-1440x900", 1440, 900);
                Require(vm.CurrentTabIndex == route, $"{theme} route {route}: {vm.PageTitle}", report);
            }
            foreach (var (width, height) in new[] { (1100, 700), (1366, 768), (1920, 1080) })
            {
                foreach (var route in new[] { 6, 1, 0 })
                {
                    vm.NavigateTo(route);
                    await CaptureAsync(window, vm, output, report, $"{theme.ToString().ToLowerInvariant()}-route-{route:00}-{width}x{height}", width, height);
                }
            }
            vm.NavigateTo(6); vm.IsProjectListView = true;
            await CaptureAsync(window, vm, output, report, $"{theme.ToString().ToLowerInvariant()}-projects-list-1440x900", 1440, 900);
            vm.IsProjectListView = false;
            vm.NavigateTo(1); vm.IsCharacterListView = true;
            await CaptureAsync(window, vm, output, report, $"{theme.ToString().ToLowerInvariant()}-characters-list-1440x900", 1440, 900);
            vm.IsCharacterListView = false;
        }

        // The collection view filter runs locally; query/selection state must survive a view toggle.
        vm.NavigateTo(1); vm.CharacterSearchText = "Nguyệt";
        Require(vm.FilteredCharacters.Cast<object>().Count() == 1, "Vietnamese character search filters the collection", report);
        var selectedId = vm.SelectedCharacter?.Id;
        vm.ToggleCharacterViewCommand.Execute(null);
        Require(vm.CharacterSearchText == "Nguyệt" && vm.SelectedCharacter?.Id == selectedId, "Grid/list switching preserves query and selection", report);
        vm.CharacterSearchText = "không tồn tại";
        await CaptureAsync(window, vm, output, report, "light-characters-filtered-empty", 1440, 900);
        Require(!vm.FilteredCharacters.Cast<object>().Any(), "Filtered-empty state differs from populated fixture", report);
        vm.CharacterSearchText = string.Empty; vm.IsCharacterListView = false;

        vm.NavigateTo(0);
        vm.SelectedCharacter = vm.Characters.First(character => character.Id == SimulatedApiHandler.CharacterId);
        vm.RawPromptInput = "SIMULATED · Upscale test";
        await PumpAsync(window);
        Require(vm.QualityModeIndex == 0 && vm.IsFullQualityAvailable, "Studio defaults to the original workflow when Full is enabled.", report);
        await vm.GenerateCommand.ExecuteAsync(null);
        Require(!handler.FullQualityRequested && vm.GeneratedImage?.PixelWidth == 2, "Original generates at the source resolution.", report);
        var qualitySelector = Descendants(window).OfType<ComboBox>().Single(box =>
            box.IsVisible && AutomationProperties.GetName(box) == "Chế độ tạo ảnh");
        qualitySelector.ApplyTemplate();
        var qualityPopup = qualitySelector.Template?.FindName("PART_Popup", qualitySelector) as Popup;
        Require(qualityPopup?.Placement == PlacementMode.Top, "Quality dropdown opens upward (Placement=Top) above the composer bar", report);
        qualitySelector.SelectedIndex = 1;
        await vm.GenerateCommand.ExecuteAsync(null);
        Require(handler.FullQualityRequested && vm.GeneratedImage?.PixelWidth == 4 && vm.StudioImageSize == "4 × 4 px",
            "Full submits the quality mode and displays the finished PNG at native resolution.", report);
        await CaptureAsync(window, vm, output, report, "studio-full-quality", 1440, 900);
        vm.QualityModeIndex = 0;

        await ExerciseCharacterCreatorAsync(window, vm, handler, output, report);

        // Exercise Storyboard + Multi-Angle Character Face Pack + Smooth Character Chip Selector + Lightbox + Depth Pose Gallery modal
        vm.NavigateTo(12);
        await PumpAsync(window);
        Require(vm.ActiveStory != null && vm.ActiveStory.Scenes.Count > 0, "Story studio initializes with a starter story and scenes", report);
        if (vm.SelectedStoryCharacter != null)
        {
            var faceBmp = CreateSyntheticBitmap(256, 320);
            vm.SelectedStoryCharacter.FacePreview = faceBmp;
            vm.SelectedStoryCharacter.ExtraFaces.Add(new StoryFaceAngleVm(Guid.NewGuid(), "Trái 3/4 · Cười") { Preview = faceBmp });
            vm.SelectedStoryCharacter.ExtraFaces.Add(new StoryFaceAngleVm(Guid.NewGuid(), "Phải 3/4") { Preview = faceBmp });
            var secondChar = new StoryCharacterVm(Guid.NewGuid(), "Nhân vật 2", "nam trẻ thanh lịch, tóc đen ngắn") { FacePreview = faceBmp };
            var thirdChar = new StoryCharacterVm(Guid.NewGuid(), "Nhân vật 3", "nữ phong cách tối giản");
            vm.ActiveStory!.Characters.Add(secondChar);
            vm.ActiveStory!.Characters.Add(thirdChar);
            vm.SelectStoryCharacterCommand.Execute(secondChar);
            Require(vm.SelectedStoryCharacter == secondChar && vm.SelectedStoryCharacterId == secondChar.Id,
                "Smooth character chip selector switches active Story character cleanly", report);
            vm.SelectStoryCharacterCommand.Execute(vm.ActiveStory!.Characters[0]);
            await PumpAsync(window);
            Require(vm.SelectedStoryCharacter.TotalFaceCount == 3 && vm.SelectedStoryCharacter.FaceLockBadge.Contains("3/5", StringComparison.Ordinal),
                "Multi-angle face pack tracks up to 5 angles cleanly", report);
            ThemeService.Instance.SetTheme(AppTheme.Dark, persist: false);
            await CaptureAsync(window, vm, output, report, "dark-story-multi-angle-1440x900", 1440, 900);
            ThemeService.Instance.SetTheme(AppTheme.Light, persist: false);

            await vm.OpenStoryCharacterFaceLightboxCommand.ExecuteAsync(vm.SelectedStoryCharacter);
            await PumpAsync(window);
            Require(vm.IsStoryLightboxOpen && vm.StoryLightboxImage != null && vm.StoryLightboxTitle.Contains(vm.SelectedStoryCharacter.Name, StringComparison.Ordinal),
                "Clicking character face opens full-resolution Story Lightbox", report);
            await CaptureAsync(window, vm, output, report, "light-story-face-lightbox-1440x900", 1440, 900);
            vm.CloseStoryLightboxCommand.Execute(null);
            await PumpAsync(window);

            await vm.OpenStoryExtraFaceLightboxCommand.ExecuteAsync(vm.SelectedStoryCharacter.ExtraFaces[0]);
            await PumpAsync(window);
            Require(vm.IsStoryLightboxOpen && vm.StoryLightboxTitle.Contains("Trái 3/4", StringComparison.Ordinal),
                "Clicking extra face angle opens full-resolution angle Lightbox", report);
            vm.CloseStoryLightboxCommand.Execute(null);
            await PumpAsync(window);
        }
        await vm.OpenPoseGalleryForStudioCommand.ExecuteAsync(null);
        await PumpAsync(window);
        Require(vm.IsPoseGalleryOpen && vm.PoseGalleryGroups.Count > 0, "Pose gallery modal loads groups from API", report);
        await CaptureAsync(window, vm, output, report, "light-pose-gallery-1440x900", 1440, 900);
        vm.ClosePoseGalleryCommand.Execute(null);
        await PumpAsync(window);

        // Exercise Outfit Gallery modal in Story & Studio (Dark + Light themes)
        var firstScene = vm.ActiveStory!.Scenes[0];
        await vm.OpenOutfitGalleryForSceneCommand.ExecuteAsync(firstScene);
        await PumpAsync(window);
        Require(vm.IsOutfitGalleryOpen && vm.OutfitGalleryItems.Count >= 2 && vm.OutfitGalleryItems[0].ShowTopQualityBadge,
            "Outfit gallery modal loads curated outfits ranked with top quality first", report);
        ThemeService.Instance.SetTheme(AppTheme.Dark, persist: false);
        await CaptureAsync(window, vm, output, report, "dark-outfit-gallery-1440x900", 1440, 900);
        ThemeService.Instance.SetTheme(AppTheme.Light, persist: false);
        await CaptureAsync(window, vm, output, report, "light-outfit-gallery-1440x900", 1440, 900);
        await vm.ApplyOutfitGalleryItemCommand.ExecuteAsync(vm.OutfitGalleryItems[0]);
        await PumpAsync(window);
        Require(!vm.IsOutfitGalleryOpen && firstScene.HasOutfit && !string.IsNullOrWhiteSpace(firstScene.OutfitLabel),
            "Selecting an outfit attaches it to the active Story scene", report);
        ThemeService.Instance.SetTheme(AppTheme.Dark, persist: false);
        await CaptureAsync(window, vm, output, report, "dark-story-with-outfit-1440x900", 1440, 900);

        vm.NavigateTo(0);
        await vm.OpenOutfitGalleryForStudioCommand.ExecuteAsync(null);
        await PumpAsync(window);
        await vm.ApplyOutfitGalleryItemCommand.ExecuteAsync(vm.OutfitGalleryItems[1]);
        await PumpAsync(window);
        Require(vm.HasActiveOutfit && vm.ActiveOutfitSummary.Contains("Đời Đường", StringComparison.Ordinal),
            "Selecting an outfit in Studio shows the active outfit chip in the composer", report);
        await CaptureAsync(window, vm, output, report, "dark-studio-with-outfit-1440x900", 1440, 900);
        ThemeService.Instance.SetTheme(AppTheme.Light, persist: false);
        await CaptureAsync(window, vm, output, report, "light-studio-with-outfit-1440x900", 1440, 900);
        vm.ClearOutfitCommand.Execute(null);
        vm.ClearSceneOutfitCommand.Execute(firstScene);
        await PumpAsync(window);

        // Capture sleek unified Studio Options Popover open
        vm.NavigateTo(0);
        vm.ToggleStudioAdvancedCommand.Execute(null);
        await PumpAsync(window);
        Require(vm.IsStudioAdvancedOpen, "Studio options popover opens cleanly", report);
        ThemeService.Instance.SetTheme(AppTheme.Dark, persist: false);
        await CaptureAsync(window, vm, output, report, "dark-studio-options-popover-1440x900", 1440, 900);
        vm.ToggleStudioAdvancedCommand.Execute(null);
        await PumpAsync(window);

        // Exercise floating composer buttons (inline Paperclip + Quality next to Voice Waveform + external Aspect Ratio & Batch pills) & Sidebar Collapse/Expand next to logo
        var attachBtn = Descendants(window).OfType<Button>().Single(b => b.IsVisible && AutomationProperties.GetName(b) == "Đính kèm ảnh mẫu");
        var voiceBtn = Descendants(window).OfType<Button>().Single(b => b.IsVisible && AutomationProperties.GetName(b) == "Nhập prompt bằng giọng nói");
        var ratioBar = Descendants(window).OfType<Border>().Single(b => b.IsVisible && AutomationProperties.GetName(b) == "Tỉ lệ khung hình");
        var batchBar = Descendants(window).OfType<Border>().Single(b => b.IsVisible && AutomationProperties.GetName(b) == "Số biến thể");
        Require(attachBtn.IsEnabled && voiceBtn.IsEnabled && ratioBar.IsVisible && batchBar.IsVisible,
            "Floating composer displays inline Paperclip, Voice button, and external Aspect Ratio + Batch Count pills", report);
        Require(!string.IsNullOrWhiteSpace(VoiceInputService.ResolveModelPath()), "Local Whisper STT model file is installed and discovered", report);

        // Verify aspect-matched generating card sizing (1:1 -> 720x720, 3:4 -> 560x720, 16:9 -> 1120x616)
        var turn1x1 = new ChatTurnItem(Guid.NewGuid(), "test", 1024, 1024, false, DateTimeOffset.Now);
        var turn3x4 = new ChatTurnItem(Guid.NewGuid(), "test", 896, 1152, false, DateTimeOffset.Now);
        var turn16x9 = new ChatTurnItem(Guid.NewGuid(), "test", 1280, 704, false, DateTimeOffset.Now);
        Require(Math.Abs(turn1x1.GeneratingCardWidth - 720) < 1 && Math.Abs(turn1x1.GeneratingCardHeight - 720) < 1
            && Math.Abs(turn3x4.GeneratingCardWidth - 560) < 1 && Math.Abs(turn3x4.GeneratingCardHeight - 720) < 1
            && Math.Abs(turn16x9.GeneratingCardWidth - 1120) < 1 && Math.Abs(turn16x9.GeneratingCardHeight - 616) < 1,
            "Generating card matches exact displayed dimensions for 1:1 (720x720), 3:4 (560x720), and 16:9 (1120x616)", report);

        window.ToggleSidebar();
        await PumpAsync(window);
        Require(window.IsSidebarCollapsed, "Sidebar collapses cleanly when clicking toggle icon next to logo", report);
        ThemeService.Instance.SetTheme(AppTheme.Dark, persist: false);
        await CaptureAsync(window, vm, output, report, "dark-sidebar-collapsed-1440x900", 1440, 900);
        window.ToggleSidebar();
        await PumpAsync(window);
        Require(!window.IsSidebarCollapsed, "Sidebar expands cleanly back to full navigation", report);

        // Exercise Context-Aware Lightbox Prev/Next Navigation (Chat images scope vs Favorites images scope)
        var chatImages = vm.ActiveChatThread?.Turns.SelectMany(t => t.Images).ToList() ?? [];
        Require(chatImages.Count >= 2, "Active chat thread has multiple images to verify Lightbox Prev/Next navigation", report);
        await vm.OpenImageLightboxCommand.ExecuteAsync(chatImages[0]);
        await PumpAsync(window);
        Require(vm.IsLightboxOpen && vm.HasLightboxGalleryNavigation && !vm.CanGoPreviousLightboxImage && vm.CanGoNextLightboxImage && vm.LightboxScopeBadge.Contains("Ảnh chat", StringComparison.Ordinal),
            "Opening a chat image in Lightbox enables Chat-scoped Prev/Next arrows at index 1/N", report);
        ThemeService.Instance.SetTheme(AppTheme.Dark, persist: false);
        await CaptureAsync(window, vm, output, report, "dark-chat-lightbox-nav-1440x900", 1440, 900);
        await vm.NextLightboxImageCommand.ExecuteAsync(null);
        await PumpAsync(window);
        Require(vm.LightboxItem == chatImages[1] && vm.CanGoPreviousLightboxImage,
            "NextLightboxImageCommand advances cleanly to the next image in the chat", report);
        await vm.PreviousLightboxImageCommand.ExecuteAsync(null);
        await PumpAsync(window);
        Require(vm.LightboxItem == chatImages[0] && !vm.CanGoPreviousLightboxImage,
            "PreviousLightboxImageCommand steps back cleanly to the previous image in the chat", report);
        vm.CloseLightboxCommand.Execute(null);
        await PumpAsync(window);

        // Verify SmoothScrollTrack recovers Visibility after an empty initial pass (Maximum=0 -> Maximum=30000)
        // AND preserves >=44px grabbable Thumb with exact 1:1 drag math on very tall chats!
        var longTrack = new CharacterConceptAI.Desktop.Components.SmoothScrollTrack
        {
            Orientation = Orientation.Vertical,
            IsDirectionReversed = true,
            Minimum = 0,
            Maximum = 0,
            ViewportSize = 600,
            Value = 0,
            Thumb = new Thumb(),
            DecreaseRepeatButton = new RepeatButton(),
            IncreaseRepeatButton = new RepeatButton()
        };
        longTrack.Measure(new Size(12, 600));
        longTrack.Arrange(new Rect(0, 0, 12, 600)); // Pass 1: Maximum=0 hides Thumb (WPF base behavior)
        longTrack.Maximum = 30000;
        longTrack.Value = 15000;
        longTrack.Measure(new Size(12, 600));
        longTrack.Arrange(new Rect(0, 0, 12, 600)); // Pass 2: Scrollable content MUST restore Thumb.Visibility == Visible!
        Require(longTrack.Thumb.Visibility == Visibility.Visible && longTrack.Thumb.RenderSize.Height >= 40.0 && Math.Abs(longTrack.ValueFromDistance(0, 556) - 30000) < 200,
            "SmoothScrollTrack restores Visible Thumb >=44px after empty initial pass and keeps exact 1:1 drag math", report);

        // Also verify real live ChatScrollViewer's OWN PART_VerticalScrollBar and SmoothScrollTrack are visibly rendered!
        var chatScroll = Descendants(window).OfType<ScrollViewer>().FirstOrDefault(sv => sv.Name == "ChatScrollViewer" && sv.IsVisible);
        if (chatScroll != null && chatScroll.ScrollableHeight > 0)
        {
            var vBar = chatScroll.Template?.FindName("PART_VerticalScrollBar", chatScroll) as ScrollBar;
            var vTrack = vBar?.Template?.FindName("PART_Track", vBar) as Track;
            var vThumb = vTrack?.Thumb;
            var diag = $"ScrollableH={chatScroll.ScrollableHeight:0.#} VP={chatScroll.ViewportHeight:0.#} | Bar={vBar != null} BarVis={vBar?.IsVisible} TrackVis={vTrack?.IsVisible} ThumbVis={vThumb?.IsVisible} ThumbSize={vThumb?.RenderSize}";
            Require(vBar is { IsVisible: true } && vTrack is { IsVisible: true } && vThumb is { IsVisible: true } && vThumb.RenderSize.Height >= 40.0,
                $"Real ChatScrollViewer PART_VerticalScrollBar, Track and Thumb are visibly rendered ({diag})", report);
        }

        // Exercise Favorites logic (toggle, sidebar highlight exclusivity, chat history retention, Favorites-scoped Lightbox Prev/Next, SetTab("0") exit)
        vm.NavigateTo(0);
        await PumpAsync(window);
        var favImg = chatImages[0];
        var favImg2 = chatImages[1];
        {
            var initialThreadsCount = vm.ChatThreads.Count;
            vm.ToggleFavoriteImageCommand.Execute(favImg);
            vm.ToggleFavoriteImageCommand.Execute(favImg2);
            await PumpAsync(window);
            Require(vm.HasFavoriteImages && vm.FavoriteImages.Count == 2, "Favoriting images populates FavoriteImages immediately", report);

            await vm.ToggleFavoritesModeCommand.ExecuteAsync(null);
            await PumpAsync(window);
            Require(vm.ShowFavoritesOnly && vm.IsFavoritesTabSelected && !vm.IsStudioChatTabSelected,
                "Favorites mode highlights Favorites exclusively (never both Tạo ảnh AI and Yêu thích)", report);
            Require(vm.FilteredChatThreads.Count == initialThreadsCount,
                "Opening Favorites preserves normal chat history in the sidebar", report);

            await vm.OpenImageLightboxCommand.ExecuteAsync(vm.FavoriteImages[0]);
            await PumpAsync(window);
            Require(vm.IsLightboxOpen && vm.HasLightboxGalleryNavigation && vm.LightboxScopeBadge.Contains("Yêu thích", StringComparison.Ordinal) && vm.CanGoNextLightboxImage,
                "Opening a Favorites image in Lightbox scopes Prev/Next arrows strictly to Favorites", report);
            await vm.NextLightboxImageCommand.ExecuteAsync(null);
            await PumpAsync(window);
            Require(vm.LightboxItem == vm.FavoriteImages[1] && vm.CanGoPreviousLightboxImage,
                "Favorites Lightbox Next arrow navigates cleanly to the next favorite image", report);
            vm.CloseLightboxCommand.Execute(null);
            await PumpAsync(window);

            ThemeService.Instance.SetTheme(AppTheme.Dark, persist: false);
            await CaptureAsync(window, vm, output, report, "dark-favorites-1440x900", 1440, 900);
            ThemeService.Instance.SetTheme(AppTheme.Light, persist: false);
            await CaptureAsync(window, vm, output, report, "light-favorites-1440x900", 1440, 900);

            // Navigating to Settings exits Favorites; clicking Favorites from Settings returns directly to Favorites
            vm.SetTabCommand.Execute("5");
            await PumpAsync(window);
            Require(vm.CurrentTabIndex == 5 && !vm.ShowFavoritesOnly, "Switching away from Studio automatically exits Favorites mode", report);
            await vm.ToggleFavoritesModeCommand.ExecuteAsync(null);
            await PumpAsync(window);
            Require(vm.CurrentTabIndex == 0 && vm.ShowFavoritesOnly, "Clicking Favorites from another tab navigates directly to Favorites", report);

            // Clicking Tạo ảnh AI (SetTab 0) exits Favorites cleanly back to Studio chat
            vm.SetTabCommand.Execute("0");
            await PumpAsync(window);
            Require(vm.CurrentTabIndex == 0 && !vm.ShowFavoritesOnly && vm.IsStudioChatTabSelected,
                "Clicking Tạo ảnh AI exits Favorites back to Studio chat cleanly", report);
        }

        // Exercise Master NSFW Child-Safety Switch (default OFF hides Mở khóa 18+ everywhere)
        Require(!vm.AllowNsfwContent, "NSFW content master switch defaults to OFF for child/family safety", report);
        await vm.OpenPoseGalleryForStudioCommand.ExecuteAsync(null);
        await PumpAsync(window);
        var poseNsfwCheck = Descendants(window).OfType<CheckBox>().Single(c => AutomationProperties.GetName(c) == "Mở khóa 18+ kho dáng");
        Require(!poseNsfwCheck.IsVisible, "Pose Gallery hides Mở khóa 18+ completely when AllowNsfwContent is OFF", report);
        vm.PoseGalleryIncludeNsfw = true;
        Require(!vm.PoseGalleryIncludeNsfw, "PoseGalleryIncludeNsfw cannot be enabled while AllowNsfwContent is OFF", report);
        ThemeService.Instance.SetTheme(AppTheme.Dark, persist: false);
        await CaptureAsync(window, vm, output, report, "dark-pose-gallery-safe-1440x900", 1440, 900);
        vm.ClosePoseGalleryCommand.Execute(null);
        await PumpAsync(window);

        await vm.OpenOutfitGalleryForStudioCommand.ExecuteAsync(null);
        await PumpAsync(window);
        var outfitNsfwCheck = Descendants(window).OfType<CheckBox>().Single(c => AutomationProperties.GetName(c) == "Mở khóa 18+ kho trang phục");
        Require(!outfitNsfwCheck.IsVisible, "Outfit Gallery hides Mở khóa 18+ completely when AllowNsfwContent is OFF", report);
        vm.OutfitGalleryIncludeNsfw = true;
        Require(!vm.OutfitGalleryIncludeNsfw, "OutfitGalleryIncludeNsfw cannot be enabled while AllowNsfwContent is OFF", report);

        vm.AllowNsfwContent = true;
        await PumpAsync(window);
        Require(outfitNsfwCheck.IsVisible, "Outfit Gallery reveals Mở khóa 18+ when AllowNsfwContent is turned ON in Settings", report);
        vm.OutfitGalleryIncludeNsfw = true;
        await PumpAsync(window);
        vm.AllowNsfwContent = false;
        await PumpAsync(window);
        Require(!outfitNsfwCheck.IsVisible && !vm.OutfitGalleryIncludeNsfw && !vm.PoseGalleryIncludeNsfw,
            "Turning AllowNsfwContent OFF immediately hides Mở khóa 18+ and resets NSFW flags", report);
        vm.CloseOutfitGalleryCommand.Execute(null);
        await PumpAsync(window);

        // Exercise Settings Font Size Increase / Decrease & Presets
        vm.SetTabCommand.Execute("5");
        await PumpAsync(window);
        Require(Math.Abs(vm.UiFontScale - 1.0) < 0.001, "Font scale starts at 100%", report);
        vm.IncreaseFontSizeCommand.Execute(null);
        Require(Math.Abs(vm.UiFontScale - 1.05) < 0.001, "IncreaseFontSizeCommand increments font scale by 5%", report);
        vm.DecreaseFontSizeCommand.Execute(null);
        Require(Math.Abs(vm.UiFontScale - 1.0) < 0.001, "DecreaseFontSizeCommand decrements font scale by 5%", report);
        vm.SetFontScalePresetCommand.Execute("1.25");
        await PumpAsync(window);
        Require(Math.Abs(vm.UiFontScale - 1.25) < 0.001 && (double)Application.Current.Resources["FontSizeBody"] > 17.0,
            "Font scale preset 125% dynamically scales global Typography resources", report);
        ThemeService.Instance.SetTheme(AppTheme.Dark, persist: false);
        await CaptureAsync(window, vm, output, report, "dark-settings-font125-1440x900", 1440, 900);
        vm.ResetFontSizeCommand.Execute(null);
        await PumpAsync(window);
        ThemeService.Instance.SetTheme(AppTheme.Dark, persist: false);
        await CaptureAsync(window, vm, output, report, "dark-settings-1440x900", 1440, 900);
        ThemeService.Instance.SetTheme(AppTheme.Light, persist: false);
        await CaptureAsync(window, vm, output, report, "light-settings-1440x900", 1440, 900);

        // Verify no legacy "Đóng (Esc)" text buttons remain anywhere in the visual tree
        var legacyCloseButtons = Descendants(window).OfType<Button>().Where(b => b.Content is string s && s.Contains("Đóng (Esc)", StringComparison.OrdinalIgnoreCase)).ToList();
        Require(legacyCloseButtons.Count == 0, "All legacy 'Đóng (Esc)' text buttons are replaced by X vector icon buttons", report);

        vm.NavigateTo(6); vm.OpenCreateProjectDialogCommand.Execute(null);
        await CaptureAsync(window, vm, output, report, "light-project-dialog", 1440, 900);
        vm.CloseCreateProjectDialogCommand.Execute(null);
        vm.IsBusy = true;
        await CaptureAsync(window, vm, output, report, "light-projects-busy", 1440, 900);
        vm.IsBusy = false;

        handler.Offline = true;
        await vm.RefreshProjectsCommand.ExecuteAsync(null);
        Require(vm.ApiConnectionStatus == "Mất kết nối", "HTTP offline failure maps to persistent connection state", report);
        await CaptureAsync(window, vm, output, report, "light-offline", 1440, 900);
        handler.Offline = false;
        await vm.RefreshProjectsCommand.ExecuteAsync(null);

        handler.Forbidden = true;
        await vm.RefreshQueueCommand.ExecuteAsync(null);
        await PumpAsync(window);
        Require(vm.SelectedProject is null && !vm.CanCreateInProject && !vm.IsProjectOwner,
            "Fixture 403 invalidates cached project scope and editing permissions", report);
        await CaptureAsync(window, vm, output, report, "light-forbidden", 1440, 900);
        handler.Forbidden = false;

        vm.NavigateTo(8);
        await PumpAsync(window);
        var textBox = Descendants(window).OfType<TextBox>().FirstOrDefault(t => t.IsVisible && t.IsEnabled && t.Focusable);
        if (textBox is not null)
        {
            textBox.Focus();
            await PumpAsync(window);
            report.Checks.Add($"Programmatic input focus exercised: {textBox.IsKeyboardFocused}; manual keyboard traversal remains required.");
        }
        var grids = Descendants(window).OfType<DataGrid>().ToArray();
        Require(grids.Length > 0 && grids.All(g => g.EnableRowVirtualization && g.EnableColumnVirtualization), "DataGrid controls enable row/column virtualization", report);
        // Verify live 3-language hot-swap (Vietnamese -> English -> Simplified Chinese -> Vietnamese) with 0 binding warnings
        ThemeService.Instance.SetTheme(AppTheme.Dark, persist: false);
        vm.NavigateTo(0);
        vm.SetLanguageCommand.Execute("en");
        await PumpAsync(window);
        Require(vm.CurrentLanguageShortBadge == "EN" && vm.IsLanguageEn, "Live language switch to English updates badges & dictionary", report);
        await CaptureAsync(window, vm, output, report, "dark-studio-en-1440x900", 1440, 900);
        vm.SetLanguageCommand.Execute("zh");
        await PumpAsync(window);
        Require(vm.CurrentLanguageShortBadge == "中文" && vm.IsLanguageZh, "Live language switch to Simplified Chinese updates badges & dictionary", report);
        await CaptureAsync(window, vm, output, report, "dark-studio-zh-1440x900", 1440, 900);
        vm.SetLanguageCommand.Execute("vi");
        await PumpAsync(window);
        Require(vm.CurrentLanguageShortBadge == "VI" && vm.IsLanguageVi, "Live language switch back to Vietnamese succeeds", report);

        // Verify independent FavoritesStore persistence, backup recovery, and survival across chat thread deletion
        var favTestDir = Path.Combine(Path.GetTempPath(), $"gula_fav_test_{Guid.NewGuid():N}");
        try
        {
            var imgA = Path.Combine(favTestDir, "abc123.png");
            var imgAInpaint = Path.Combine(favTestDir, "abc123_inpaint.png");
            var aidA = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            var k1 = FavoritesStore.BuildKey(imgA, aidA);
            var k2 = FavoritesStore.BuildKey(imgAInpaint, aidA);
            Require(!string.Equals(k1, k2, StringComparison.OrdinalIgnoreCase),
                "FavoritesStore distinguishes original and _inpaint image keys even with same AssetId", report);

            FavoritesStore.Save(
            [
                new FavoriteImageRecord(k1, aidA, 42, 896, 1152, "Portrait A", imgA, null, false, now, now),
                new FavoriteImageRecord(k1.ToUpperInvariant(), aidA, 42, 896, 1152, "Portrait A dup", imgA, null, false, now, now.AddSeconds(5)),
                new FavoriteImageRecord(k2, aidA, 42, 896, 1152, "Portrait A inpaint", imgAInpaint, null, false, now, now.AddSeconds(10))
            ], favTestDir);
            var loadedFavs = FavoritesStore.Load(favTestDir);
            Require(loadedFavs.Count == 2 && loadedFavs[0].PromptSnapshot == "Portrait A inpaint" && loadedFavs[1].PromptSnapshot == "Portrait A dup",
                "FavoritesStore deduplicates case-insensitively and preserves newest FavoritedAt order", report);

            // Corrupt main file and verify automatic recovery from .bak (after saving a second revision to populate .bak)
            FavoritesStore.Save(loadedFavs, favTestDir);
            File.WriteAllText(FavoritesStore.GetFavoritesFilePath(favTestDir), "{corrupt json");
            var recoveredFavs = FavoritesStore.Load(favTestDir);
            Require(recoveredFavs.Count == 2, "FavoritesStore automatically recovers from .bak when main favorites.json is corrupt", report);

            // Verify MainViewModel keeps favorited image alive even after deleting its parent chat thread
            var testImg = new ChatTurnImageItem(Guid.NewGuid(), 777, 1024, 1024, "Smoke Favorite Preservation", now, true)
            {
                LocalImagePath = imgA
            };
            var testTurn = new ChatTurnItem(Guid.NewGuid(), "Smoke Favorite Preservation", 1024, 1024, true, now);
            testTurn.Images.Add(testImg);
            var testThread = new ChatThreadItem(Guid.NewGuid(), "Smoke Favorite Thread", now, now);
            testThread.Turns.Add(testTurn);
            vm.ChatThreads.Insert(0, testThread);

            var beforeFavCount = vm.FavoriteImages.Count;
            vm.ToggleFavoriteImageCommand.Execute(testImg);
            Require(testImg.IsFavorite && vm.FavoriteImages.Count == beforeFavCount + 1,
                "ToggleFavoriteImageCommand adds image to FavoriteImages", report);

            vm.DeleteChatThreadCommand.Execute(testThread);
            Require(vm.FavoriteImages.Count == beforeFavCount + 1 && vm.FavoriteImages.Any(f => f.PromptSnapshot == "Smoke Favorite Preservation"),
                "Deleting a chat thread preserves its favorited images as standalone favorites", report);

            var preservedFav = vm.FavoriteImages.First(f => f.PromptSnapshot == "Smoke Favorite Preservation");
            vm.ToggleFavoriteImageCommand.Execute(preservedFav);
            Require(vm.FavoriteImages.Count == beforeFavCount,
                "Unfavoriting a standalone favorite cleanly removes it from FavoriteImages", report);
        }
        finally
        {
            try { if (Directory.Exists(favTestDir)) Directory.Delete(favTestDir, recursive: true); } catch { }
        }

        report.Checks.Add("No desktop preference file was changed by capture theme/language switches (persist:false).");
        report.Limitations.Add("No real SQL, GPU workflow, two-PC LAN, native titlebar, screen reader, physical DPI switch, or human UX acceptance is claimed.");
        report.Limitations.Add("Renders use 96-DPI target bitmaps from the real WPF visual tree. Physical 125%/150% monitor testing remains required.");
        report.Limitations.Add("Dark textMuted token measures 4.29:1 on bgSelected; selected data should use primary/secondary foreground roles.");
    }

    private static async Task CaptureAsync(MainWindow window, MainViewModel vm, string output, SmokeReport report, string name, int width, int height)
    {
        window.Width = width; window.Height = height;
        await PumpAsync(window);
        var content = (FrameworkElement)window.Content;
        // Render the client viewport only. This is the actual app visual tree, not an OS screenshot.
        content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        // Window.Background is behind the client root and is otherwise omitted when
        // rendering Content directly. Composite that genuine app brush first.
        var backdrop = new DrawingVisual();
        using (var drawing = backdrop.RenderOpen())
            drawing.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
        bitmap.Render(backdrop);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var fileName = name + ".png";
        using (var stream = File.Create(Path.Combine(output, fileName))) encoder.Save(stream);
        bitmap.Clear();
        bitmap.Freeze();
        report.Captures.Add(new(name, fileName, width, height, vm.CurrentTabIndex, vm.IsLoggedIn, ThemeService.Instance.CurrentTheme.ToString()));
        if (report.Captures.Count % 6 == 0)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    private static async Task PumpAsync(Window window)
    {
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        await Task.Delay(35);
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static BitmapImage CreateSyntheticBitmap(int width, int height)
    {
        var pixels = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 3;
                pixels[i] = (byte)(60 + (x * 120 / width));
                pixels[i + 1] = (byte)(80 + (y * 110 / height));
                pixels[i + 2] = 175;
            }
        var src = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null, pixels, width * 3);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        enc.Save(ms);
        ms.Position = 0;
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private static void Require(bool condition, string check, SmokeReport report)
    {
        if (!condition) throw new InvalidOperationException("SMOKE ASSERTION FAILED: " + check);
        report.Checks.Add(check);
    }

    private sealed class BindingListener : TraceListener
    {
        private readonly StringBuilder _buffer = new();
        internal List<string> Messages { get; } = [];
        public override void Write(string? message) { lock (_buffer) _buffer.Append(message); }
        public override void WriteLine(string? message)
        {
            lock (_buffer)
            {
                _buffer.Append(message);
                var text = _buffer.ToString(); _buffer.Clear();
                if (!string.IsNullOrWhiteSpace(text)) Messages.Add(text);
            }
        }
    }

    private sealed class SmokeReport
    {
        public string Result { get; set; } = "INCOMPLETE";
        public string DataProvenance { get; } = "SIMULATED synthetic API fixtures; no live server/SQL/GPU";
        public string RenderProvenance { get; } = "Actual WPF MainWindow visual tree via RenderTargetBitmap, 96 DPI, software rendering; not an OS screenshot";
        public DateTimeOffset ExecutedAtUtc { get; } = DateTimeOffset.UtcNow;
        public List<string> Checks { get; } = [];
        public List<Capture> Captures { get; } = [];
        public List<string> BindingErrors { get; } = [];
        public List<string> Failures { get; } = [];
        public List<string> Requests { get; } = [];
        public List<string> UnhandledRequests { get; } = [];
        public List<string> Limitations { get; } = [];
    }

    private sealed record Capture(string Name, string File, int Width, int Height, int Route, bool IsLoggedIn, string Theme);
}

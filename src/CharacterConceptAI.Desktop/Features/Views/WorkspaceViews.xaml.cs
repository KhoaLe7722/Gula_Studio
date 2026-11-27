using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CharacterConceptAI.Desktop.ViewModels;
using CharacterConceptAI.Shared.Contracts.Projects;

namespace CharacterConceptAI.Desktop.Features.Views;

// These handlers concern keyboard focus, pointer selection and PasswordBox transport only.
// All domain mutations and network access stay in the ViewModel and its services.
internal static class WorkspaceInteraction
{
    public static void SelectContextItem(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox list && e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(list, source) is ListBoxItem item)
        {
            item.IsSelected = true;
            item.Focus();
        }
        else if (sender is DataGrid table && e.OriginalSource is DependencyObject gridSource &&
                 ItemsControl.ContainerFromElement(table, gridSource) is DataGridRow row)
        {
            table.SelectedItem = row.Item;
            row.Focus();
        }
    }

    public static void OpenProject(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ItemsControl list || e.OriginalSource is not DependencyObject source ||
            ItemsControl.ContainerFromElement(list, source) is not (ListBoxItem or DataGridRow)) return;
        if (list.DataContext is MainViewModel vm && vm.SelectedProject is ProjectDto project && vm.OpenProjectCommand.CanExecute(project))
            vm.OpenProjectCommand.Execute(project);
    }

    public static void ProjectKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not ItemsControl list || list.DataContext is not MainViewModel vm) return;
        if (vm.SelectedProject is { } project && vm.OpenProjectCommand.CanExecute(project))
        {
            vm.OpenProjectCommand.Execute(project); e.Handled = true;
        }
    }

    public static void PrepareProjectContext(object sender, MouseButtonEventArgs e)
    {
        SelectContextItem(sender, e);
        if (sender is FrameworkElement target && target.DataContext is MainViewModel vm && vm.SelectedProject is { } project)
            target.ContextMenu = CreateProjectMenu(vm, project);
    }

    public static void ShowProjectMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MainViewModel vm, DataContext: ProjectDto project } button) return;
        vm.SelectedProject = project;
        var menu = CreateProjectMenu(vm, project);
        menu.PlacementTarget = button;
        menu.IsOpen = true;
        e.Handled = true;
    }

    // Both right-click and ellipsis construct precisely the same permission-aware commands.
    private static ContextMenu CreateProjectMenu(MainViewModel vm, ProjectDto project)
    {
        var menu = new ContextMenu { DataContext = vm };
        menu.Items.Add(new MenuItem { Header = "Mở dự án", Command = vm.OpenProjectCommand, CommandParameter = project });
        menu.Items.Add(new MenuItem { Header = "Chỉnh sửa dự án", Command = vm.OpenEditProjectCommand });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Lưu trữ dự án", Command = vm.ArchiveProjectCommand });
        menu.Items.Add(new MenuItem { Header = "Khôi phục dự án", Command = vm.RestoreProjectCommand });
        return menu;
    }
}

public partial class ProjectsView : UserControl
{
    public ProjectsView() => InitializeComponent();
    private void Projects_Open(object sender, MouseButtonEventArgs e) => WorkspaceInteraction.OpenProject(sender, e);
    private void Projects_KeyDown(object sender, KeyEventArgs e) => WorkspaceInteraction.ProjectKey(sender, e);
    private void SelectContextItem(object sender, MouseButtonEventArgs e) => WorkspaceInteraction.PrepareProjectContext(sender, e);
    private void ProjectMenu_Click(object sender, RoutedEventArgs e) => WorkspaceInteraction.ShowProjectMenu(sender, e);
}
public partial class RecentsView : UserControl
{
    public RecentsView() => InitializeComponent();
    private void Projects_Open(object sender, MouseButtonEventArgs e) => WorkspaceInteraction.OpenProject(sender, e);
    private void Projects_KeyDown(object sender, KeyEventArgs e) => WorkspaceInteraction.ProjectKey(sender, e);
    private void SelectContextItem(object sender, MouseButtonEventArgs e) => WorkspaceInteraction.PrepareProjectContext(sender, e);
    private void ProjectMenu_Click(object sender, RoutedEventArgs e) => WorkspaceInteraction.ShowProjectMenu(sender, e);
}
public partial class OverviewView : UserControl { public OverviewView() => InitializeComponent(); }
public partial class CharactersView : UserControl
{
    public CharactersView() => InitializeComponent();
    private void SelectContextItem(object sender, MouseButtonEventArgs e) => WorkspaceInteraction.SelectContextItem(sender, e);
}
public partial class StudioView : UserControl
{
    private MainViewModel? _boundVm;

    public StudioView()
    {
        InitializeComponent();
        DataContextChanged += OnStudioDataContextChanged;
        Unloaded += (_, _) => DetachVm();
    }

    private void OnStudioDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        DetachVm();
        if (e.NewValue is MainViewModel vm)
        {
            _boundVm = vm;
            _boundVm.ChatScrollRequested += ScrollChatToBottom;
            _boundVm.PromptFocusRequested += FocusPromptAtEnd;
            _boundVm.MaskPngExporter = () => MagicBrushCanvas?.ExportMaskPngBytes();
            _boundVm.MaskClearAction = () => MagicBrushCanvas?.ClearMask();
        }
    }

    private void DetachVm()
    {
        if (_boundVm != null)
        {
            _boundVm.ChatScrollRequested -= ScrollChatToBottom;
            _boundVm.PromptFocusRequested -= FocusPromptAtEnd;
            _boundVm.MaskPngExporter = null;
            _boundVm.MaskClearAction = null;
            _boundVm = null;
        }
    }

    private void ScrollChatToBottom()
    {
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, () => ChatScrollViewer?.ScrollToEnd());
    }

    private void FocusPromptAtEnd()
    {
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            if (PromptInput == null) return;
            PromptInput.Focus();
            PromptInput.CaretIndex = PromptInput.Text?.Length ?? 0;
        });
    }

    private void ChatBubbleTextBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (ChatScrollViewer == null || e.Handled) return;
        e.Handled = true;
        var forwarded = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = sender
        };
        ChatScrollViewer.RaiseEvent(forwarded);
    }

    private void PromptInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control &&
            DataContext is MainViewModel vm && vm.GenerateCommand.CanExecute(null))
        {
            vm.GenerateCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void LightboxBackdrop_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == sender && DataContext is MainViewModel vm && vm.IsLightboxOpen)
        {
            vm.CloseLightboxCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void Composer_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            var first = files?.FirstOrDefault();
            var ext = first is null ? string.Empty : System.IO.Path.GetExtension(first).ToLowerInvariant();
            e.Effects = ext is ".png" or ".jpg" or ".jpeg" ? DragDropEffects.Copy : DragDropEffects.None;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private async void Composer_Drop(object sender, DragEventArgs e)
    {
        if (DataContext is not MainViewModel vm || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = e.Data.GetData(DataFormats.FileDrop) as string[];
        var first = files?.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(first))
        {
            e.Handled = true;
            try { await vm.AttachLocalReferenceFromFileAsync(first); }
            catch (Exception ex) { vm.StatusMessage = $"Không thể đính kèm ảnh: {ex.Message}"; }
        }
    }
}
public partial class ConceptsView : UserControl { public ConceptsView() => InitializeComponent(); }
public partial class ScriptsView : UserControl { public ScriptsView() => InitializeComponent(); }
public partial class ProjectTeamView : UserControl { public ProjectTeamView() => InitializeComponent(); }
public partial class JobsView : UserControl { public JobsView() => InitializeComponent(); }
public partial class SettingsView : UserControl { public SettingsView() => InitializeComponent(); }
public partial class StoryStudioView : UserControl
{
    public StoryStudioView() => InitializeComponent();

    private void StoryField_LostFocus(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.SaveLocalStories();
    }

    private void StoryCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { IsLoaded: true, IsDropDownOpen: true } && DataContext is MainViewModel vm)
            vm.SaveLocalStories();
    }

    private void StoryCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.SaveLocalStories();
    }

    private void StoryLightboxBackdrop_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == sender && DataContext is MainViewModel vm && vm.IsStoryLightboxOpen)
        {
            vm.CloseStoryLightboxCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void StoryMultiLineTextBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not TextBox tb || e.Handled) return;
        // If the inner TextBox does not need to scroll further in this direction, bubble wheel smoothly to parent ScrollViewer.
        var atTop = e.Delta > 0 && tb.VerticalOffset <= 0.5;
        var atBottom = e.Delta < 0 && tb.VerticalOffset >= tb.ExtentHeight - tb.ViewportHeight - 0.5;
        var noScroll = tb.ExtentHeight <= tb.ViewportHeight + 1.0;
        if (!noScroll && !atTop && !atBottom) return;

        var parent = System.Windows.Media.VisualTreeHelper.GetParent(tb);
        while (parent != null && parent is not ScrollViewer)
            parent = System.Windows.Media.VisualTreeHelper.GetParent(parent);

        if (parent is ScrollViewer sv)
        {
            e.Handled = true;
            sv.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = sender
            });
        }
    }
}
public partial class PoseGalleryOverlay : UserControl
{
    private MainViewModel? _boundVm;

    public PoseGalleryOverlay()
    {
        InitializeComponent();
        DataContextChanged += OnPoseDataContextChanged;
        Unloaded += (_, _) => DetachVm();
    }

    private void OnPoseDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        DetachVm();
        if (e.NewValue is MainViewModel vm)
        {
            _boundVm = vm;
            _boundVm.PoseGalleryScrollTopRequested += ScrollPoseListToTop;
        }
    }

    private void DetachVm()
    {
        if (_boundVm != null)
        {
            _boundVm.PoseGalleryScrollTopRequested -= ScrollPoseListToTop;
            _boundVm = null;
        }
    }

    private void ScrollPoseListToTop()
    {
        Dispatcher.BeginInvoke(() => PoseCardsScrollViewer?.ScrollToTop());
    }

    private void PoseGalleryBackdrop_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == sender && DataContext is MainViewModel vm && vm.IsPoseGalleryOpen)
        {
            vm.ClosePoseGalleryCommand.Execute(null);
            e.Handled = true;
        }
    }
}
public partial class OutfitGalleryOverlay : UserControl
{
    private MainViewModel? _boundVm;

    public OutfitGalleryOverlay()
    {
        InitializeComponent();
        DataContextChanged += OnOutfitDataContextChanged;
        Unloaded += (_, _) => DetachVm();
    }

    private void OnOutfitDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        DetachVm();
        if (e.NewValue is MainViewModel vm)
        {
            _boundVm = vm;
            _boundVm.OutfitGalleryScrollTopRequested += ScrollOutfitListToTop;
        }
    }

    private void DetachVm()
    {
        if (_boundVm != null)
        {
            _boundVm.OutfitGalleryScrollTopRequested -= ScrollOutfitListToTop;
            _boundVm = null;
        }
    }

    private void ScrollOutfitListToTop()
    {
        Dispatcher.BeginInvoke(() => OutfitCardsScrollViewer?.ScrollToTop());
    }

    private void OutfitGalleryBackdrop_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == sender && DataContext is MainViewModel vm && vm.IsOutfitGalleryOpen)
        {
            vm.CloseOutfitGalleryCommand.Execute(null);
            e.Handled = true;
        }
    }
}
public partial class ComponentGalleryView : UserControl { public ComponentGalleryView() => InitializeComponent(); }
public partial class AdminView : UserControl
{
    public AdminView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => { if (!IsVisible) { NewAccountPassword.Clear(); ResetPasswordInput.Clear(); } };
    }
    private async void CreateAccount_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || !vm.CreateAccountCommand.CanExecute(null)) return;
        vm.AccountPassword = NewAccountPassword.Password;
        try { await vm.CreateAccountCommand.ExecuteAsync(null); }
        catch (Exception ex) { vm.StatusMessage = $"Lỗi tạo tài khoản: {ex.Message}"; }
        finally { vm.AccountPassword = string.Empty; NewAccountPassword.Clear(); }
    }
    private async void ResetAccountPassword_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || !vm.ResetAccountPasswordCommand.CanExecute(null)) return;
        vm.ResetAccountPassword = ResetPasswordInput.Password;
        try { await vm.ResetAccountPasswordCommand.ExecuteAsync(null); }
        catch (Exception ex) { vm.StatusMessage = $"Lỗi đặt lại mật khẩu: {ex.Message}"; }
        finally { vm.ResetAccountPassword = string.Empty; ResetPasswordInput.Clear(); }
    }
}

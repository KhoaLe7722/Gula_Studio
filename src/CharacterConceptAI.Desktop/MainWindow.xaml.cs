using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using CharacterConceptAI.Desktop.ViewModels;
using CharacterConceptAI.Desktop.Features.Views;

namespace CharacterConceptAI.Desktop;

public partial class MainWindow : Window
{
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;
    private const int WmGetMinMaxInfo = 0x0024;
    private const uint MonitorDefaultToNearest = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left; public int top; public int right; public int bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private double _workspaceZoom = 1.0;
    private bool _isFullScreen;
    private WindowState _preFullScreenState = WindowState.Normal;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.PropertyChanged += ViewModel_PropertyChanged;
        Loaded += (_, _) =>
        {
            ApplyResponsiveNavigation(force: true);
            UpdateWindowChromeState();
        };
        Closing += (_, e) =>
        {
            if (Keyboard.FocusedElement is TextBox focusedTextBox)
                focusedTextBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            if (!vm.ConfirmDiscardChanges())
            {
                e.Cancel = true;
                return;
            }
            vm.FlushAllLocalStores();
        };
        Closed += (_, _) =>
        {
            vm.PropertyChanged -= ViewModel_PropertyChanged;
            vm.DisposeAllResources();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
                var preference = DwmwcpRound;
                _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref preference, sizeof(int));
            }
        }
        catch
        {
            // Ignore on older Windows versions without DWM rounded corners.
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmGetMinMaxInfo)
        {
            ApplyWorkAreaMaximizeBounds(hwnd, lParam);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void ApplyWorkAreaMaximizeBounds(IntPtr hwnd, IntPtr lParam)
    {
        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor != IntPtr.Zero)
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                var rcMonitor = info.rcMonitor;
                var rcBounds = _isFullScreen ? info.rcMonitor : info.rcWork;
                mmi.ptMaxPosition.x = rcBounds.left - rcMonitor.left;
                mmi.ptMaxPosition.y = rcBounds.top - rcMonitor.top;
                mmi.ptMaxSize.x = rcBounds.right - rcBounds.left;
                mmi.ptMaxSize.y = rcBounds.bottom - rcBounds.top;
            }
        }
        var dpi = VisualTreeHelper.GetDpi(this);
        mmi.ptMinTrackSize.x = (int)Math.Round(MinWidth * dpi.DpiScaleX);
        mmi.ptMinTrackSize.y = (int)Math.Round(MinHeight * dpi.DpiScaleY);
        Marshal.StructureToPtr(mmi, lParam, true);
    }

    private void Window_StateChanged(object? sender, EventArgs e) => UpdateWindowChromeState();

    private void UpdateWindowChromeState()
    {
        if (RootWindowHost == null || MaximizeGlyph == null || RestoreGlyph == null) return;
        var isMax = WindowState == WindowState.Maximized;
        // WM_GETMINMAXINFO clamps exact maximized bounds to rcWork (above Taskbar), so zero outer margin is needed.
        RootWindowHost.Margin = new Thickness(0);
        MaximizeGlyph.Visibility = isMax ? Visibility.Collapsed : Visibility.Visible;
        RestoreGlyph.Visibility = isMax ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e) =>
        SystemCommands.MinimizeWindow(this);

    private void MaximizeRestoreWindow_Click(object sender, RoutedEventArgs e)
    {
        if (_isFullScreen)
        {
            ToggleFullScreen();
            return;
        }
        if (WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(this);
        else
            SystemCommands.MaximizeWindow(this);
    }

    private void CloseWindow_Click(object sender, RoutedEventArgs e) =>
        SystemCommands.CloseWindow(this);

    // ─── Menu Bar: Edit (Chỉnh sửa) ───
    private void ExecuteEditCommand(RoutedUICommand command, string fallbackStatus)
    {
        // Menu has its own focus scope, so check Window's focused element first before Keyboard.FocusedElement.
        IInputElement?[] candidates = [FocusManager.GetFocusedElement(this), Keyboard.FocusedElement];
        foreach (var target in candidates)
        {
            if (target != null && command.CanExecute(null, target))
            {
                command.Execute(null, target);
                return;
            }
        }
        if (DataContext is MainViewModel vm)
            vm.StatusMessage = fallbackStatus;
    }

    private void MenuEditUndo_Click(object sender, RoutedEventArgs e) =>
        ExecuteEditCommand(ApplicationCommands.Undo, "Đặt con trỏ vào ô nhập liệu để Hoàn tác (Ctrl+Z).");

    private void MenuEditRedo_Click(object sender, RoutedEventArgs e) =>
        ExecuteEditCommand(ApplicationCommands.Redo, "Đặt con trỏ vào ô nhập liệu để Làm lại (Ctrl+Y).");

    private void MenuEditCut_Click(object sender, RoutedEventArgs e) =>
        ExecuteEditCommand(ApplicationCommands.Cut, "Chọn đoạn văn bản cần cắt (Ctrl+X).");

    private void MenuEditCopy_Click(object sender, RoutedEventArgs e) =>
        ExecuteEditCommand(ApplicationCommands.Copy, "Chọn đoạn văn bản cần sao chép (Ctrl+C).");

    private void MenuEditPaste_Click(object sender, RoutedEventArgs e) =>
        ExecuteEditCommand(ApplicationCommands.Paste, "Đặt con trỏ vào ô nhập liệu để dán (Ctrl+V).");

    private void MenuEditSelectAll_Click(object sender, RoutedEventArgs e) =>
        ExecuteEditCommand(ApplicationCommands.SelectAll, "Đặt con trỏ vào ô nhập liệu để chọn tất cả (Ctrl+A).");

    private void MenuEditPreferences_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.SetTabCommand.CanExecute("5"))
            vm.SetTabCommand.Execute("5");
    }

    // ─── Menu Bar: View (Xem) ───
    private void SetWorkspaceZoom(double zoom)
    {
        _workspaceZoom = Math.Round(Math.Clamp(zoom, 0.80, 1.30), 2);
        if (WorkspaceZoomTransform != null)
        {
            WorkspaceZoomTransform.ScaleX = _workspaceZoom;
            WorkspaceZoomTransform.ScaleY = _workspaceZoom;
        }
        if (DataContext is MainViewModel vm)
            vm.StatusMessage = $"Tỷ lệ hiển thị giao diện: {(int)Math.Round(_workspaceZoom * 100)}%.";
    }

    private void MenuViewZoomIn_Click(object sender, RoutedEventArgs e) => SetWorkspaceZoom(_workspaceZoom + 0.10);
    private void MenuViewZoomOut_Click(object sender, RoutedEventArgs e) => SetWorkspaceZoom(_workspaceZoom - 0.10);
    private void MenuViewZoomReset_Click(object sender, RoutedEventArgs e) => SetWorkspaceZoom(1.0);

    private void ToggleFullScreen()
    {
        _isFullScreen = !_isFullScreen;
        if (MenuFullScreenItem != null) MenuFullScreenItem.IsChecked = _isFullScreen;
        if (_isFullScreen)
        {
            _preFullScreenState = WindowState;
            TitleBarRow.Height = new GridLength(0);
            TitleBarChrome.Visibility = Visibility.Collapsed;
            WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
            if (DataContext is MainViewModel vm) vm.StatusMessage = "Chế độ Toàn màn hình (nhấn F11 hoặc Esc để thoát).";
        }
        else
        {
            TitleBarRow.Height = new GridLength(34);
            TitleBarChrome.Visibility = Visibility.Visible;
            WindowState = WindowState.Normal;
            if (_preFullScreenState == WindowState.Maximized)
                WindowState = WindowState.Maximized;
            if (DataContext is MainViewModel vm) vm.StatusMessage = "Đã thoát chế độ Toàn màn hình.";
        }
        UpdateWindowChromeState();
    }

    private void MenuViewFullScreen_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

    // ─── Menu Bar: Window (Cửa sổ) ───
    private Rect GetCurrentWorkAreaDip()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var monitor = hwnd != IntPtr.Zero ? MonitorFromWindow(hwnd, MonitorDefaultToNearest) : IntPtr.Zero;
        if (monitor != IntPtr.Zero)
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                return new Rect(
                    info.rcWork.left / dpi.DpiScaleX,
                    info.rcWork.top / dpi.DpiScaleY,
                    (info.rcWork.right - info.rcWork.left) / dpi.DpiScaleX,
                    (info.rcWork.bottom - info.rcWork.top) / dpi.DpiScaleY);
            }
        }
        return SystemParameters.WorkArea;
    }

    private void SnapWindowTo(double xRatio, double wRatio)
    {
        if (_isFullScreen) ToggleFullScreen();
        WindowState = WindowState.Normal;
        var wa = GetCurrentWorkAreaDip();
        Width = Math.Min(wa.Width, Math.Max(MinWidth, wa.Width * wRatio));
        Height = wa.Height;
        Left = Math.Max(wa.Left, Math.Min(wa.Left + wa.Width * xRatio, wa.Right - Width));
        Top = wa.Top;
    }

    private void MenuWindowSnapLeft_Click(object sender, RoutedEventArgs e) => SnapWindowTo(0.0, 0.5);
    private void MenuWindowSnapRight_Click(object sender, RoutedEventArgs e) => SnapWindowTo(0.5, 0.5);

    private void MenuWindowCenterDefault_Click(object sender, RoutedEventArgs e)
    {
        if (_isFullScreen) ToggleFullScreen();
        WindowState = WindowState.Normal;
        var wa = GetCurrentWorkAreaDip();
        Width = Math.Min(1440, wa.Width - 24);
        Height = Math.Min(900, wa.Height - 24);
        Left = wa.Left + (wa.Width - Width) / 2.0;
        Top = wa.Top + (wa.Height - Height) / 2.0;
    }

    private void MenuWindowTopmost_Click(object sender, RoutedEventArgs e)
    {
        Topmost = MenuTopmostItem?.IsChecked == true;
        if (DataContext is MainViewModel vm)
            vm.StatusMessage = Topmost ? "Đã ghim cửa sổ Gula Studio luôn hiển thị trên cùng." : "Đã tắt ghim cửa sổ trên cùng.";
    }

    // ─── Menu Bar: Help (Trợ giúp) ───
    private void ShowHelpModal(string title, string body, bool showAboutLinks = false)
    {
        if (HelpModalOverlay == null || HelpModalTitle == null || HelpModalBody == null) return;
        HelpModalTitle.Text = title;
        HelpModalBody.Text = body;
        if (AboutActionLinks != null)
            AboutActionLinks.Visibility = showAboutLinks ? Visibility.Visible : Visibility.Collapsed;
        HelpModalOverlay.Visibility = Visibility.Visible;
    }

    private void OpenAuthorGitHub_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://github.com/KhoaLe7722",
                UseShellExecute = true
            });
        }
        catch
        {
            if (DataContext is MainViewModel vm)
                vm.StatusMessage = "GitHub: https://github.com/KhoaLe7722";
        }
    }

    private void OpenAuthorX_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://x.com/KhoaLe7722",
                UseShellExecute = true
            });
        }
        catch
        {
            if (DataContext is MainViewModel vm)
                vm.StatusMessage = "X: https://x.com/KhoaLe7722";
        }
    }

    private void OpenAuthorGmail_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "mailto:leanhkhoa7722@gmail.com",
                UseShellExecute = true
            });
        }
        catch
        {
            if (DataContext is MainViewModel vm)
                vm.StatusMessage = "Gmail: leanhkhoa7722@gmail.com";
        }
    }

    private void CloseHelpModal_Click(object sender, RoutedEventArgs e)
    {
        if (HelpModalOverlay != null) HelpModalOverlay.Visibility = Visibility.Collapsed;
    }

    private void HelpModalBackdrop_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (HelpModalOverlay != null) HelpModalOverlay.Visibility = Visibility.Collapsed;
    }

    private void SetupModalBackdrop_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.IsSetupModalOpen = false;
    }

    private void HelpModalCard_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void MenuHelpGuide_Click(object sender, RoutedEventArgs e)
    {
        var lang = Infrastructure.Settings.LocalizationService.Instance.CurrentLanguage;
        if (lang == Infrastructure.Settings.AppLanguage.En)
        {
            ShowHelpModal(
                "Quick Start Guide — Gula Studio",
                "1. Instant Inspiration Bento Wall:\n" +
                "   Hover over any artwork on the home screen to preview its style and click to load its curated prompt and aspect ratio.\n\n" +
                "2. Local AI Creation & Pro Controls:\n" +
                "   Type in English, Vietnamese, or Chinese (or use the microphone button), and combine 3D Poses, Outfits, 0-VRAM Face Lock, and LoRAs.\n\n" +
                "3. Multi-Scene Storyboard:\n" +
                "   Switch to Storyboard to manage persistent characters (1–5 reference angles) and render consistent multi-scene stories.\n\n" +
                "4. One-Click Setup & Model Manager:\n" +
                "   Open File → Setup & Model Manager anytime to auto-download and install ComfyUI, Custom Nodes, and AI weights.");
        }
        else if (lang == Infrastructure.Settings.AppLanguage.Zh)
        {
            ShowHelpModal(
                "快速使用指南 — Gula Studio",
                "1. 灵感 Bento 瀑布墙：\n" +
                "   悬停主页任意作品查看风格，点击即可一键填充精选提示词与画幅比例。\n\n" +
                "2. 本地 AI 绘图与专业控制：\n" +
                "   支持中/英/越文输入或语音转提示词，自由搭配 3D 姿势库、服装库、0 显存面部锁定与 LoRA 风格。\n\n" +
                "3. 多分镜故事工作室：\n" +
                "   切换到“故事分镜”管理固定角色（支持 1–5 张多角度参考脸），稳定输出连贯剧情画面。\n\n" +
                "4. 一键安装与模型管理器：\n" +
                "   点击 File → Setup & Model Manager 随时一键下载并自动安装 ComfyUI、插件与全部模型。");
        }
        else
        {
            ShowHelpModal(
                "Hướng dẫn sử dụng nhanh — Gula Studio",
                "1. Khởi tạo nhanh bằng lưới ảnh mẫu:\n" +
                "   Rê chuột vào bất kỳ ảnh nào ở màn hình chính để xem phong cách và bấm để nạp sẵn prompt, tỷ lệ khung hình tương ứng.\n\n" +
                "2. Tạo ảnh AI & Điều khiển chuyên sâu:\n" +
                "   Nhập ý tưởng tiếng Việt/Anh/Trung hoặc nói bằng giọng nói (biểu tượng micro), kết hợp Dáng 3D, Trang phục, Khóa gương mặt (CPU 0 VRAM) và phong cách LoRA.\n\n" +
                "3. Kể chuyện nhiều phân cảnh (Storyboard):\n" +
                "   Chuyển sang mục Câu chuyện để quản lý dàn nhân vật cố định (1–5 ảnh góc mặt) và sinh chuỗi phân cảnh nhất quán.\n\n" +
                "4. Hậu kỳ Studio Pro & Tải mô hình 1-Click:\n" +
                "   Vào File → Setup & Model Manager để tải tự động mọi mô hình AI, hoặc mở ảnh lớn để dùng Inpainting, Siêu nét 2K, Tách nền RMBG-1.4 và Chỉnh màu phim.");
        }
    }

    private void MenuHelpShortcuts_Click(object sender, RoutedEventArgs e)
    {
        var lang = Infrastructure.Settings.LocalizationService.Instance.CurrentLanguage;
        if (lang == Infrastructure.Settings.AppLanguage.En)
        {
            ShowHelpModal(
                "Keyboard Shortcuts — Gula Studio",
                "• Ctrl + Enter          Generate image from current prompt\n" +
                "• Ctrl + N              Start a new conversation\n" +
                "• Ctrl + B              Toggle left sidebar\n" +
                "• Ctrl + ,              Open Settings & Model Manager\n" +
                "• Ctrl + F              Search conversation history\n" +
                "• Ctrl + Z / Ctrl + Y   Undo / Redo text\n" +
                "• Ctrl + + / Ctrl + -   Zoom UI in / out\n" +
                "• Ctrl + 0              Reset UI zoom to 100%\n" +
                "• F11                   Toggle Full Screen\n" +
                "• Ctrl + M              Minimize window\n" +
                "• ← / →                 Previous / next image in Lightbox\n" +
                "• Esc                   Close dialog / exit Full Screen\n" +
                "• F1                    Open this shortcuts guide");
        }
        else if (lang == Infrastructure.Settings.AppLanguage.Zh)
        {
            ShowHelpModal(
                "键盘快捷键 — Gula Studio",
                "• Ctrl + Enter          根据当前提示词生成图像\n" +
                "• Ctrl + N              新建对话\n" +
                "• Ctrl + B              展开 / 收起左侧边栏\n" +
                "• Ctrl + ,              打开设置与模型管理器\n" +
                "• Ctrl + F              搜索历史记录\n" +
                "• Ctrl + Z / Ctrl + Y   撤销 / 重做文本\n" +
                "• Ctrl + + / Ctrl + -   放大 / 缩小界面比例\n" +
                "• Ctrl + 0              重置界面比例为 100%\n" +
                "• F11                   切换全屏模式\n" +
                "• Ctrl + M              最小化窗口\n" +
                "• ← / →                 大图预览切换上一张 / 下一张\n" +
                "• Esc                   关闭弹窗 / 退出全屏\n" +
                "• F1                    打开此快捷键列表");
        }
        else
        {
            ShowHelpModal(
                "Danh sách phím tắt — Gula Studio",
                "• Ctrl + Enter          Tạo ảnh từ câu lệnh hiện tại\n" +
                "• Ctrl + N              Tạo cuộc trò chuyện mới\n" +
                "• Ctrl + B              Bật / tắt thanh bên (Sidebar)\n" +
                "• Ctrl + ,              Mở Cài đặt / Tùy chọn (Preferences)\n" +
                "• Ctrl + F              Tìm kiếm trong lịch sử trò chuyện\n" +
                "• Ctrl + Z / Ctrl + Y   Hoàn tác / Làm lại văn bản\n" +
                "• Ctrl + + / Ctrl + -   Phóng to / Thu nhỏ tỷ lệ giao diện\n" +
                "• Ctrl + 0              Đặt lại tỷ lệ giao diện 100%\n" +
                "• F11                   Bật / tắt chế độ Toàn màn hình\n" +
                "• Ctrl + M              Thu nhỏ cửa sổ xuống thanh tác vụ\n" +
                "• ← / →                 Chuyển ảnh trước / sau trong Lightbox\n" +
                "• Esc                   Đóng hộp thoại / thoát Toàn màn hình\n" +
                "• F1                    Mở bảng phím tắt này");
        }
    }

    private void MenuHelpCheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        var lang = Infrastructure.Settings.LocalizationService.Instance.CurrentLanguage;
        if (lang == Infrastructure.Settings.AppLanguage.En)
        {
            ShowHelpModal(
                "Check for Updates",
                "Current Version: Gula Studio 1.0.0 (Local Edition)\n" +
                "Inference Engine: Z-Image-Turbo INT8 ConvRot · ComfyUI\n" +
                "Runtime Stack: .NET 10 WPF · PyTorch CUDA · ONNX Runtime CPU\n\n" +
                "Status: You are running the latest local release. Image generation, prompt enhancement, and face locking run 100% offline on your PC.");
        }
        else if (lang == Infrastructure.Settings.AppLanguage.Zh)
        {
            ShowHelpModal(
                "检查更新",
                "当前版本：Gula Studio 1.0.0 (本地版)\n" +
                "生成引擎：Z-Image-Turbo INT8 ConvRot · ComfyUI\n" +
                "运行环境：.NET 10 WPF · PyTorch CUDA · ONNX Runtime CPU\n\n" +
                "状态：您已使用最新本地版本。所有图像生成、提示词扩写与面部锁定均在本地安全离线运行。");
        }
        else
        {
            ShowHelpModal(
                "Kiểm tra cập nhật",
                "Phiên bản hiện tại: Gula Studio 1.0.0 (Local Edition)\n" +
                "Động cơ tạo sinh: Z-Image-Turbo INT8 ConvRot · ComfyUI\n" +
                "Môi trường thực thi: .NET 10 WPF · PyTorch CUDA · ONNX Runtime CPU\n\n" +
                "Trạng thái: Bạn đang sử dụng phiên bản cục bộ mới nhất. Toàn bộ quá trình tạo ảnh, mở rộng prompt và khóa danh tính vận hành ngoại tuyến an toàn trên máy cá nhân.");
        }
    }

    private void MenuHelpAbout_Click(object sender, RoutedEventArgs e)
    {
        var lang = Infrastructure.Settings.LocalizationService.Instance.CurrentLanguage;
        if (lang == Infrastructure.Settings.AppLanguage.En)
        {
            ShowHelpModal(
                "About Gula Studio",
                "Thank you for using Gula Studio! I am:\n" +
                "• Lê Anh Khoa (feel free to call me Khoa Lê)\n" +
                "• GitHub: https://github.com/KhoaLe7722\n" +
                "• X: https://x.com/KhoaLe7722\n" +
                "• Gmail: leanhkhoa7722@gmail.com\n" +
                "If you have feedback or new ideas, don't hesitate to reach out!\n\n" +
                "Gula Studio v1.0.0\n" +
                "Local AI character concept & multi-scene storyboard workstation powered by Z-Image-Turbo.\n\n" +
                "Integrated Technologies:\n" +
                "• Z-Image-Turbo INT8 ConvRot + Qwen3-4B FP8 + DynamicVRAM\n" +
                "• ControlNet Union 2.1 Lite (Depth Cosine & Soft-Edge Inpainting)\n" +
                "• 0-VRAM CPU Face Identity Lock (SCRFD, ArcFace, HyperSwap 256, GFPGAN, CIE LAB)\n" +
                "• Studio Pro: Tiled Hires-Fix 2K Hann 2D, Bria RMBG-1.4, BGRA Film Grading, Whisper.net & llama-server",
                showAboutLinks: true);
        }
        else if (lang == Infrastructure.Settings.AppLanguage.Zh)
        {
            ShowHelpModal(
                "关于 Gula Studio",
                "感谢您使用 Gula Studio，我是作者：\n" +
                "• Lê Anh Khoa（叫我 Khoa Lê 就好）\n" +
                "• GitHub: https://github.com/KhoaLe7722\n" +
                "• X: https://x.com/KhoaLe7722\n" +
                "• Gmail: leanhkhoa7722@gmail.com\n" +
                "如果您有任何建议或新想法，欢迎随时联系我！\n\n" +
                "Gula Studio v1.0.0\n" +
                "基于 Z-Image-Turbo 的本地 AI 角色概念设计与多分镜故事创作工作站。\n\n" +
                "集成核心技术：\n" +
                "• Z-Image-Turbo INT8 ConvRot + Qwen3-4B FP8 + DynamicVRAM\n" +
                "• ControlNet Union 2.1 Lite（3D 深度余弦控制与柔边局部重绘）\n" +
                "• CPU 0 显存面部身份锁定（SCRFD、ArcFace、HyperSwap 256、GFPGAN、CIE LAB）\n" +
                "• Studio Pro：2K 分块高清重绘、Bria RMBG-1.4 抠图、BGRA 实时调色、Whisper.net 与 llama-server",
                showAboutLinks: true);
        }
        else
        {
            ShowHelpModal(
                "About Gula Studio",
                "Cảm ơn bạn đã sử dụng Gula Studio, mình là:\n" +
                "• Lê Anh Khoa (bạn cứ gọi mình là Khoa Lê nhé)\n" +
                "• GitHub: https://github.com/KhoaLe7722\n" +
                "• X: https://x.com/KhoaLe7722\n" +
                "• Gmail: leanhkhoa7722@gmail.com\n" +
                "Nếu bạn có góp ý hay ý tưởng mới, đừng ngại nhắn cho mình nha!\n\n" +
                "Gula Studio v1.0.0\n" +
                "Xưởng tạo hình nhân vật và phác thảo phân cảnh AI cục bộ tích hợp Z-Image-Turbo.\n\n" +
                "Công nghệ tích hợp:\n" +
                "• Z-Image-Turbo INT8 ConvRot + Qwen3-4B FP8 + DynamicVRAM\n" +
                "• ControlNet Union 2.1 Lite (Depth Cosine & Soft-Edge Inpainting)\n" +
                "• Khóa danh tính bảo toàn biểu cảm trên CPU (SCRFD, ArcFace, HyperSwap 256, GFPGAN, CIE LAB)\n" +
                "• Studio Pro: Tiled Hires-Fix 2K Hann 2D, Bria RMBG-1.4, Chỉnh màu BGRA, Whisper.net & llama-server",
                showAboutLinks: true);
        }
    }

    private IInputElement? _dialogOrigin;
    private bool? _lastCompactNav;
    private bool _isSidebarManuallyCollapsed;

    public bool IsSidebarCollapsed => _isSidebarManuallyCollapsed;

    public void ToggleSidebar()
    {
        _isSidebarManuallyCollapsed = !_isSidebarManuallyCollapsed;
        ApplyResponsiveNavigation(force: true);
    }

    private void ToggleSidebar_Click(object sender, RoutedEventArgs e) => ToggleSidebar();

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (e.PropertyName is nameof(vm.IsCreateProjectDialogOpen) or nameof(vm.IsCreateCharacterDialogOpen))
        {
            var input = vm.IsCreateCharacterDialogOpen ? CharacterNameInput
                : vm.IsCreateProjectDialogOpen ? ProjectNameInput : null;
            if (input != null)
            {
                _dialogOrigin = Keyboard.FocusedElement;
                Dispatcher.BeginInvoke(() => { input.Focus(); input.SelectAll(); });
            }
            else if (_dialogOrigin is { } origin)
            {
                _dialogOrigin = null;
                Dispatcher.BeginInvoke(() => Keyboard.Focus(origin));
            }
        }
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyResponsiveNavigation();

    private void ApplyResponsiveNavigation(bool force = false)
    {
        if (SidebarColumn == null || SidebarBorder == null) return;

        if (_isSidebarManuallyCollapsed)
        {
            _lastCompactNav = null;
            SidebarColumn.Width = new GridLength(0);
            SidebarBorder.Visibility = Visibility.Collapsed;
            CollapsedSidebarHeader.Visibility = Visibility.Visible;
            CollapsedMiniLogo.Visibility = Visibility.Visible;
            return;
        }

        var compact = ActualWidth < 1280;
        if (!force && _lastCompactNav == compact && SidebarBorder.Visibility == Visibility.Visible) return;
        _lastCompactNav = compact;

        SidebarBorder.Visibility = Visibility.Visible;
        SidebarColumn.Width = new GridLength(compact ? 64 : 256);
        ChatHistorySection.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        SidebarCollapseButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        CollapsedSidebarHeader.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        CollapsedMiniLogo.Visibility = Visibility.Collapsed;

        foreach (var label in Descendants<FrameworkElement>(SidebarBorder).Where(el => Equals(el.Tag, "NavLabel")))
            label.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        var navigationStyle = TryFindResource("BtnSidebarNav");
        foreach (var button in Descendants<Button>(SidebarBorder).Where(b => ReferenceEquals(b.Style, navigationStyle) || b.Name == "NewChatButton"))
        {
            if (compact)
            {
                // The 64 DIP rail has 40 DIP available after its outer padding.
                // Remove the expanded button's horizontal padding so the whole icon fits.
                button.Padding = new Thickness(0);
                button.HorizontalContentAlignment = HorizontalAlignment.Center;
            }
            else
            {
                if (button.Name == "NewChatButton")
                {
                    button.Padding = new Thickness(16, 0, 16, 0);
                    button.HorizontalContentAlignment = HorizontalAlignment.Center;
                }
                else
                {
                    button.ClearValue(Control.PaddingProperty);
                    button.ClearValue(Control.HorizontalContentAlignmentProperty);
                }
            }
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (HelpModalOverlay?.Visibility == Visibility.Visible && e.Key == Key.Escape)
        {
            HelpModalOverlay.Visibility = Visibility.Collapsed;
            e.Handled = true;
            return;
        }
        if (DataContext is MainViewModel vmSetup && vmSetup.IsSetupModalOpen && e.Key == Key.Escape)
        {
            vmSetup.IsSetupModalOpen = false;
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F11)
        {
            ToggleFullScreen();
            e.Handled = true;
            return;
        }
        if (_isFullScreen && e.Key == Key.Escape && DataContext is MainViewModel vmFs && !vmFs.IsLightboxOpen && !vmFs.IsStoryLightboxOpen && !vmFs.IsPoseGalleryOpen && !vmFs.IsOutfitGalleryOpen)
        {
            ToggleFullScreen();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F1)
        {
            MenuHelpShortcuts_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key is Key.OemPlus or Key.Add)
        {
            SetWorkspaceZoom(_workspaceZoom + 0.10);
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (e.Key == Key.B) { ToggleSidebar(); e.Handled = true; return; }
            if (e.Key == Key.OemComma) { MenuEditPreferences_Click(this, new RoutedEventArgs()); e.Handled = true; return; }
            if (e.Key is Key.OemPlus or Key.Add) { SetWorkspaceZoom(_workspaceZoom + 0.10); e.Handled = true; return; }
            if (e.Key is Key.OemMinus or Key.Subtract) { SetWorkspaceZoom(_workspaceZoom - 0.10); e.Handled = true; return; }
            if (e.Key is Key.D0 or Key.NumPad0) { SetWorkspaceZoom(1.0); e.Handled = true; return; }
            if (e.Key == Key.M) { SystemCommands.MinimizeWindow(this); e.Handled = true; return; }
        }

        if (DataContext is not MainViewModel vm || !vm.IsLoggedIn) return;
        if (e.Key == Key.Escape && (vm.IsVoiceRecording || vm.IsVoiceTranscribing))
        {
            vm.CancelVoiceInputCommand.Execute(null); e.Handled = true; return;
        }
        if (vm.IsOutfitGalleryOpen)
        {
            if (e.Key == Key.Escape)
            {
                vm.CloseOutfitGalleryCommand.Execute(null); e.Handled = true; return;
            }
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
            {
                var search = Descendants<TextBox>(this).FirstOrDefault(t => t.IsVisible && t.Name == "OutfitSearchBox");
                if (search != null) { search.Focus(); search.SelectAll(); e.Handled = true; }
            }
            return;
        }
        if (vm.IsPoseGalleryOpen)
        {
            if (e.Key == Key.Escape)
            {
                vm.ClosePoseGalleryCommand.Execute(null); e.Handled = true; return;
            }
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
            {
                var search = Descendants<TextBox>(this).FirstOrDefault(t => t.IsVisible && t.Name == "PoseSearchBox");
                if (search != null) { search.Focus(); search.SelectAll(); e.Handled = true; }
            }
            return;
        }
        if (vm.IsStoryLightboxOpen)
        {
            if (e.Key == Key.Escape) { vm.CloseStoryLightboxCommand.Execute(null); e.Handled = true; }
            return;
        }
        if (vm.IsLightboxOpen)
        {
            if (e.Key == Key.Escape)
            {
                vm.CloseLightboxCommand.Execute(null); e.Handled = true; return;
            }
            if (e.OriginalSource is not System.Windows.Controls.Primitives.TextBoxBase)
            {
                if (e.Key == Key.Left && vm.PreviousLightboxImageCommand.CanExecute(null))
                {
                    vm.PreviousLightboxImageCommand.Execute(null); e.Handled = true; return;
                }
                if (e.Key == Key.Right && vm.NextLightboxImageCommand.CanExecute(null))
                {
                    vm.NextLightboxImageCommand.Execute(null); e.Handled = true; return;
                }
            }
            return;
        }
        if (vm.IsCreateProjectDialogOpen)
        {
            if (e.Key == Key.Escape) { vm.CloseCreateProjectDialogCommand.Execute(null); e.Handled = true; }
            return;
        }
        if (vm.IsCreateCharacterDialogOpen)
        {
            if (e.Key == Key.Escape) { vm.CloseCreateCharacterDialogCommand.Execute(null); e.Handled = true; }
            return;
        }
        if (e.Key == Key.Escape && vm.CurrentTabIndex == 0)
        {
            if (vm.IsSecondaryCharacterPickerOpen) { vm.IsSecondaryCharacterPickerOpen = false; e.Handled = true; return; }
            if (vm.IsStudioCharacterPickerOpen) { vm.IsStudioCharacterPickerOpen = false; e.Handled = true; return; }
            if (vm.IsStudioAdvancedOpen) { vm.IsStudioAdvancedOpen = false; e.Handled = true; return; }
            if (vm.IsPromptPreviewOpen) { vm.IsPromptPreviewOpen = false; e.Handled = true; return; }
            if (vm.IsTextOverlayOpen) { vm.IsTextOverlayOpen = false; e.Handled = true; return; }
        }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
        {
            var search = Descendants<TextBox>(this).FirstOrDefault(t => t.IsVisible && t.Name is "ProjectSearchBox" or "CharacterSearchBox" or "ChatSearchInput");
            if (search != null) { search.Focus(); search.SelectAll(); e.Handled = true; }
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.N && vm.CurrentTabIndex is 6 or 10)
        {
            vm.OpenCreateProjectDialogCommand.Execute(null); e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.N && vm.IsLocalStudioMode && vm.CurrentTabIndex is 0 or 5 or 7 or 12)
        {
            vm.NewChatCommand.Execute(null);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.N && vm.CurrentTabIndex is 0 or 1)
        {
            if (vm.OpenCreateCharacterDialogCommand.CanExecute(null))
                vm.OpenCreateCharacterDialogCommand.Execute(null);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S)
        {
            ICommand? command = null;
            if (vm.CurrentTabIndex == 1)
            {
                var index = Descendants<CharactersView>(this).FirstOrDefault()?.FindName("CharacterDetailsTabs") as TabControl;
                command = index?.SelectedIndex switch { 0 => vm.SaveCharacterProfileCommand, 1 => vm.SaveAttributesCommand, 2 => vm.SaveVariantAttributesCommand, _ => null };
            }
            else if (vm.CurrentTabIndex == 3)
            {
                var index = Descendants<ScriptsView>(this).FirstOrDefault()?.FindName("ScriptDetailsTabs") as TabControl;
                command = index?.SelectedIndex == 0 ? vm.SaveScriptCommand : vm.SaveSceneCommand;
            }
            if (command?.CanExecute(null) == true) { command.Execute(null); e.Handled = true; }
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.R && vm.CurrentTabIndex is 6 or 10)
        {
            if (vm.RefreshProjectsCommand.CanExecute(null)) vm.RefreshProjectsCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.F2 && Keyboard.Modifiers == ModifierKeys.None && vm.CurrentTabIndex is 6 or 10)
        {
            if (vm.OpenEditProjectCommand.CanExecute(null)) vm.OpenEditProjectCommand.Execute(null);
            e.Handled = true;
        }
    }

    internal static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}

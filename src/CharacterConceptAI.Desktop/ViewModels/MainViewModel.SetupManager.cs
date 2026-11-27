using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CharacterConceptAI.Desktop.Services;
using Microsoft.Win32;

namespace CharacterConceptAI.Desktop.ViewModels;

public partial class MainViewModel
{
    private readonly DependencySetupService _setupService = new();
    private CancellationTokenSource? _setupDownloadCts;
    private bool _setupInitialized;

    public ObservableCollection<SetupPackageItem> SetupPackages { get; } = [];

    [ObservableProperty]
    private bool _isSetupModalOpen;

    [ObservableProperty]
    private string _setupWorkspacePath = GulaWorkspaceLocator.GetWorkspaceRoot();

    [ObservableProperty]
    private string _setupGpuInfo = "Đang kiểm tra GPU...";

    [ObservableProperty]
    private bool _setupHasNvidiaGpu = true;

    [ObservableProperty]
    private string _setupSqlInfo = "Đang kiểm tra SQL Server...";

    [ObservableProperty]
    private bool _setupHasSqlServer = true;

    [ObservableProperty]
    private string _setupPythonInfo = "Đang kiểm tra Python...";

    [ObservableProperty]
    private string _setupFreeDiskInfo = "Đang kiểm tra ổ đĩa...";

    [ObservableProperty]
    private string _setupSummaryText = "Bấm \"Quét ổ đĩa\" để kiểm tra các gói cần tải.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSetupIdle))]
    private bool _isAnySetupDownloading;

    [ObservableProperty]
    private string _setupBannerMessage = string.Empty;

    public bool IsSetupIdle => !IsAnySetupDownloading;

    public void EnsureSetupCatalogLoaded()
    {
        if (_setupInitialized) return;
        _setupInitialized = true;
        foreach (var pkg in DependencySetupService.CreateCatalog())
            SetupPackages.Add(pkg);

        SetupWorkspacePath = GulaWorkspaceLocator.GetWorkspaceRoot();
        _setupService.RefreshInstallStates(SetupPackages, SetupWorkspacePath);
        UpdateSetupSummary();
    }

    public void RefreshSetupLocalizedStrings()
    {
        if (SetupPackages.Count > 0)
        {
            _setupService.RefreshInstallStates(SetupPackages, SetupWorkspacePath);
            foreach (var pkg in SetupPackages)
                pkg.NotifyLocalizationChanged();
            UpdateSetupSummary();
        }
    }

    private void UpdateSetupSummary()
    {
        var essentialTotal = SetupPackages.Count(p => p.IsEssential);
        var essentialReady = SetupPackages.Count(p => p.IsEssential && p.IsInstalled);
        var totalReady = SetupPackages.Count(p => p.IsInstalled);
        var lang = Infrastructure.Settings.LocalizationService.Instance.CurrentLanguage;
        SetupSummaryText = lang switch
        {
            Infrastructure.Settings.AppLanguage.En => essentialReady == essentialTotal
                ? $"Ready to run · {essentialReady}/{essentialTotal} required installed · Total {totalReady}/{SetupPackages.Count} packages."
                : $"Missing {essentialTotal - essentialReady} required package(s) ({essentialReady}/{essentialTotal}) for local AI generation.",
            Infrastructure.Settings.AppLanguage.Zh => essentialReady == essentialTotal
                ? $"已就绪 · 核心必需包 {essentialReady}/{essentialTotal} 已安装 · 总计 {totalReady}/{SetupPackages.Count} 个组件。"
                : $"缺少 {essentialTotal - essentialReady} 个核心必需组件（{essentialReady}/{essentialTotal}），请点击一键安装。",
            _ => essentialReady == essentialTotal
                ? $"Sẵn sàng vận hành · Đã cài {essentialReady}/{essentialTotal} gói bắt buộc · Tổng {totalReady}/{SetupPackages.Count} gói."
                : $"Còn thiếu {essentialTotal - essentialReady} gói bắt buộc ({essentialReady}/{essentialTotal}) để chạy tạo ảnh AI cục bộ."
        };
    }

    [RelayCommand]
    private async Task OpenSetupManagerAsync()
    {
        EnsureSetupCatalogLoaded();
        IsSetupModalOpen = true;
        await RefreshSetupStatusAsync();
    }

    [RelayCommand]
    private void CloseSetupManager()
    {
        IsSetupModalOpen = false;
    }

    [RelayCommand]
    private async Task RefreshSetupStatusAsync()
    {
        EnsureSetupCatalogLoaded();
        SetupWorkspacePath = GulaWorkspaceLocator.GetWorkspaceRoot();
        _setupService.RefreshInstallStates(SetupPackages, SetupWorkspacePath);
        UpdateSetupSummary();

        try
        {
            var diag = await _setupService.ProbeEnvironmentAsync(SetupWorkspacePath);
            SetupGpuInfo = diag.GpuLabel;
            SetupHasNvidiaGpu = diag.HasNvidiaGpu;
            SetupSqlInfo = diag.SqlStatusLabel;
            SetupHasSqlServer = diag.HasSqlServer;
            SetupPythonInfo = diag.PythonLabel;
            SetupFreeDiskInfo = diag.FreeDiskLabel;
        }
        catch
        {
            // Keep graceful fallback labels when running in restricted/sandboxed environments.
        }
    }

    [RelayCommand]
    private async Task BrowseSetupWorkspaceAsync()
    {
        if (IsAnySetupDownloading) return;
        var dialog = new OpenFolderDialog
        {
            Title = "Chọn thư mục cài đặt ComfyUI & Mô hình AI cho Gula Studio",
            InitialDirectory = Directory.Exists(SetupWorkspacePath) ? SetupWorkspacePath : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        };
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            GulaWorkspaceLocator.SetWorkspaceRoot(dialog.FolderName);
            SetupWorkspacePath = GulaWorkspaceLocator.GetWorkspaceRoot();
            SetupBannerMessage = $"Đã đổi thư mục cài đặt sang: {SetupWorkspacePath}";
            await RefreshSetupStatusAsync();
        }
    }

    [RelayCommand]
    private void OpenSetupWorkspaceFolder()
    {
        try
        {
            Directory.CreateDirectory(SetupWorkspacePath);
            Process.Start(new ProcessStartInfo { FileName = SetupWorkspacePath, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetupBannerMessage = $"Không thể mở thư mục: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task InstallSetupPackageAsync(SetupPackageItem? package)
    {
        if (package == null || IsAnySetupDownloading) return;
        EnsureSetupCatalogLoaded();

        _setupDownloadCts?.Dispose();
        _setupDownloadCts = new CancellationTokenSource();
        IsAnySetupDownloading = true;
        SetupBannerMessage = $"Đang tải và cài đặt: {package.Name}...";
        StatusMessage = SetupBannerMessage;

        try
        {
            await _setupService.InstallPackageAsync(package, SetupWorkspacePath, _setupDownloadCts.Token);
            _setupService.RefreshInstallStates(SetupPackages, SetupWorkspacePath);
            UpdateSetupSummary();
            SetupBannerMessage = $"Đã cài đặt xong: {package.Name} ✓";
            StatusMessage = SetupBannerMessage;
        }
        catch (OperationCanceledException)
        {
            SetupBannerMessage = $"Đã tạm dừng tải {package.Name}. Tệp tạm được giữ lại để tải tiếp.";
            StatusMessage = SetupBannerMessage;
        }
        catch (Exception ex)
        {
            SetupBannerMessage = $"Cài đặt {package.Name} chưa hoàn tất: {ex.Message}";
            StatusMessage = SetupBannerMessage;
        }
        finally
        {
            IsAnySetupDownloading = false;
        }
    }

    [RelayCommand]
    private async Task InstallEssentialBundleAsync() => await InstallBundleInternalAsync(essentialOnly: true);

    [RelayCommand]
    private async Task InstallFullBundleAsync() => await InstallBundleInternalAsync(essentialOnly: false);

    private async Task InstallBundleInternalAsync(bool essentialOnly)
    {
        if (IsAnySetupDownloading) return;
        EnsureSetupCatalogLoaded();
        _setupService.RefreshInstallStates(SetupPackages, SetupWorkspacePath);

        var targets = SetupPackages
            .Where(p => !p.IsInstalled && (essentialOnly ? p.IsEssential : (p.IsEssential || p.IsRecommended)))
            .ToList();

        if (targets.Count == 0)
        {
            SetupBannerMessage = essentialOnly
                ? "Toàn bộ gói bắt buộc đã được cài đặt đầy đủ ✓"
                : "Toàn bộ gói cơ bản & nâng cao đã được cài đặt đầy đủ ✓";
            StatusMessage = SetupBannerMessage;
            return;
        }

        _setupDownloadCts?.Dispose();
        _setupDownloadCts = new CancellationTokenSource();
        IsAnySetupDownloading = true;

        try
        {
            for (var i = 0; i < targets.Count; i++)
            {
                var pkg = targets[i];
                SetupBannerMessage = $"Đang cài đặt gói {i + 1}/{targets.Count}: {pkg.Name}...";
                StatusMessage = SetupBannerMessage;
                await _setupService.InstallPackageAsync(pkg, SetupWorkspacePath, _setupDownloadCts.Token);
                _setupService.RefreshInstallStates(SetupPackages, SetupWorkspacePath);
                UpdateSetupSummary();
            }

            SetupBannerMessage = $"Hoàn tất cài đặt {targets.Count} gói! Bấm \"Khởi động lại Dịch vụ AI\" để dùng ngay.";
            StatusMessage = SetupBannerMessage;
        }
        catch (OperationCanceledException)
        {
            SetupBannerMessage = "Đã dừng chuỗi cài đặt tự động. Bạn có thể bấm lại để tải tiếp phần còn lại.";
            StatusMessage = SetupBannerMessage;
        }
        catch (Exception ex)
        {
            SetupBannerMessage = $"Dừng cài đặt do lỗi: {ex.Message}";
            StatusMessage = SetupBannerMessage;
        }
        finally
        {
            IsAnySetupDownloading = false;
        }
    }

    [RelayCommand]
    private void CancelSetupDownload()
    {
        try { _setupDownloadCts?.Cancel(); } catch { }
    }

    [RelayCommand]
    private async Task InstallSqlServerExpressAsync()
    {
        if (IsAnySetupDownloading) return;
        IsAnySetupDownloading = true;
        SetupBannerMessage = "Đang cài đặt SQL Server 2022 Express qua winget (có thể hiện hộp thoại cấp quyền Windows)...";
        try
        {
            var (ok, msg) = await _setupService.InstallSqlServerExpressViaWingetAsync(CancellationToken.None);
            SetupBannerMessage = msg;
            if (!ok)
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "https://www.microsoft.com/en-us/sql-server/sql-server-downloads",
                        UseShellExecute = true
                    });
                }
                catch { }
            }
            await RefreshSetupStatusAsync();
        }
        finally
        {
            IsAnySetupDownloading = false;
        }
    }

    [RelayCommand]
    private async Task RestartLocalAiServicesAsync()
    {
        try
        {
            if (Uri.TryCreate(ServerUrl, UriKind.Absolute, out var uri))
                App.EnsureLocalBackendServicesStarted(uri);
            SetupBannerMessage = "Đã kích hoạt khởi động lại ComfyUI, Prompt Enhancer và kiểm tra kết nối máy chủ cục bộ...";
            await Task.Delay(1500);
            if (RefreshConnectionCommand.CanExecute(null))
                await RefreshConnectionCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            SetupBannerMessage = $"Không thể khởi động dịch vụ: {ex.Message}";
        }
    }
}

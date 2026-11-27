using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using CharacterConceptAI.Desktop.Infrastructure.Settings;
using CharacterConceptAI.Desktop.Services;
using CharacterConceptAI.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace CharacterConceptAI.Desktop;

public partial class App : Application
{
    private ServiceProvider? _services;
    private readonly bool _suppressProductionBootstrap;

    public App() { }

    // In-process WPF smoke harness: load the identical compiled App resources without starting real services.
    public App(bool suppressProductionBootstrap) => _suppressProductionBootstrap = suppressProductionBootstrap;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (_suppressProductionBootstrap) return;
        ThemeService.Instance.Initialize();
        LocalizationService.Instance.Initialize();
        if (Environment.GetEnvironmentVariable("CHARACTER_CONCEPT_AI_SOFTWARE_RENDERING") == "1")
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        var serverUrl = ReadServerUrl(e.Args);
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0
            || uri.Query.Length > 0 || uri.Fragment.Length > 0
            || !(uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback))
        {
            MessageBox.Show("Địa chỉ máy chủ không hợp lệ. Dùng HTTPS cho máy chủ LAN; HTTP chỉ dùng trên máy cục bộ.",
                "Cấu hình kết nối", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(1); return;
        }

        var services = new ServiceCollection();
        services.AddHttpClient("CharacterConceptApi", client => client.Timeout = TimeSpan.FromSeconds(45))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.Brotli | DecompressionMethods.GZip,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5)
            });
        services.AddHttpClient("CharacterConceptPose", client => client.Timeout = TimeSpan.FromSeconds(240))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.Brotli | DecompressionMethods.GZip,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5)
            });
        services.AddSingleton(provider =>
        {
            var factory = provider.GetRequiredService<IHttpClientFactory>();
            var client = new ApiClient(factory.CreateClient("CharacterConceptApi"), factory.CreateClient("CharacterConceptPose"))
            {
                BaseUrl = serverUrl.TrimEnd('/')
            };
            if (uri.IsLoopback)
            {
                client.EnsureLocalServerAsync = async ct =>
                {
                    EnsureLocalBackendServicesStarted(uri);
                    return await client.WaitUntilHealthyAsync(TimeSpan.FromSeconds(18), ct);
                };
            }
            return client;
        });
        services.AddSingleton<AuthSessionService>();
        services.AddSingleton<MainViewModel>();
        services.AddTransient<MainWindow>();
        _services = services.BuildServiceProvider();
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Photoshop-style artistic Splash Screen: shows immediately while backend services, workspace data,
        // and ComfyUI GPU weights (Z-Image Turbo + Qwen3-4B + VAE) warm up thoroughly before MainWindow opens.
        var splash = new SplashWindow();
        splash.Show();
        EnsureLocalBackendServicesStarted(uri);
        _ = RunSplashStartupAsync(splash);
    }

    private async Task RunSplashStartupAsync(SplashWindow splash)
    {
        try
        {
            var vm = _services!.GetRequiredService<MainViewModel>();
            vm.IsLocalAutoConnecting = true;

            // Start local backend + ComfyUI GPU pre-warmup IMMEDIATELY in parallel while the logo intro plays
            var startupTask = vm.StartLocalStudioAsync((pct, msg) => splash.ReportProgress(pct, msg));

            // Stage 1 (1.5s): Crisp Gula Studio logo reveal, then crossfade to the artistic splash poster
            await splash.PlayLogoIntroAndRevealPosterAsync(1500);

            // Stage 2: Keep artistic splash poster visible smoothly while waiting for GPU warmup (fast ~0.9s warm exit)
            var posterSw = System.Diagnostics.Stopwatch.StartNew();
            await startupTask;

            var minPosterMs = 900;
            var remaining = minPosterMs - (int)posterSw.ElapsedMilliseconds;
            if (remaining > 0)
            {
                splash.ReportProgress(100, "Gula Studio đã sẵn sàng sáng tạo!");
                await Task.Delay(remaining);
            }
            else
            {
                splash.ReportProgress(100, "Gula Studio đã sẵn sàng sáng tạo!");
                await Task.Delay(180);
            }

            var mainWindow = _services!.GetRequiredService<MainWindow>();
            MainWindow = mainWindow;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            mainWindow.Show();
            mainWindow.Activate();
            await splash.FadeOutAndCloseAsync();
        }
        catch
        {
            try
            {
                var mainWindow = _services?.GetRequiredService<MainWindow>();
                if (mainWindow != null)
                {
                    MainWindow = mainWindow;
                    ShutdownMode = ShutdownMode.OnMainWindowClose;
                    mainWindow.Show();
                }
                splash.Close();
            }
            catch { Shutdown(1); }
        }
    }

    private static readonly object ServerLaunchLock = new();
    private static readonly object ComfyLaunchLock = new();
    private static readonly object EnhancerLaunchLock = new();
    private static long _lastServerLaunchTicks;
    private static long _lastComfyLaunchTicks;
    private static long _lastEnhancerLaunchTicks;

    internal static void EnsureLocalBackendServicesStarted(Uri serverUri)
    {
        if (!serverUri.IsLoopback) return;
        var serverPort = serverUri.Port > 0 ? serverUri.Port : 5000;

        // Launch all 3 independent local services in parallel (eliminates ~1s sequential TCP probe stall)
        _ = Task.Run(() => EnsureLocalApiServerStarted(serverPort));
        _ = Task.Run(EnsureLocalComfyUiStarted);
        _ = Task.Run(EnsureLocalPromptEnhancerStarted);
    }

    private static bool HasRunningProcess(string processName)
    {
        var procs = System.Diagnostics.Process.GetProcessesByName(processName);
        var any = procs.Length > 0;
        foreach (var p in procs)
        {
            try { p.Dispose(); } catch { }
        }
        return any;
    }

    private static void EnsureLocalApiServerStarted(int serverPort)
    {
        lock (ServerLaunchLock)
        {
            try
            {
                var now = Environment.TickCount64;
                if (IsTcpPortOpen("127.0.0.1", serverPort) ||
                    HasRunningProcess("CharacterConceptAI.Server") ||
                    (_lastServerLaunchTicks != 0 && now - _lastServerLaunchTicks <= 8_000))
                    return;

                var serverExe = GulaWorkspaceLocator.FindLocalServerExecutable();
                if (serverExe == null) return;

                _lastServerLaunchTicks = now;
                using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = serverExe,
                    Arguments = $"--urls http://127.0.0.1:{serverPort}",
                    WorkingDirectory = Path.GetDirectoryName(serverExe) ?? AppContext.BaseDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            catch { }
        }
    }

    private static void EnsureLocalComfyUiStarted()
    {
        lock (ComfyLaunchLock)
        {
            try
            {
                var now = Environment.TickCount64;
                if (IsTcpPortOpen("127.0.0.1", 8188) ||
                    (_lastComfyLaunchTicks != 0 && now - _lastComfyLaunchTicks <= 35_000))
                    return;

                // Cross-process guard shared with Server ComfyUiClient prevents duplicate ComfyUI instances
                using var mutex = new Mutex(false, @"Local\CharacterConceptAI.ComfyLaunch");
                var acquired = false;
                try
                {
                    acquired = mutex.WaitOne(TimeSpan.FromMilliseconds(200));
                    if (!acquired) return;

                    var stampPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "CharacterConceptAI", "comfy-launch.stamp");
                    if (File.Exists(stampPath) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(stampPath)).TotalSeconds < 35)
                        return;

                    var comfyBat = GulaWorkspaceLocator.FindComfyLaunchBatch();
                    if (string.IsNullOrWhiteSpace(comfyBat) || !File.Exists(comfyBat)) return;

                    _lastComfyLaunchTicks = now;
                    Directory.CreateDirectory(Path.GetDirectoryName(stampPath)!);
                    File.WriteAllText(stampPath, DateTimeOffset.UtcNow.ToString("O"));
                    using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c \"\"{comfyBat}\" --no-pause\"",
                        WorkingDirectory = Path.GetDirectoryName(comfyBat) ?? AppContext.BaseDirectory,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                }
                finally
                {
                    if (acquired) mutex.ReleaseMutex();
                }
            }
            catch { }
        }
    }

    private static void EnsureLocalPromptEnhancerStarted()
    {
        lock (EnhancerLaunchLock)
        {
            try
            {
                var now = Environment.TickCount64;
                if (IsTcpPortOpen("127.0.0.1", 11435) ||
                    HasRunningProcess("llama-server") ||
                    (_lastEnhancerLaunchTicks != 0 && now - _lastEnhancerLaunchTicks <= 15_000))
                    return;

                var workspaceRoot = GulaWorkspaceLocator.GetWorkspaceRoot();
                var peDir = Path.Combine(workspaceRoot, "PromptEnhancer", "bin");
                var peExe = Path.Combine(peDir, "llama-server.exe");
                var peModels = Path.Combine(workspaceRoot, "PromptEnhancer", "models");
                if (!File.Exists(peExe) || !Directory.Exists(peModels)) return;

                _lastEnhancerLaunchTicks = now;
                using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = peExe,
                    Arguments = $"--models-dir \"{peModels}\" --host 127.0.0.1 --port 11435 --ctx-size 2048 --n-gpu-layers 99 --cache-type-k q8_0 --cache-type-v q8_0 --sleep-idle-seconds 15 --parallel 1",
                    WorkingDirectory = peDir,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            catch { }
        }
    }

    private static bool IsTcpPortOpen(string host, int port)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            using var cts = new CancellationTokenSource(150);
            client.ConnectAsync(host, port, cts.Token).AsTask().GetAwaiter().GetResult();
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static string ReadServerUrl(string[] args)
    {
        var serverUrl = "http://127.0.0.1:5000";
        var config = Path.Combine(AppContext.BaseDirectory, "client-config.json");
        try
        {
            if (File.Exists(config))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(config));
                if (document.RootElement.TryGetProperty("ServerBaseUrl", out var property)
                    && !string.IsNullOrWhiteSpace(property.GetString())) serverUrl = property.GetString()!.Trim();
            }
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        { /* Environment and command line may still provide a valid address. */ }
        var environmentUrl = Environment.GetEnvironmentVariable("CHARACTER_CONCEPT_AI_SERVER_URL");
        if (!string.IsNullOrWhiteSpace(environmentUrl)) serverUrl = environmentUrl.Trim();
        for (var index = 0; index < args.Length - 1; index++)
            if (args[index] is "--server" or "-s") serverUrl = args[++index].Trim();
        return serverUrl;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }
}

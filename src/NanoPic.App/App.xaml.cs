using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using NanoPic.Codecs;
using NanoPic.Core;
using NanoPic.Infrastructure;

namespace NanoPic.App;

public partial class App : System.Windows.Application
{
    private static readonly TimeSpan EmbeddingIdleTimeout = TimeSpan.FromSeconds(30);

    private ShellIntegrationHost? _shellHost;
    private DispatcherTimer? _embeddingIdleTimer;
    private RedactingFileLogger? _logger;

    protected override void OnStartup(StartupEventArgs e)
    {
        AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", isEnabled: false);
        AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", isEnabled: false);
        DispatcherUnhandledException += HandleDispatcherUnhandledException;
        base.OnStartup(e);

        var decision = StartupModeParser.Parse(e.Args);
        switch (decision.Mode)
        {
            case NanoPicStartupMode.SmokeTest:
                RunSmokeTest(decision);
                return;

            case NanoPicStartupMode.ComEmbedding:
                StartComEmbedding();
                return;

            case NanoPicStartupMode.Invalid:
                Log("WARN", decision.Diagnostic ?? "启动参数无效。", null);
                Shutdown(decision.ExitCode);
                return;

            default:
                StartNormalWindow();
                return;
        }
    }

    private void StartNormalWindow()
    {
        // 先让 broker 选举与端点就绪，再创建窗口：窗口初始化（设置加载、能力探测）
        // 不应推迟 Explorer 激活所需的 class object 注册。
        var host = CreateShellHost();
        host?.StartNormalInstance();
        var window = new MainWindow(host);
        MainWindow = window;
        window.Show();
    }

    /// <summary>
    /// Explorer 通过 COM 激活：先初始化 STA COM Server 与 class factory，不显示空白窗口。
    /// 只有在必须本地接管请求时才创建正常窗口，并转为普通 UI 生命周期。
    /// </summary>
    private void StartComEmbedding()
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var host = CreateShellHost();
        if (host is null)
        {
            Log("ERROR", "COM 初始化失败：无法创建 Shell 集成宿主。", null);
            Shutdown(3);
            return;
        }

        host.EnsureWindow = CreateWindowForShellRequest;
        host.EmbeddingIdle += (_, _) => ShutdownIfNoWindows();
        host.StartEmbedding();

        // 没有任何激活到达时不长期驻留隐藏进程。
        _embeddingIdleTimer = new DispatcherTimer { Interval = EmbeddingIdleTimeout };
        _embeddingIdleTimer.Tick += (_, _) =>
        {
            _embeddingIdleTimer?.Stop();
            ShutdownIfNoWindows();
        };
        _embeddingIdleTimer.Start();
    }

    private bool CreateWindowForShellRequest()
    {
        try
        {
            _embeddingIdleTimer?.Stop();
            ShutdownMode = ShutdownMode.OnLastWindowClose;
            if (MainWindow is not null)
            {
                return true;
            }

            var window = new MainWindow(_shellHost);
            MainWindow = window;
            window.Show();
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            Log("ERROR", $"为 Shell 请求创建窗口失败：{exception.GetType().Name}: {exception.Message}", exception);
            return false;
        }
    }

    private void ShutdownIfNoWindows()
    {
        if (Windows.OfType<Window>().Any(window => window.IsVisible))
        {
            return;
        }

        _shellHost?.Dispose();
        _shellHost = null;
        Shutdown(0);
    }

    private ShellIntegrationHost? CreateShellHost()
    {
        try
        {
            var executablePath = Assembly.GetEntryAssembly()?.Location ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                Log("WARN", "无法确定可执行文件路径，右键菜单集成不可用。", null);
                return null;
            }

            var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0";
            _shellHost = new ShellIntegrationHost(Dispatcher, executablePath!, version, (message, exception) => Log("INFO", message, exception));
            return _shellHost;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or InvalidOperationException or System.Security.SecurityException)
        {
            Log("ERROR", "初始化 Shell 集成宿主失败。", exception);
            return null;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shellHost?.Dispose();
        _shellHost = null;
        base.OnExit(e);
    }

    private void Log(string level, string message, Exception? exception)
    {
        try
        {
            _logger ??= new RedactingFileLogger(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NanoPic", "logs", "NanoPic.log"));
            _logger.WriteAsync(level, message, exception, System.Threading.CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception loggingException) when (loggingException is IOException or UnauthorizedAccessException)
        {
        }
    }

    private async void HandleDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        try
        {
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NanoPic", "logs", "NanoPic.log");
            var logger = new RedactingFileLogger(logPath);
            await logger.WriteAsync("ERROR", $"界面发生未处理异常：{e.Exception.GetType().Name}。{e.Exception.Message}", e.Exception, System.Threading.CancellationToken.None);
        }
        catch
        {
        }

        System.Windows.MessageBox.Show(
            $"操作未能完成：{e.Exception.Message}\n\n详细信息已写入日志。",
            "NanoPic",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private void RunSmokeTest(StartupModeDecision decision)
    {
        try
        {
            var request = new ImageFileProcessRequest(
                decision.SmokeTestInput!,
                decision.SmokeTestOutput!,
                new ImageEncodingOptions(ImageOutputFormat.Original, Quality: 80),
                new ImageTransformOptions(),
                ImageSafetyLimits.Default,
                OutputConflictPolicy.Overwrite);
            var result = new ImageFileProcessingService(new WicImageCodec())
                .ProcessAsync(request, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            Shutdown(result.IsSuccess ? 0 : 2);
        }
        catch
        {
            Shutdown(3);
        }
    }
}

using System.IO;
using System.Windows;
using NanoPic.Codecs;
using NanoPic.Core;
using NanoPic.Infrastructure;

namespace NanoPic.App;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", isEnabled: false);
        AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", isEnabled: false);
        DispatcherUnhandledException += HandleDispatcherUnhandledException;
        base.OnStartup(e);

        if (e.Args.Length > 0 && string.Equals(e.Args[0], "--smoke-test", StringComparison.OrdinalIgnoreCase))
        {
            RunSmokeTest(e.Args);
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
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

    private void RunSmokeTest(string[] arguments)
    {
        if (arguments.Length != 3)
        {
            Shutdown(64);
            return;
        }

        try
        {
            var request = new ImageFileProcessRequest(
                arguments[1],
                arguments[2],
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

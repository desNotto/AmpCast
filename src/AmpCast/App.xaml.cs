using System;
using System.Linq;
using System.Runtime;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using AmpCast.Setup;

namespace AmpCast;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Installer maintenance commands (--register-asio, --install-driver, ...)
        int code = CommandLine.Run(e.Args);
        if (code != CommandLine.NotACommand)
        {
            Shutdown(code);
            return;
        }

        // Developer helper: AmpCast.exe --render-preview out.png
        if (e.Args.Length >= 2 && e.Args[0] == "--render-preview")
        {
            RenderPreview(e.Args[1]);
            return;
        }

        _singleInstance = new Mutex(true, @"Local\AmpCast_SingleInstance", out bool firstInstance);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\AmpCast_Show");
        if (!firstInstance)
        {
            _showEvent.Set();   // bring the running instance to front
            Shutdown(0);
            return;
        }

        // The Discord path is allocation-free; this keeps blocking gen2 GCs away anyway.
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

        var window = new MainWindow(demo: false);
        MainWindow = window;
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent,
            (_, _) => Dispatcher.InvokeAsync(window.ShowFromTray), null, Timeout.Infinite, false);

        if (e.Args.Contains("--minimized"))
            window.StartInTray();
        else
            window.Show();
    }

    private void RenderPreview(string path)
    {
        var window = new MainWindow(demo: true)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -30000,
            Top = -30000,
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        window.ContentRendered += async (_, _) =>
        {
            await Task.Delay(1200);
            try { window.SavePreview(path); }
            finally { Shutdown(0); }
        };
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showWait?.Unregister(null);
        _showEvent?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}

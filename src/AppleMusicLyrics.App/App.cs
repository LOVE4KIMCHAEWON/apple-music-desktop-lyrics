using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using AppleMusicLyrics.Core;

namespace AppleMusicLyrics.App;

public sealed class App : Application
{
    private MainController? _controller;
    private Mutex? _mutex;
    private InstanceActivationServer? _activation;
    private bool _pendingSettings;

    private void RequestSettings()
    {
        if (_controller is null) { _pendingSettings = true; return; }
        _pendingSettings = false;
        _controller.ShowSettings();
    }

    [STAThread]
    public static int Main(string[] args)
    {
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppleMusicDesktopLyrics");
        var portableDirectory = Path.Combine(AppContext.BaseDirectory, "data");
        var languageMarker = Path.Combine(AppContext.BaseDirectory, "ui-language.txt");
        if (File.Exists(Path.Combine(portableDirectory, "settings.json")) || File.Exists(languageMarker)) dataDirectory = portableDirectory;
        string? requestedLanguage = null;
        var smokeSeconds = 0;
        var preview = false;
        var displayAudit = false;
        try
        {
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--data-dir" when i + 1 < args.Length: dataDirectory = Path.GetFullPath(args[++i]); break;
                    case "--smoke-seconds" when i + 1 < args.Length: smokeSeconds = int.Parse(args[++i]); break;
                    case "--preview": preview = true; break;
                    case "--display-audit": displayAudit = true; break;
                    case "--settings": app._pendingSettings = true; break;
                    case "--language" when i + 1 < args.Length:
                        requestedLanguage = UiText.NormalizeLanguage(args[++i]) ?? throw new ArgumentException("Supported interface languages: en-US, zh-CN.");
                        UiText.Language = requestedLanguage;
                        break;
                    default: throw new ArgumentException("Unknown or incomplete argument: " + args[i]);
                }
            }
            var savedLanguage = new SettingsStore(dataDirectory).Load().UiLanguage;
            string? portableLanguage = null;
            try { if (File.Exists(languageMarker) && new FileInfo(languageMarker).Length <= 32) portableLanguage = File.ReadAllText(languageMarker).Trim(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            UiText.Language = UiText.ResolveLanguage(requestedLanguage, savedLanguage, portableLanguage);
            // Separate data directories are independent instances, useful for portable testing.
            var instanceName = InstanceActivationServer.BuildName(dataDirectory, System.Diagnostics.Process.GetCurrentProcess().SessionId);
            app._mutex = new Mutex(true, @"Local\" + instanceName, out var created);
            if (!created)
            {
                app._mutex.Dispose(); app._mutex = null;
                if (InstanceActivationServer.SendSettingsAsync(instanceName, TimeSpan.FromSeconds(2)).GetAwaiter().GetResult()) return 0;
                if (smokeSeconds <= 0)
                    MessageBox.Show(UiText.T("An existing lyrics instance could not be contacted. Exit it from the system tray, then start again.\n\nData folder: ") + dataDirectory,
                        UiText.T("Apple Music Desktop Lyrics"), MessageBoxButton.OK, MessageBoxImage.Information);
                return 2;
            }
            Directory.CreateDirectory(dataDirectory);
            AppLog.Initialize(dataDirectory);
            app.DispatcherUnhandledException += (_, e) => { AppLog.Write(e.Exception.ToString()); e.Handled = true; };
            TaskScheduler.UnobservedTaskException += (_, e) => { AppLog.Write(e.Exception.ToString()); e.SetObserved(); };
            app._activation = new InstanceActivationServer(instanceName, () =>
            {
                if (!app.Dispatcher.HasShutdownStarted) _ = app.Dispatcher.BeginInvoke(new Action(app.RequestSettings));
            });
            app._activation.Start();
            app.Startup += async (_, _) =>
            {
                try
                {
                    app._controller = new MainController(dataDirectory);
                    await app._controller.StartAsync(preview);
                    if (app._pendingSettings) _ = app.Dispatcher.BeginInvoke(new Action(app.RequestSettings));
                    if (smokeSeconds > 0)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(smokeSeconds));
                        if (displayAudit) await app._controller.CaptureDisplayAuditAsync();
                        app._controller.WriteDiagnostics();
                        app.Shutdown();
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Write(ex.ToString());
                    if (smokeSeconds <= 0) MessageBox.Show(UiText.T("Could not start lyrics.\n\n") + ex.Message + UiText.T("\n\nLog folder: ") + dataDirectory,
                        UiText.T("Apple Music Desktop Lyrics"), MessageBoxButton.OK, MessageBoxImage.Error);
                    app.Shutdown(1);
                }
            };
            app.Exit += (_, _) =>
            {
                try { app._activation?.Dispose(); app._controller?.Dispose(); }
                finally { app._mutex?.ReleaseMutex(); app._mutex?.Dispose(); }
            };
            return app.Run();
        }
        catch (Exception ex)
        {
            try { Directory.CreateDirectory(dataDirectory); File.AppendAllText(Path.Combine(dataDirectory, "startup-error.log"), ex + Environment.NewLine); }
            catch (Exception logException) when (logException is IOException or UnauthorizedAccessException) { }
            app._activation?.Dispose();
            app._mutex?.Dispose();
            if (smokeSeconds <= 0) MessageBox.Show(UiText.T("Could not start lyrics.\n\n") + ex.Message + UiText.T("\n\nLog folder: ") + dataDirectory,
                UiText.T("Apple Music Desktop Lyrics"), MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }
}

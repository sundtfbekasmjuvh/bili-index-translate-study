using System.IO;
using System.Windows;
using System.Windows.Threading;
using BiliIndexTranslate.App.Startup;
using BiliIndexTranslate.Core.Startup;
using Velopack;

namespace BiliIndexTranslate.App;

/// <summary>
/// Entry point. No App.xaml: the Application object is created by hand so that the single-instance
/// check runs before any WPF window or tray icon exists.
/// </summary>
public static class Program
{
    public const string MutexName = @"Local\BiliIndexTranslate.SingleInstance";
    public const string ActivateEventName = @"Local\BiliIndexTranslate.Activate";

    /// <summary>%LocalAppData%\BiliIndexTranslate, or the directory given with <c>--data-dir</c>.</summary>
    public static string DataDirectory { get; private set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BiliIndexTranslate");

    /// <summary>Name of the activation event; equals <see cref="ActivateEventName"/> unless <c>--data-dir</c> was given.</summary>
    public static string ActivateName { get; private set; } = ActivateEventName;

    [STAThread]
    public static int Main(string[] args)
    {
        // Must run first, before the single-instance check: the Velopack installer starts the exe with special
        // arguments (install, update, uninstall); Run() handles them and exits. In a normal start it returns at once.
        // The install directory is %LocalAppData%\BiliIndexTranslate.Desktop (the pack id), apart from DataDirectory,
        // so uninstalling and updating never touch the settings.
        VelopackApp.Build().OnBeforeUninstallFastCallback(_ => AutoStart.RemoveOnUninstall()).Run();

        var options = StartupOptions.Parse(args);
        var mutexName = MutexName;
        if (options.DataDirectory is not null)
        {
            DataDirectory = options.DataDirectory;
            mutexName = StartupOptions.ScopedName(MutexName, DataDirectory);
            ActivateName = StartupOptions.ScopedName(ActivateEventName, DataDirectory);
        }

        using var mutex = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
        if (!createdNew)
        {
            // Second instance: wake the running one (it opens Settings) and leave quietly.
            try
            {
                using var evt = EventWaitHandle.OpenExisting(ActivateName);
                evt.Set();
            }
            catch (WaitHandleCannotBeOpenedException) { }
            return 0;
        }

        Directory.CreateDirectory(DataDirectory);
        System.Windows.Forms.Application.EnableVisualStyles(); // tray menu only

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/BiliIndexTranslate;component/Themes/Base.xaml"),
        });

        AppHost? host = null;
        var crashCount = 0;
        app.DispatcherUnhandledException += (_, e) =>
        {
            WriteCrash(e.Exception);
            host?.ReportCrash(e.Exception);
            // Keep running after an isolated UI exception; give up on a storm.
            e.Handled = ++crashCount < 5;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrash(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { WriteCrash(e.Exception); e.SetObserved(); };

        host = new AppHost(app, DataDirectory, options.Welcome);
        app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, host.Start);
        var code = app.Run();
        host.Dispose();
        mutex.ReleaseMutex();
        return code;
    }

    private static void WriteCrash(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            File.AppendAllText(Path.Combine(DataDirectory, "crash.log"),
                $"{DateTimeOffset.Now:O} {ex.GetType().FullName}: {ex.Message}{Environment.NewLine}{ex.StackTrace}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception) { }
    }
}

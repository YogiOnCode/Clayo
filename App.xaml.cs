using System.IO;
using System.Windows;
using CcxShell.Core;

namespace CcxShell;

public partial class App : Application
{
    private SingleInstance? _instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) => LogFatal(args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) LogFatal(ex);
        };

        // Every session we start has to be a top-level one, so drop any Claude Code
        // session markers we inherited before a pane can pass them on.
        SessionLauncher.ScrubInheritedSession();

        // Explorer's address bar runs a command with the current folder as its
        // working directory, so this is the folder the user typed "ccx" in.
        // An explicit path argument wins, for launching from a script.
        var folder = e.Args.FirstOrDefault(a => Directory.Exists(a))
                     ?? Environment.CurrentDirectory;

        _instance = new SingleInstance();
        if (!_instance.TryAcquire(folder))
        {
            // Another window has it. We handed the folder over; nothing left to do.
            _instance.Dispose();
            Shutdown();
            return;
        }

        base.OnStartup(e);

        var window = new MainWindow(folder);
        MainWindow = window;

        _instance.FolderReceived += path =>
            window.Dispatcher.BeginInvoke(() => window.AdoptFolder(path));

        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);

        // Leave no process behind. WebView2 and the pty children run threads we do not own,
        // and if any of them is a foreground thread the process outlives its window — which
        // is worse than a crash: the survivor still holds the single-instance mutex, so
        // every later `clayo` in Explorer hands its folder to a window that no longer exists
        // and exits without opening anything. The app then looks permanently broken until
        // the corpse is killed by hand. Nothing meaningful runs after OnExit, so leaving
        // here rather than waiting on those threads costs nothing.
        Environment.Exit(e.ApplicationExitCode);
    }

    /// <summary>
    /// Without this, a failure on the UI thread takes the window down leaving no record of
    /// why — and by the time you notice, the window is gone and there is nothing to inspect.
    /// Writes the exception next to the other state we keep and lets the crash proceed:
    /// swallowing it would leave a half-dead window, which is what we are trying to avoid.
    /// </summary>
    private static void LogFatal(Exception ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clayo");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

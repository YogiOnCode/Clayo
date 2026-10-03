using System.IO;
using System.Windows;
using CcxShell.Core;

namespace CcxShell;

public partial class App : Application
{
    private SingleInstance? _instance;
    private IslandWindow? _island;

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

        // Started at login: no window, only the island, until you ask for Clayo.
        bool background = e.Args.Contains(LoginStartup.BackgroundArg);

        // Explorer's address bar runs a command with the current folder as its
        // working directory, so this is the folder the user typed "ccx" in.
        // An explicit path argument wins, for launching from a script.
        var folder = e.Args.FirstOrDefault(a => Directory.Exists(a))
                     ?? (background ? null : Chosen(Environment.CurrentDirectory));

        _instance = new SingleInstance();
        if (!_instance.TryAcquire(background ? null : folder ?? ""))
        {
            // Another window has it. We handed the folder over; nothing left to do.
            _instance.Dispose();
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // With no folder there is nothing to start a session in; the window opens on your
        // home folder for the next New session.
        var window = new MainWindow(
            folder ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            startSession: folder is not null);
        MainWindow = window;

        _instance.FolderReceived += path => window.Dispatcher.BeginInvoke(() =>
        {
            if (path.Length == 0) window.Reveal();
            else window.AdoptFolder(path);
        });

        // Signing out ends the app as a real quit would, panes closed, rather than meeting a
        // close that only hides.
        SessionEnding += (_, _) => window.Quit();

        // Reveal, not Show: until setup is done the setup window comes first (docs/SETUP.md D6).
        if (!background) window.Reveal();

        // After MainWindow is set, so this window does not become the one whose closing ends
        // the app. ShutdownMode is OnMainWindowClose, so it never keeps the process alive.
        _island = new IslandWindow(window);
        if (background) _island.Greet();

        // Only the per-user Run key, and only from a Release build (see LoginStartup.CanWrite).
        // The installed exe re-registers itself on every start unless you turned it off.
        if (Environment.ProcessPath is { } exe) LoginStartup.ForThisUser().Keep(exe);
    }

    /// <summary>
    /// The working directory, unless nobody chose it: a shortcut or the Start menu starts
    /// Clayo in its own folder, and some launchers in System32. A session there is never
    /// what you wanted.
    /// </summary>
    private static string? Chosen(string cwd)
    {
        static string Norm(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));
        var dir = Norm(cwd);
        return dir.Equals(Norm(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase)
            || dir.Equals(Norm(Environment.SystemDirectory), StringComparison.OrdinalIgnoreCase)
            ? null : cwd;
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

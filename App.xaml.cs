using System.IO;
using System.Windows;
using CcxShell.Core;

namespace CcxShell;

public partial class App : Application
{
    private SingleInstance? _instance;

    protected override void OnStartup(StartupEventArgs e)
    {
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
    }
}

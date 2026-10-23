using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Terminal.Avalonia;
using Terminal.Pty;

// TermSpike [--lib tav|ice] [--panes N] [--cmd exe] [--root folder]
// N panes, each running `cmd` in its own folder under root (pane1, pane2, ...). One shows at a time,
// like Clayo; the buttons on the left switch, with AutomationIds pane1.. for driving it from a script.
// --lib picks the terminal control: tav = Terminal.Avalonia, ice = Iciclecreek.Avalonia.Terminal.
static class Program
{
    // STA, as Avalonia's templates have: without it the Windows clipboard fails (CO_E_NOTINITIALIZED).
    [STAThread]
    static void Main(string[] args) =>
        AppBuilder.Configure<SpikeApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
}

sealed class SpikeApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = Spike.Window(desktop.Args ?? []);
        base.OnFrameworkInitializationCompleted();
    }
}

static class Spike
{
    static string Arg(string[] args, string name, string fallback)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
    }

    public static Window Window(string[] args)
    {
        var count = int.Parse(Arg(args, "--panes", "4"));
        var cmd = Arg(args, "--cmd", "powershell.exe");
        var root = Arg(args, "--root", Path.Combine(Path.GetTempPath(), "termspike"));
        var lib = Arg(args, "--lib", "tav");

        var buttons = new StackPanel { Width = 120, Spacing = 4, Margin = new Thickness(8) };
        var host = new Grid();
        var panes = new List<Control>();

        void Show(int index)
        {
            for (int i = 0; i < panes.Count; i++) panes[i].IsVisible = i == index;
            // Iciclecreek's control isn't focusable itself: the terminal view inside it takes the keys.
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                (Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(panes[index])
                    .OfType<Avalonia.Input.InputElement>().FirstOrDefault(e => e.Focusable) ?? panes[index]).Focus(),
                Avalonia.Threading.DispatcherPriority.Loaded);
        }

        for (int i = 0; i < count; i++)
        {
            var folder = Directory.CreateDirectory(Path.Combine(root, $"pane{i + 1}")).FullName;
            // The shell starts in the folder and runs cmd there (Terminal.Avalonia has no working
            // directory). Encoded, so cmd's spaces and quotes reach PowerShell as one script.
            var script = $"Set-Location -LiteralPath '{folder}'; {cmd}";
            string[] shellArgs = ["-NoLogo", "-NoExit", "-EncodedCommand", Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script))];
            var font = new FontFamily("Cascadia Code, Cascadia Mono, Consolas");

            Control term;
            if (lib == "ice")
            {
                // Launches itself once loaded, from these three.
                term = new Iciclecreek.Terminal.TerminalControl
                {
                    FontFamily = font, FontSize = 13, IsVisible = false,
                    Process = "powershell.exe", ProcessArgs = shellArgs, StartingDirectory = folder,
                    // Ctrl+V pastes and Ctrl+C copies a selection, as Clayo's panes do today.
                    ShortcutMode = Iciclecreek.Terminal.ShortcutMode.Desktop,
                };
                host.Children.Add(term);
            }
            else
            {
                var tav = new TerminalControl { FontFamily = font, FontSize = 13, IsVisible = false };
                host.Children.Add(tav);
                tav.Start(new PtyOptions { Command = "powershell.exe", Arguments = shellArgs, Columns = 120, Rows = 30 });
                term = tav;
            }
            panes.Add(term);

            var index = i;
            var button = new Button { Content = $"pane{i + 1}", HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetAutomationId(button, $"pane{i + 1}");
            button.Click += (_, _) => Show(index);
            buttons.Children.Add(button);
        }

        var layout = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Left);
        layout.Children.Add(buttons);
        layout.Children.Add(host);

        var window = new Window { Title = "TermSpike", Width = 1280, Height = 800, Content = layout };
        window.Opened += (_, _) => Show(count - 1);
        return window;
    }
}

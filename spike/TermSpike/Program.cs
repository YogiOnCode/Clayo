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
// On a Mac (M2b) it's always ice, each pane is the login shell (then `cmd`, if given), and two test
// pills show under the menu bar: see Pill.
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
        var mac = OperatingSystem.IsMacOS();
        var count = int.Parse(Arg(args, "--panes", "4"));
        var cmd = Arg(args, "--cmd", mac ? "" : "powershell.exe");
        var root = Arg(args, "--root", Path.Combine(Path.GetTempPath(), "termspike"));
        var lib = mac ? "ice" : Arg(args, "--lib", "tav");

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
            var font = new FontFamily(mac ? "Menlo" : "Cascadia Code, Cascadia Mono, Consolas");
            var process = "powershell.exe";
            if (mac)
            {
                // The login shell (D8), so PATH has what Terminal.app's has. A command runs first,
                // then the shell stays, as -NoExit does on Windows.
                process = Environment.GetEnvironmentVariable("SHELL") ?? "/bin/zsh";
                shellArgs = cmd == "" ? ["-l"] : ["-l", "-c", $"{cmd}; exec {process} -l"];
            }

            Control term;
            if (lib == "ice")
            {
                // Launches itself once loaded, from these three.
                term = new Iciclecreek.Terminal.TerminalControl
                {
                    FontFamily = font, FontSize = 13, IsVisible = false,
                    Process = process, ProcessArgs = shellArgs, StartingDirectory = folder,
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
        if (mac)
        {
            var pills = new Button { Content = "Show pills", HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 16, 0, 0) };
            pills.Click += (_, _) => Pill.ShowBoth(window);
            buttons.Children.Add(pills);
            // Also by itself after 5 s, so it can appear while you type in another app.
            window.Opened += (_, _) => Avalonia.Threading.DispatcherTimer.RunOnce(() => Pill.ShowBoth(window), TimeSpan.FromSeconds(5));
        }
        return window;
    }
}

// The Mac pill test (M2b): two ways to a pill under the menu bar that never takes focus. Both float
// over other apps, on every Space and over full-screen apps; the tester says which one kept the typing.
// A: an ordinary Avalonia window (an NSWindow), shown without activating.
// B: an Avalonia popup, which on the Mac is an NSPanel (AvnPanel), made non-activating.
static class Pill
{
    static Window? a;
    static Avalonia.Controls.Primitives.Popup? b;

    public static void ShowBoth(Window owner)
    {
        var screen = owner.Screens.Primary ?? owner.Screens.All[0];
        var area = screen.WorkingArea; // below the menu bar, in pixels
        var scale = screen.Scaling;
        const double w = 260, h = 34;
        var x = area.X + (area.Width - w * scale) / 2;
        var y = area.Y + 6 * scale;

        a ??= new Window
        {
            Width = w, Height = h, CanResize = false, Topmost = true, ShowActivated = false,
            ShowInTaskbar = false, WindowDecorations = WindowDecorations.None,
            Background = Brushes.Transparent, TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
            Content = Body("A · window"),
        };
        a.Position = new PixelPoint((int)x, (int)y);
        a.Show();
        Float(a, panel: false);

        // B sits under A. A popup is placed relative to its owner, so turn the screen point into one.
        var origin = owner.PointToScreen(new Point(0, 0));
        b ??= new Avalonia.Controls.Primitives.Popup
        {
            PlacementTarget = owner, Placement = PlacementMode.AnchorAndGravity,
            PlacementAnchor = Avalonia.Controls.Primitives.PopupPositioning.PopupAnchor.TopLeft,
            PlacementGravity = Avalonia.Controls.Primitives.PopupPositioning.PopupGravity.BottomRight,
            PlacementConstraintAdjustment = Avalonia.Controls.Primitives.PopupPositioning.PopupPositionerConstraintAdjustment.None,
            IsLightDismissEnabled = false, Topmost = true, Child = Body("B · panel"),
        };
        if (b.Parent is null) ((Panel)((DockPanel)owner.Content!).Children[0]).Children.Add(b);
        b.PlacementRect = new Rect((x - origin.X) / scale, (y + (h + 8) * scale - origin.Y) / scale, 1, 1);
        b.IsOpen = true;
        if (TopLevel.GetTopLevel(b.Child) is { } top) Float(top, panel: true); // the popup's own top level, its panel
    }

    // A pill: rounded, dark, and counts clicks, so the tester sees a click arrive.
    static Control Body(string name)
    {
        var clicks = 0;
        var text = new TextBlock { Text = $"{name} · click me", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        var pill = new Border { Width = 260, Height = 34, CornerRadius = new CornerRadius(17), Background = new SolidColorBrush(Color.FromRgb(0x1c, 0x1c, 0x1e)), Child = text };
        pill.PointerPressed += (_, _) => text.Text = $"{name} · clicks: {++clicks}";
        return pill;
    }

    // Above other apps (status level), on every Space and beside full-screen apps, kept when the app
    // isn't active. A panel also gets the non-activating style, so clicking it leaves focus alone.
    static void Float(TopLevel top, bool panel)
    {
        if (top.TryGetPlatformHandle() is not Avalonia.Platform.IMacOSTopLevelPlatformHandle handle) return;
        var win = handle.NSWindow;
        if (win == IntPtr.Zero) return;
        ObjC.Send(win, "setLevel:", 25L); // NSStatusWindowLevel
        ObjC.Send(win, "setCollectionBehavior:", (1UL << 0) | (1UL << 4) | (1UL << 8)); // all Spaces, stationary, full-screen auxiliary
        ObjC.Send(win, "setHidesOnDeactivate:", false);
        if (!panel) return;
        ObjC.Send(win, "setStyleMask:", ObjC.SendU(win, "styleMask") | (1UL << 7)); // NSWindowStyleMaskNonactivatingPanel
        if (ObjC.Responds(win, "setBecomesKeyOnlyIfNeeded:")) ObjC.Send(win, "setBecomesKeyOnlyIfNeeded:", true);
        if (ObjC.Responds(win, "setFloatingPanel:")) ObjC.Send(win, "setFloatingPanel:", true);
    }
}

static class ObjC
{
    const string Lib = "/usr/lib/libobjc.A.dylib";
    [System.Runtime.InteropServices.DllImport(Lib)] static extern IntPtr sel_registerName(string name);
    [System.Runtime.InteropServices.DllImport(Lib, EntryPoint = "objc_msgSend")] static extern ulong MsgU(IntPtr o, IntPtr sel);
    [System.Runtime.InteropServices.DllImport(Lib, EntryPoint = "objc_msgSend")] static extern void MsgL(IntPtr o, IntPtr sel, long v);
    [System.Runtime.InteropServices.DllImport(Lib, EntryPoint = "objc_msgSend")] static extern void MsgUL(IntPtr o, IntPtr sel, ulong v);
    [System.Runtime.InteropServices.DllImport(Lib, EntryPoint = "objc_msgSend")] static extern void MsgB(IntPtr o, IntPtr sel, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.I1)] bool v);
    [System.Runtime.InteropServices.DllImport(Lib, EntryPoint = "objc_msgSend")] [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.I1)] static extern bool MsgSelB(IntPtr o, IntPtr sel, IntPtr arg);

    public static ulong SendU(IntPtr o, string sel) => MsgU(o, sel_registerName(sel));
    public static void Send(IntPtr o, string sel, long v) => MsgL(o, sel_registerName(sel), v);
    public static void Send(IntPtr o, string sel, ulong v) => MsgUL(o, sel_registerName(sel), v);
    public static void Send(IntPtr o, string sel, bool v) => MsgB(o, sel_registerName(sel), v);
    public static bool Responds(IntPtr o, string sel) => MsgSelB(o, sel_registerName("respondsToSelector:"), sel_registerName(sel));
}

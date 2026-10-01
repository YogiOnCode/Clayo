using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CcxShell.Core;
using CcxShell.UI;

namespace CcxShell;

/// <summary>
/// The island at the top of the screen. While hidden there is no window at the edge at all —
/// a catcher strip would sit over a maximized browser's tab bar — so the cursor is polled
/// instead and IslandTrigger decides. It never takes focus; only a click hands focus to the
/// main window.
/// </summary>
public partial class IslandWindow : Window
{
    // Logical px. The extra height is slack for the spring overshoot, see the XAML.
    private const double IslandWidth = 300;
    private const double IslandHeight = 60;

    private readonly MainWindow _main;
    private readonly IslandTrigger _trigger = new();
    private readonly DispatcherTimer _poll;
    private readonly DispatcherTimer _waveStop;

    private bool _busy;
    // Not long.MinValue: now - MinValue overflows negative and the check would never run.
    // TickCount64 is never below zero, so this makes the first tick check.
    private long _busyCheckedAt = -1000;
    private bool _escWasDown;

    /// <summary>Where the island was last put, in physical px, for the over-the-island test.</summary>
    private PxRect _placed;

    public IslandWindow(MainWindow main)
    {
        InitializeComponent();
        _main = main;
        Width = IslandWidth;
        Height = IslandHeight;
        Slide.Y = -IslandHeight;

        // The handle is needed to position the window before its first Show, and creating it
        // here runs OnSourceInitialized so the extended styles are in place before it is ever
        // visible — a single activated frame would already have stolen focus.
        new WindowInteropHelper(this).EnsureHandle();

        _poll = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background,
                                    Tick, Dispatcher);

        // Hello on each peek, then back to breathing, as the prototype does.
        _waveStop = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
        _waveStop.Tick += (_, _) => { _waveStop.Stop(); Mascot.Play(MascotMove.Idle); };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // NOACTIVATE keeps typing going into the browser while the island shows, including
        // when it is clicked. TOOLWINDOW keeps it out of Alt+Tab.
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowLong(hwnd, GWL_EXSTYLE,
            GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    protected override void OnClosed(EventArgs e)
    {
        // Shutdown closes this window while the dispatcher can still run a tick, and Show on a
        // closed window throws.
        _poll.Stop();
        _waveStop.Stop();
        base.OnClosed(e);
    }

    // ------------------------------------------------------------------- poll

    private void Tick(object? sender, EventArgs e)
    {
        // Fails on the secure desktop (lock screen, UAC prompt). Nothing to decide there.
        if (!GetCursorPos(out var pt)) return;

        var monitor = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return;
        double scale = GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var dpi, out _) == 0
            ? dpi / 96.0 : 1.0;
        var bounds = new PxRect(info.rcMonitor.Left, info.rcMonitor.Top,
                                info.rcMonitor.Right, info.rcMonitor.Bottom);

        long now = Environment.TickCount64;

        // The only call here that is not trivially cheap: it asks the shell. Once a second is
        // plenty for something that changes when a game or slideshow starts.
        if (now - _busyCheckedAt >= 1000)
        {
            _busy = IsBusy();
            _busyCheckedAt = now;
        }

        // Edge-detected so an Esc held down from before the peek does not count. The key still
        // reaches the foreground app: swallowing it would need a keyboard hook, and taking focus
        // to receive it is exactly what the island must not do.
        bool escDown = (GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0;
        bool escPressed = escDown && !_escWasDown;
        _escWasDown = escDown;

        var was = _trigger.State;
        if (was == IslandState.Peek && escPressed) _trigger.Dismiss();

        var state = _trigger.Update(new IslandInput(
            pt.X, pt.Y, bounds, scale,
            ButtonDown: PrimaryButtonDown(),
            MovingOrSizing: InMoveSize(),
            // With Clayo itself in front the sidebar already shows every session, so the
            // island would only repeat it. Behind the browser it is needed again.
            Busy: _busy || _main.IsActive,
            OverIsland: IsVisible && _placed.Contains(pt.X, pt.Y),
            NowMs: now));

        if (state == was) return;
        if (state == IslandState.Peek) SlideIn(bounds, scale);
        else SlideOut();
    }

    /// <summary>
    /// GetAsyncKeyState reads the physical buttons, so with the buttons swapped the one that
    /// drags windows is VK_RBUTTON.
    /// </summary>
    private static bool PrimaryButtonDown()
    {
        int vk = GetSystemMetrics(SM_SWAPBUTTON) != 0 ? VK_RBUTTON : VK_LBUTTON;
        return (GetAsyncKeyState(vk) & 0x8000) != 0;
    }

    private static bool InMoveSize()
    {
        // Thread 0 means the foreground thread, which is the one doing any window drag.
        var gti = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        return GetGUIThreadInfo(0, ref gti) && (gti.flags & GUI_INMOVESIZE) != 0;
    }

    private static bool IsBusy()
    {
        if (SHQueryUserNotificationState(out var state) != 0) return false;
        return state is QUNS_BUSY or QUNS_RUNNING_D3D_FULL_SCREEN
                     or QUNS_PRESENTATION_MODE or QUNS_QUIET_TIME;
    }

    // -------------------------------------------------------------- show/hide

    private void SlideIn(PxRect monitor, double scale)
    {
        int w = (int)Math.Round(IslandWidth * scale);
        int h = (int)Math.Round(IslandHeight * scale);
        int x = (monitor.Left + monitor.Right) / 2 - w / 2;
        _placed = new PxRect(x, monitor.Top, x + w, monitor.Top + h);

        int n = _main.OpenSessionCount;
        Sub.Text = n == 1 ? "1 session" : $"{n} sessions";

        // Placed by hand in physical px: Left/Top are in the units of whichever monitor the
        // window was last on, which is the wrong monitor whenever the cursor has moved to a
        // screen at a different scale. Placed again after Show because crossing to such a
        // screen raises WM_DPICHANGED, and WPF answers that by moving the window to the rect
        // Windows suggests rather than where we put it.
        Place();
        if (!IsVisible) Show();
        Place();

        Mascot.Play(MascotMove.Wave);
        _waveStop.Stop();
        _waveStop.Start();

        Slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(450))
        {
            // The prototype's spring, cubic-bezier(.34,1.4,.64,1): out with a slight overshoot.
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 },
        });
    }

    private void SlideOut()
    {
        var anim = new DoubleAnimation(-IslandHeight, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        };
        // A peek can start again while this is still running; then the window must stay.
        anim.Completed += (_, _) =>
        {
            if (_trigger.State == IslandState.Hidden) Hide();
        };
        Slide.BeginAnimation(TranslateTransform.YProperty, anim);
    }

    private void Place()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowPos(hwnd, IntPtr.Zero, _placed.Left, _placed.Top,
                     _placed.Right - _placed.Left, _placed.Bottom - _placed.Top,
                     SWP_NOZORDER | SWP_NOACTIVATE);
    }

    private void Pill_Click(object sender, MouseButtonEventArgs e)
    {
        _trigger.Dismiss();
        SlideOut();

        // The click was the last input event and it went to this process, so Windows allows
        // the foreground change. RestoreWindow rather than WindowState = Normal, so a window
        // that was maximized before it was minimized comes back maximized.
        if (_main.WindowState == WindowState.Minimized) SystemCommands.RestoreWindow(_main);
        _main.Show();
        _main.Activate();
    }

    // ---------------------------------------------------------------- interop

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public RECT rcCaret;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT point, int flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vk);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    private const int MONITOR_DEFAULTTONEAREST = 2;
    private const int MDT_EFFECTIVE_DPI = 0;

    private const int VK_LBUTTON = 0x01;
    private const int VK_RBUTTON = 0x02;
    private const int VK_ESCAPE = 0x1B;
    private const int SM_SWAPBUTTON = 23;

    private const int GUI_INMOVESIZE = 0x0002;

    private const int QUNS_BUSY = 2;
    private const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
    private const int QUNS_PRESENTATION_MODE = 4;
    private const int QUNS_QUIET_TIME = 6;

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
}

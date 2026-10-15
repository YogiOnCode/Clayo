using System.IO;
using System.Runtime.InteropServices;
using System.Text;
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
    // Logical px. A peek is the prototype's 's' pill, a notification its 'm' one, which has
    // room for a session name. The window is always the wider one; see the XAML. The extra
    // height is slack for the spring overshoot.
    private const double PeekWidth = 300;
    // The compact pill: the mascot and its status dot with the header's padding, nothing more.
    private const double CompactWidth = 92;
    private const double NoteWidth = 420;
    private const double IslandHeight = 60;
    // While the drop target shows: the header, the 110 px target and its padding, and the
    // spring's slack. The rest of the time the window is only as tall as the header, so the
    // slack below it never reaches far over the page.
    private const double TallHeight = 190;

    private readonly MainWindow _main;
    private readonly IslandTrigger _trigger = new();
    private readonly IslandGlow _glow = new();
    private readonly DispatcherTimer _poll;

    // The second half of a two-part pose: wave then idle, alert then talk.
    private readonly DispatcherTimer _poseTimer;
    private MascotMove _poseAfter;

    // A note can stay up for hours (a question while you're at lunch). Both windows are layered
    // and redrawn in software every frame, so after a while the island holds still.
    private readonly DispatcherTimer _restTimer = new() { Interval = TimeSpan.FromSeconds(30) };

    // The cursor is read 20 times a second near the top edge, where a peek or a drop can start,
    // and four times a second elsewhere with nothing showing.
    private static readonly TimeSpan PollNear = TimeSpan.FromMilliseconds(50), PollFar = TimeSpan.FromMilliseconds(250);

    // The screenshot picker (docs/SCREENSHOT.md). Ctrl+Alt+S is registered while the setting is
    // on; 1, 2, 3, N and Esc only while the picker shows (D7), since the island never has focus.
    private const int ShotKey = 1, PickKeys = 10;
    private static readonly uint[] PickVks = [0x31, 0x32, 0x33, 0x4E, 0x1B];   // 1 2 3 N Esc
    private bool _shotKey, _pickKeys, _offering, _pickAsked;
    private uint _clipSeq;
    private IReadOnlyList<PickChoice> _choices = [];

    /// <summary>What is on screen, so a new note while showing is noticed as a change.</summary>
    private IslandNote? _shown;
    private PxRect _monitor;
    private double _scale = 1;

    private bool _busy;
    // Not long.MinValue: now - MinValue overflows negative and the check would never run.
    // TickCount64 is never below zero, so this makes the first tick check.
    private long _busyCheckedAt = -1000;
    private bool _escWasDown;

    /// <summary>Where the pill was last put, in physical px, for the over-the-island test.</summary>
    private PxRect _placed;

    /// <summary>The whole window, as wide as the widest pill.</summary>
    private PxRect _window;
    private double _pillWidth = PeekWidth;

    public IslandWindow(MainWindow main)
    {
        InitializeComponent();
        _main = main;
        Width = NoteWidth;
        Height = IslandHeight;
        Slide.Y = -IslandHeight;

        // The handle is needed to position the window before its first Show, and creating it
        // here runs OnSourceInitialized so the extended styles are in place before it is ever
        // visible — a single activated frame would already have stolen focus.
        new WindowInteropHelper(this).EnsureHandle();

        _poll = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background,
                                    Tick, Dispatcher);

        // Hello on each peek, then back to breathing, as the prototype does; a needs-you
        // alerts first and then keeps talking.
        _poseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
        _poseTimer.Tick += (_, _) => { _poseTimer.Stop(); Mascot.Play(_poseAfter); };
        _restTimer.Tick += (_, _) =>
        {
            _restTimer.Stop();
            Mascot.Still = true;
            _glow.Rest();
        };

        // Only queued here; the next poll decides whether it may show (fullscreen, Clayo in front).
        _main.SessionNotice += (pane, name, kind) =>
        {
            if (kind is { } k) _trigger.Notify(new IslandNote(pane, name, k));
            else _trigger.Resolve(pane);
        };
        _main.SessionWorking += (pane, working) => _trigger.SetWorking(pane, working);
        // Keyed by the limit, so a newer warning about it replaces the older one.
        _main.ReserveCrossed += m => _trigger.Notify(new IslandNote(m.Label, $"{m.Label} at {m.Percent}%", NoteKind.Reserve));
        _main.SettingsApplied += ApplyShots;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // NOACTIVATE keeps typing going into the browser while the island shows, including
        // when it is clicked. TOOLWINDOW keeps it out of Alt+Tab.
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowLong(hwnd, GWL_EXSTYLE,
            GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);

        // The hotkeys and clipboard notices arrive as window messages to this handle.
        HwndSource.FromHwnd(hwnd).AddHook(WndProc);
        ApplyShots(_main.Settings);
    }

    protected override void OnClosed(EventArgs e)
    {
        // Shutdown closes this window while the dispatcher can still run a tick, and Show on a
        // closed window throws.
        _poll.Stop();
        _poseTimer.Stop();
        _restTimer.Stop();
        ReleasePickKeys();
        ApplyShots(_main.Settings with { ScreenshotHotkey = false, ScreenshotOffer = false });
        _glow.Retire();
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
        if (was != IslandState.Hidden && escPressed) _trigger.Dismiss();
        // Here, after `was`, so the picker is seen as a change and slides in.
        if (_pickAsked)
        {
            _pickAsked = false;
            _trigger.Pick(now);
        }

        // The pill grows downwards with the drop target, so its height is read again on every
        // poll rather than when it was placed.
        _placed = _placed with
        {
            Bottom = _placed.Top + (int)Math.Round(Math.Max(IslandHeight, Pill.ActualHeight) * _scale),
        };

        var state = _trigger.Update(new IslandInput(
            pt.X, pt.Y, bounds, scale,
            ButtonDown: PrimaryButtonDown(),
            MovingOrSizing: InMoveSize(),
            Busy: _busy,
            // The open menu hangs below the pill; reaching for it must not send the island away.
            OverIsland: IsVisible && (_placed.Contains(pt.X, pt.Y) || Menu.IsOpen),
            NowMs: now,
            // With Clayo itself in front the sidebar already shows every session, so the
            // island would only repeat it. Behind the browser it is needed again.
            ClayoActive: ClayoInFront,
            Dragging: InOleDrag()));

        if (was == IslandState.Pick && state != IslandState.Pick) ReleasePickKeys();

        // A new note while one shows is a change too: same state, different content.
        if (state != was || !ReferenceEquals(_trigger.Note, _shown))
        {
            // A note that replaces another stays on the island's monitor rather than following
            // the cursor to another screen.
            if (state == IslandState.Hidden) SlideOut();
            else if (was == IslandState.Hidden) SlideIn(bounds, scale, fresh: true);
            else SlideIn(_monitor, _scale, fresh: false);
        }

        // The compact pill sits over the browser's tab strip, so clicks go through it to the
        // tabs; resting on it opens the full island, which takes clicks again. Through while
        // sliding out too, so a click meant for a tab cannot land on a pill that is leaving.
        ClickThrough(state is IslandState.Compact or IslandState.Hidden);

        // After SlideIn, so the glow window is already lit when the bar stops needing it.
        _glow.Dwell(bounds, scale, _trigger.DwellProgress, _trigger.DwellLengthMs);

        // Setting the interval restarts the timer, so only on a change.
        var interval = state == IslandState.Hidden && pt.Y - bounds.Top > 200 * scale ? PollFar : PollNear;
        if (_poll.Interval != interval) _poll.Interval = interval;
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

    /// <summary>
    /// An OLE drag (a file from Explorer, a link from the browser) is running on the foreground
    /// thread. While one does, ole32's tracking window, CLIPBRDWNDCLASS, holds the mouse
    /// capture. A window drag holds no capture of that class, and neither does a tab dragged
    /// along the strip or a text selection, which only hold the button down.
    /// </summary>
    private static bool InOleDrag()
    {
        var gti = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        if (!GetGUIThreadInfo(0, ref gti) || gti.hwndCapture == IntPtr.Zero) return false;
        var name = new StringBuilder(32);
        return GetClassName(gti.hwndCapture, name, name.Capacity) > 0
            && name.ToString() == "CLIPBRDWNDCLASS";
    }

    /// <summary>
    /// The shell's word, except for a plain QUNS_BUSY: it also says that with every window
    /// minimized or only the desktop showing, as if the desktop were a fullscreen app, and the
    /// island then never came out on an empty desktop. So that one also needs the foreground
    /// window to be showing and to cover its monitor. The others name what is going on.
    /// </summary>
    private static bool IsBusy()
    {
        if (SHQueryUserNotificationState(out var state) != 0) return false;
        if (state == QUNS_BUSY) return ForegroundFillsMonitor();
        return state is QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE or QUNS_QUIET_TIME;
    }

    private static bool ForegroundFillsMonitor()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd) || IsIconic(hwnd)) return false;
        // The desktop is monitor-sized too.
        var name = new StringBuilder(32);
        if (GetClassName(hwnd, name, name.Capacity) > 0 && name.ToString() is "Progman" or "WorkerW")
            return false;
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetWindowRect(hwnd, out var r)
            || !GetMonitorInfo(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref info)) return false;
        var m = info.rcMonitor;
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
    }

    /// <summary>
    /// Clayo's window is what you are looking at. IsActive alone is not enough: minimized with
    /// nothing else open, it stays the foreground window, and so stays active.
    /// </summary>
    private bool ClayoInFront =>
        _main.IsActive && _main.IsVisible && _main.WindowState != WindowState.Minimized;

    // -------------------------------------------------------------- show/hide

    /// <summary>
    /// Shows the compact pill, the peek or the trigger's note. `fresh` is coming from hidden; otherwise the pill
    /// is already there and only grows, changes its line, pose and light.
    /// </summary>
    private void SlideIn(PxRect monitor, double scale, bool fresh)
    {
        var note = _trigger.Note;
        bool compact = _trigger.State == IslandState.Compact;
        bool drop = _trigger.State == IslandState.Drop;
        bool pick = _trigger.State == IslandState.Pick;
        _shown = note;
        _monitor = monitor;
        _scale = scale;

        double pillWidth = compact ? CompactWidth : note is null && !drop && !pick ? PeekWidth : NoteWidth;

        // Compact shows only the mascot and the dot; the text under it is filled in regardless.
        // The greeting's dot is the warm hello, not the blue of work.
        bool greeting = _trigger.Greeting;
        Dot.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        Dot.Fill = new SolidColorBrush(greeting ? IslandGlow.Warm : IslandGlow.Blue);
        Text.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        // Only a pill with nothing under it opens Clayo on a click.
        DropBody.Visibility = drop ? Visibility.Visible : Visibility.Collapsed;
        PickBody.Visibility = pick ? Visibility.Visible : Visibility.Collapsed;
        Pill.Cursor = drop || pick ? Cursors.Arrow : Cursors.Hand;
        if (pick)
        {
            HeadName.Text = "send screenshot";
            HeadRest.Text = " to…";
            Sub.Text = _choices.Count == 0 ? "N for a new session · Esc to cancel" : $"1–{_choices.Count} or N · Esc to cancel";
            Sub.Visibility = Visibility.Visible;
            BuildPickRows();
        }
        else if (drop)
        {
            HeadName.Text = "clayo";
            HeadRest.Text = "";
            Sub.Text = "drop to start";
            Sub.Visibility = Visibility.Visible;
        }
        else if (note is null)
        {
            int n = _main.OpenSessionCount;
            HeadName.Text = "clayo";
            HeadRest.Text = "";
            Sub.Text = n == 1 ? "1 session" : $"{n} sessions";
            Sub.Visibility = Visibility.Visible;
        }
        else
        {
            // One line, as the sidebar words it.
            HeadName.Text = note.Name;
            HeadRest.Text = note.Kind switch
            {
                NoteKind.NeedsYou => " · needs you",
                NoteKind.Error => " · api error",
                NoteKind.Reserve => " · past your reserve",
                NoteKind.Tip => "",
                _ => " · finished",
            };
            Sub.Visibility = Visibility.Collapsed;
        }

        Present(pillWidth, fresh);

        _poseTimer.Stop();
        Mascot.Still = false;
        _restTimer.Stop();
        _restTimer.Start();
        switch (note?.Kind)
        {
            case null when drop || pick: Pose(MascotMove.Peek); break;
            // The login hello waves until it leaves, a couple of seconds later.
            case null when greeting: Pose(MascotMove.Wave); break;
            // Walking while the work runs; ClayoMascot stops it when the island hides.
            case null when compact: Pose(MascotMove.Walk); break;
            case null: Pose(MascotMove.Wave, then: MascotMove.Idle); break;
            case NoteKind.NeedsYou: Pose(MascotMove.Alert, then: MascotMove.Talk); break;
            case NoteKind.Error or NoteKind.Reserve: Pose(MascotMove.Alert); break;
            case NoteKind.Done: Pose(MascotMove.Jump); break;
            case NoteKind.Tip: Pose(MascotMove.Wave, then: MascotMove.Idle); break;
        }

        var light = note?.Kind switch
        {
            null when compact && !greeting => IslandGlow.Blue,
            null => IslandGlow.Warm,
            NoteKind.Done => IslandGlow.Green,
            NoteKind.Tip => IslandGlow.Warm,
            _ => IslandGlow.Amber,
        };
        Light(light);
    }

    /// <summary>
    /// Puts the window at the top of the island's monitor and brings the pill out at this
    /// width, or grows it there when it is already out.
    /// </summary>
    private void Present(double pillWidth, bool fresh)
    {
        _pillWidth = pillWidth;
        double height = _trigger.State switch
        {
            IslandState.Drop => TallHeight,
            // The header, a 34 px row per choice and New session, the padding and the slack.
            IslandState.Pick => 52 + 34 * (_choices.Count + 1) + 12 + 10,
            _ => IslandHeight,
        };
        int w = (int)Math.Round(NoteWidth * _scale);
        int h = (int)Math.Round(height * _scale);
        int x = (_monitor.Left + _monitor.Right) / 2 - w / 2;
        _window = new PxRect(x, _monitor.Top, x + w, _monitor.Top + h);

        // Only the pill keeps a peek open, not the transparent rest of the window. The poll
        // keeps its height up to date.
        int pw = (int)Math.Round(pillWidth * _scale);
        int px = (_monitor.Left + _monitor.Right) / 2 - pw / 2;
        _placed = new PxRect(px, _monitor.Top, px + pw, _monitor.Top + (int)Math.Round(IslandHeight * _scale));

        // Placed by hand in physical px: Left/Top are in the units of whichever monitor the
        // window was last on, which is the wrong monitor whenever the cursor has moved to a
        // screen at a different scale. Placed again after Show because crossing to such a
        // screen raises WM_DPICHANGED, and WPF answers that by moving the window to the rect
        // Windows suggests rather than where we put it.
        Place();
        if (!IsVisible) Show();
        Place();

        // The prototype's spring, cubic-bezier(.34,1.4,.64,1): out with a slight overshoot.
        var spring = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 };
        if (fresh)
        {
            Pill.BeginAnimation(WidthProperty, null);
            Pill.Width = pillWidth;
        }
        else Pill.BeginAnimation(WidthProperty, new DoubleAnimation(pillWidth, TimeSpan.FromMilliseconds(400))
        {
            EasingFunction = spring,
        });
        Slide.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0, TimeSpan.FromMilliseconds(450)) { EasingFunction = spring });
    }

    /// <summary>The glow under the pill, as wide as the pill but never wider than under a note.</summary>
    private void Light(Color color) =>
        _glow.Light(_monitor, _scale, new WindowInteropHelper(this).Handle, color,
                    Math.Min(1, _pillWidth / NoteWidth));

    private void Pose(MascotMove now, MascotMove? then = null)
    {
        Mascot.Play(now);
        if (then is not { } next) return;
        _poseAfter = next;
        _poseTimer.Start();
    }

    private void SlideOut()
    {
        _shown = null;
        _poseTimer.Stop();
        _restTimer.Stop();
        _glow.Dim(IslandHeight);

        // All the way up, however far the drop target has grown it.
        var anim = new DoubleAnimation(-Math.Max(IslandHeight, Pill.ActualHeight + 8), TimeSpan.FromMilliseconds(260))
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

    /// <summary>
    /// WS_EX_TRANSPARENT on a layered window passes every click and hover through to whatever
    /// is underneath. The poll still sees the cursor over it, because it asks GetCursorPos, not
    /// the window.
    /// </summary>
    private void ClickThrough(bool on)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int style = GetWindowLong(hwnd, GWL_EXSTYLE);
        int want = on ? style | WS_EX_TRANSPARENT : style & ~WS_EX_TRANSPARENT;
        if (want != style) SetWindowLong(hwnd, GWL_EXSTYLE, want);
    }

    private void Place()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowPos(hwnd, IntPtr.Zero, _window.Left, _window.Top,
                     _window.Right - _window.Left, _window.Bottom - _window.Top,
                     SWP_NOZORDER | SWP_NOACTIVATE);
    }

    private void Pill_Click(object sender, MouseButtonEventArgs e)
    {
        // A click on the drop target is meant for it, not a way into Clayo; so is one on the
        // picker, whose rows take their own clicks.
        if (_trigger.State is IslandState.Drop or IslandState.Pick) return;
        var note = _trigger.Note;
        _trigger.Dismiss();
        SlideOut();

        // The click was the last input event and it went to this process, so Windows allows
        // the foreground change, also for a window that is hidden rather than minimized.
        _main.Reveal();

        // On the session that asked, not whichever pane was last in front.
        if (note?.Source is TerminalPane pane) _main.ShowSession(pane);
    }

    // ------------------------------------------------------------ screenshots

    /// <summary>
    /// Follows the screenshot settings: Ctrl+Alt+S on or off, and listening to the clipboard
    /// for the automatic offer (D1, D2). Called at start, on every change, and with both off
    /// on close.
    /// </summary>
    private void ApplyShots(ClayoSettings s)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        if (s.ScreenshotHotkey && !_shotKey)
        {
            _shotKey = RegisterHotKey(hwnd, ShotKey, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 0x53);   // S
            // Another app has it (or a second Clayo): carry on without, and say so once.
            if (!_shotKey) _trigger.Notify(new IslandNote(nameof(ShotKey), "Ctrl+Alt+S is taken by another app", NoteKind.Tip));
        }
        else if (!s.ScreenshotHotkey && _shotKey)
        {
            UnregisterHotKey(hwnd, ShotKey);
            _shotKey = false;
        }
        _main.ScreenshotKeyTaken = s.ScreenshotHotkey && !_shotKey;

        if (s.ScreenshotOffer && !_offering)
        {
            _clipSeq = GetClipboardSequenceNumber();
            _offering = AddClipboardFormatListener(hwnd);
        }
        else if (!s.ScreenshotOffer && _offering)
        {
            RemoveClipboardFormatListener(hwnd);
            _offering = false;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            OnHotkey(wParam.ToInt32());
            handled = true;
        }
        else if (msg == WM_CLIPBOARDUPDATE) OnClipboard();
        return IntPtr.Zero;
    }

    private void OnHotkey(int id)
    {
        if (id == ShotKey)
        {
            if (_trigger.State == IslandState.Pick) return;
            if (Screenshots.OnClipboard()) StartPick();
            else NoShot();
            return;
        }
        int key = id - PickKeys;
        if (key >= 0 && key < _choices.Count && key <= 2) Choose(key);
        else if (key == 3) Choose(-1);
        else if (key == 4) CancelPick();
    }

    private void NoShot() =>
        _trigger.Notify(new IslandNote(nameof(Screenshots), "Copy a screenshot first (Win+Shift+S)", NoteKind.Tip));

    /// <summary>
    /// The automatic offer: a new image on the clipboard brings the picker by itself. Not while
    /// Clayo is in front (you would paste it yourself there), and not over a fullscreen app or
    /// Do Not Disturb, which the hotkey may override but this may not.
    /// </summary>
    private void OnClipboard()
    {
        // One copy can raise several notices; the sequence number tells a new copy apart.
        var seq = GetClipboardSequenceNumber();
        if (seq == _clipSeq) return;
        _clipSeq = seq;
        if (!_offering || _trigger.State == IslandState.Pick || ClayoInFront || IsBusy()
            || !Screenshots.OnClipboard()) return;
        StartPick();
    }

    private void StartPick()
    {
        _choices = _main.RecentSessions(3);
        _pickAsked = true;
        var hwnd = new WindowInteropHelper(this).Handle;
        // A key another app holds is just not offered by keyboard; a click still works.
        for (int i = 0; i < PickVks.Length; i++) RegisterHotKey(hwnd, PickKeys + i, MOD_NOREPEAT, PickVks[i]);
        _pickKeys = true;
        // Now, not on the next poll: the key press was this moment.
        Tick(null, EventArgs.Empty);
    }

    private void ReleasePickKeys()
    {
        if (!_pickKeys) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        for (int i = 0; i < PickVks.Length; i++) UnregisterHotKey(hwnd, PickKeys + i);
        _pickKeys = false;
    }

    private void CancelPick()
    {
        _trigger.EndPick();
        ReleasePickKeys();
        SlideOut();
    }

    /// <summary>
    /// An answer: a session by its place in the list, or -1 for New session. The image is only
    /// written now, so a cancelled offer leaves nothing on disk.
    /// </summary>
    private void Choose(int index)
    {
        if (_trigger.State != IslandState.Pick) return;
        var pane = index >= 0 && index < _choices.Count ? _choices[index].Pane : null;
        CancelPick();

        if (Screenshots.SaveFromClipboard() is not { } path)
        {
            NoShot();
            return;
        }
        // The key press or click was this process's input, so Clayo may come to the front.
        _main.SendImage(pane, path);
    }

    private void BuildPickRows()
    {
        PickBody.Children.Clear();
        for (int i = 0; i < _choices.Count; i++)
        {
            var c = _choices[i];
            PickBody.Children.Add(PickRow((i + 1).ToString(), c.Name, c.Folder,
                (Brush)FindResource(SessionRow.BrushKeyFor(c.Status)), i));
        }
        PickBody.Children.Add(PickRow("N", "New session…", _choices.FirstOrDefault()?.Folder ?? "", null, -1));
    }

    private static readonly Brush KeyFill = Frozen(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
    private static readonly Brush RowHover = Frozen(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
    private static readonly Brush Bright = Frozen(Color.FromRgb(0xEC, 0xEE, 0xF1));
    private static readonly Brush Faint = Frozen(Color.FromRgb(0x8B, 0x91, 0x9A));

    private static Brush Frozen(Color c)
    {
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        return brush;
    }

    /// <summary>One picker row: its key, the session's name and folder, and its status dot.</summary>
    private System.Windows.Controls.Border PickRow(string key, string name, string folder, Brush? dot, int index)
    {
        var line = new System.Windows.Controls.DockPanel { Margin = new Thickness(8, 0, 10, 0) };
        var badge = new System.Windows.Controls.Border
        {
            Width = 20, Height = 20, CornerRadius = new CornerRadius(5), Background = KeyFill,
            Margin = new Thickness(0, 0, 10, 0),
            Child = new System.Windows.Controls.TextBlock
            {
                Text = key, Foreground = Bright, FontSize = 11.5, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        System.Windows.Controls.DockPanel.SetDock(badge, System.Windows.Controls.Dock.Left);
        line.Children.Add(badge);
        if (dot is not null)
        {
            var light = new System.Windows.Shapes.Ellipse
            {
                Width = 8, Height = 8, Fill = dot, Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            System.Windows.Controls.DockPanel.SetDock(light, System.Windows.Controls.Dock.Right);
            line.Children.Add(light);
        }
        var text = new System.Windows.Controls.TextBlock
        {
            FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        text.Inlines.Add(new System.Windows.Documents.Run(name) { Foreground = Bright, FontWeight = FontWeights.SemiBold });
        if (folder.Length > 0) text.Inlines.Add(new System.Windows.Documents.Run("  " + folder) { Foreground = Faint });
        line.Children.Add(text);

        var row = new System.Windows.Controls.Border
        {
            Height = 34, CornerRadius = new CornerRadius(10), Background = Brushes.Transparent,
            Cursor = Cursors.Hand, Child = line,
        };
        row.MouseEnter += (_, _) => row.Background = RowHover;
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Choose(index);
        };
        return row;
    }

    // ------------------------------------------------------------------- drop

    /// <summary>The first path of a file drop, or null for anything else (text, a browser image).</summary>
    private static string? Dropped(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths ? paths[0] : null;

    /// <summary>
    /// Only the drop target takes a drop. A peek or a note can be under a drag too, and saying
    /// no there keeps a drag passing over the island from landing somewhere it means nothing.
    /// </summary>
    private void Pill_DragOver(object sender, DragEventArgs e)
    {
        bool take = _trigger.State == IslandState.Drop && Dropped(e) is not null;
        e.Effects = take ? DragDropEffects.Copy : DragDropEffects.None;
        DropFrame.Fill = take ? DropOver : DropIdle;
        e.Handled = true;
    }

    private void Pill_DragLeave(object sender, DragEventArgs e) => DropFrame.Fill = DropIdle;

    // The warm light, faint at rest and brighter while a drag is over the target. Shared, since
    // DragOver fires on every move of the drag.
    private static readonly Brush DropIdle = Tint(0x0F), DropOver = Tint(0x29);

    private static Brush Tint(byte alpha)
    {
        var c = IslandGlow.Warm;
        var brush = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// A full session in the dropped folder, the same as typing clayo in Explorer there. A
    /// file gets one in its folder with @file already in the input, for you to ask about it.
    /// </summary>
    private void Pill_Drop(object sender, DragEventArgs e)
    {
        Pill_DragLeave(sender, e);
        if (_trigger.State != IslandState.Drop || Dropped(e) is not { } path) return;
        e.Handled = true;
        _trigger.Release();
        SlideOut();

        if (Directory.Exists(path)) _main.AdoptFolder(path);
        else if (File.Exists(path)) _main.AdoptFolder(Path.GetDirectoryName(path)!, Mention(Path.GetFileName(path)));
    }

    /// <summary>
    /// Relative to the session's folder, which is the file's. Claude Code reads a quoted
    /// @"..." for a name with a space in it. A trailing space so you can carry on typing.
    /// </summary>
    private static string Mention(string name) => (name.Contains(' ') ? $"@\"{name}\"" : $"@{name}") + " ";

    /// <summary>Clayo was started at login; the compact pill waves once on the next poll that allows it.</summary>
    public void Greet() => _trigger.Greet();

    // Shared with the gear's menu in the sidebar.
    private void Menu_Opened(object sender, RoutedEventArgs e) => MainWindow.ShowLoginState(LoginItem);

    private void Login_Click(object sender, RoutedEventArgs e) => MainWindow.ApplyLoginState(LoginItem);

    private void Quit_Click(object sender, RoutedEventArgs e) => _main.Quit();

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
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    [DllImport("user32.dll")]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    private const int WM_HOTKEY = 0x0312;
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_NOREPEAT = 0x4000;

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
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
}

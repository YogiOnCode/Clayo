namespace CcxShell.Core;

/// <summary>Notify joins this in step 3, when pane status starts driving the island.</summary>
public enum IslandState { Hidden, Peek }

/// <summary>A rectangle in physical pixels. Right and Bottom are exclusive, like a Win32 RECT.</summary>
public readonly record struct PxRect(int Left, int Top, int Right, int Bottom)
{
    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
}

/// <summary>
/// Everything the trigger looks at in one poll. Cursor and monitor are physical pixels, as
/// GetCursorPos and GetMonitorInfo report them under PerMonitorV2; Scale is that monitor's
/// DPI over 96, so the zone can be sized in logical pixels the way the design specifies it.
/// </summary>
public readonly record struct IslandInput(
    int CursorX, int CursorY,
    PxRect Monitor, double Scale,
    bool ButtonDown, bool MovingOrSizing, bool Busy,
    bool OverIsland,
    long NowMs);

/// <summary>
/// Decides whether the island peeks. Kept free of WPF and Win32 so checks/island.cs can
/// drive it with made-up cursors and clocks; IslandWindow only gathers the inputs.
/// </summary>
public sealed class IslandTrigger
{
    /// <summary>Only the very top rows count. Anything deeper is the browser's tab strip.</summary>
    public const int EdgePx = 2;

    /// <summary>Logical px, centred on the monitor. Narrow so passing over a tab does not count.</summary>
    public const double ZoneWidth = 240;

    public const long DwellMs = 600;
    public const long LeaveMs = 400;

    private long? _dwellSince;
    private long? _awaySince;

    // Set when the island is sent away by hand (Esc, click). Without it a cursor still resting
    // at the edge would bring it straight back 600 ms later.
    private bool _mustLeaveFirst;

    public IslandState State { get; private set; }

    public static bool InZone(in IslandInput i)
    {
        var m = i.Monitor;
        double centre = (m.Left + m.Right) / 2.0;
        return i.CursorY >= m.Top && i.CursorY < m.Top + EdgePx
            && Math.Abs(i.CursorX - centre) <= ZoneWidth * i.Scale / 2;
    }

    public IslandState Update(in IslandInput i)
    {
        bool zone = InZone(i);
        if (!zone) _mustLeaveFirst = false;

        if (State == IslandState.Peek)
        {
            // A fullscreen game or a presentation outranks a peek the user asked for a moment ago.
            if (i.Busy) Hide();
            else if (zone || i.OverIsland) _awaySince = null;
            else
            {
                _awaySince ??= i.NowMs;
                if (i.NowMs - _awaySince >= LeaveMs) Hide();
            }
            return State;
        }

        // A held button means a window is being dragged toward the top to snap or maximize;
        // move/size mode catches the same drag when the button state is not what Windows uses
        // (keyboard move, touch). Either way the dwell restarts from zero afterwards.
        if (!zone || _mustLeaveFirst || i.ButtonDown || i.MovingOrSizing || i.Busy)
        {
            _dwellSince = null;
            return State;
        }

        _dwellSince ??= i.NowMs;
        if (i.NowMs - _dwellSince >= DwellMs)
        {
            State = IslandState.Peek;
            _dwellSince = null;
            _awaySince = null;
        }
        return State;
    }

    /// <summary>Hide now, and stay hidden until the cursor has left the zone once.</summary>
    public void Dismiss()
    {
        Hide();
        _mustLeaveFirst = true;
    }

    private void Hide()
    {
        State = IslandState.Hidden;
        _dwellSince = null;
        _awaySince = null;
    }
}

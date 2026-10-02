namespace CcxShell.Core;

public enum IslandState { Hidden, Peek, Notify }

/// <summary>What a session wants you to know. NeedsYou and Error block the session, Done does not.</summary>
public enum NoteKind { NeedsYou, Error, Done }

/// <summary>
/// One notification. Source is whatever identifies the session to the caller (the pane);
/// the trigger only compares it, so it stays free of WPF.
/// </summary>
public sealed record IslandNote(object Source, string Name, NoteKind Kind)
{
    public bool Urgent => Kind != NoteKind.Done;
}

/// <summary>A rectangle in physical pixels. Right and Bottom are exclusive, like a Win32 RECT.</summary>
public readonly record struct PxRect(int Left, int Top, int Right, int Bottom)
{
    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
}

/// <summary>
/// Everything the trigger looks at in one poll. Cursor and monitor are physical pixels, as
/// GetCursorPos and GetMonitorInfo report them under PerMonitorV2; Scale is that monitor's
/// DPI over 96, so the zone can be sized in logical pixels the way the design specifies it.
/// Busy is fullscreen, presentation or Do Not Disturb; ClayoActive is kept apart from it
/// because it also drops a pending Done (see Update).
/// </summary>
public readonly record struct IslandInput(
    int CursorX, int CursorY,
    PxRect Monitor, double Scale,
    bool ButtonDown, bool MovingOrSizing, bool Busy,
    bool OverIsland,
    long NowMs,
    bool ClayoActive = false);

/// <summary>
/// Decides whether the island peeks or shows a notification. Kept free of WPF and Win32 so
/// checks/island.cs can drive it with made-up cursors and clocks; IslandWindow only gathers
/// the inputs.
/// </summary>
public sealed class IslandTrigger
{
    /// <summary>Only the very top rows count. Anything deeper is the browser's tab strip.</summary>
    public const int EdgePx = 2;

    /// <summary>Logical px, centred on the monitor. Narrow so passing over a tab does not count.</summary>
    public const double ZoneWidth = 240;

    public const long DwellMs = 600;
    public const long LeaveMs = 400;
    public const long DoneMs = 4000;

    private long? _dwellSince;
    private long? _awaySince;

    // Set when the island is sent away by hand (Esc, click). Without it a cursor still resting
    // at the edge would bring it straight back 600 ms later.
    private bool _mustLeaveFirst;

    // Every note not yet dealt with, the one showing included. At most one per session: a
    // session has one status, so a newer note from it replaces the older one.
    private readonly List<(IslandNote note, long seq)> _notes = [];
    private long _seq;
    private long _shownAt;

    public IslandState State { get; private set; }

    /// <summary>The note on screen while State is Notify, otherwise null.</summary>
    public IslandNote? Note { get; private set; }

    /// <summary>0..1 through the dwell at the edge, for the dwell bar. 0 when not dwelling.</summary>
    public double DwellProgress { get; private set; }

    public static bool InZone(in IslandInput i)
    {
        var m = i.Monitor;
        double centre = (m.Left + m.Right) / 2.0;
        return i.CursorY >= m.Top && i.CursorY < m.Top + EdgePx
            && Math.Abs(i.CursorX - centre) <= ZoneWidth * i.Scale / 2;
    }

    /// <summary>Queues a note. It shows on the next Update unless something is busy.</summary>
    public void Notify(IslandNote note)
    {
        _notes.RemoveAll(n => Equals(n.note.Source, note.Source));
        _notes.Add((note, ++_seq));
    }

    /// <summary>
    /// The session's status moved on (back to work, exited, closed), so whatever it said is
    /// stale: showing it later would send you to a session that no longer needs you.
    /// </summary>
    public void Resolve(object source) => _notes.RemoveAll(n => Equals(n.note.Source, source));

    public IslandState Update(in IslandInput i)
    {
        bool zone = InZone(i);
        if (!zone) _mustLeaveFirst = false;
        DwellProgress = 0;

        if (i.Busy || i.ClayoActive)
        {
            // Held, not dropped: a missed permission leaves the session stuck. A Done only
            // reports, and with Clayo in front the sidebar already says it, so that one goes.
            if (i.ClayoActive) _notes.RemoveAll(n => !n.note.Urgent);
            // A fullscreen game or a presentation also outranks a peek asked for a moment ago.
            if (State != IslandState.Hidden) Hide();
            _dwellSince = null;
            return State;
        }

        if (ShowNote(i)) return State;
        if (State == IslandState.Notify) Hide();

        if (State == IslandState.Peek)
        {
            if (zone || i.OverIsland) _awaySince = null;
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
        if (!zone || _mustLeaveFirst || i.ButtonDown || i.MovingOrSizing)
        {
            _dwellSince = null;
            return State;
        }

        _dwellSince ??= i.NowMs;
        long dwelt = i.NowMs - _dwellSince.Value;
        if (dwelt >= DwellMs)
        {
            State = IslandState.Peek;
            _dwellSince = null;
            _awaySince = null;
        }
        else DwellProgress = (double)dwelt / DwellMs;
        return State;
    }

    /// <summary>Shows the most pressing note, if any is left. False when there is none.</summary>
    private bool ShowNote(in IslandInput i)
    {
        if (Note is { Urgent: false } done && Expired(i))
        {
            Resolve(done.Source);
            Note = null;
        }

        // Urgent before Done, then the newest.
        IslandNote? best = null;
        long bestSeq = -1;
        foreach (var (note, seq) in _notes)
            if (best is null || (note.Urgent, seq).CompareTo((best.Urgent, bestSeq)) > 0)
                (best, bestSeq) = (note, seq);
        if (best is null) return false;

        if (!ReferenceEquals(best, Note))
        {
            // The 4 s of a Done count from when it is actually on screen, not from when it was
            // queued behind a fullscreen game.
            Note = best;
            _shownAt = i.NowMs;
            _awaySince = null;
        }
        State = IslandState.Notify;
        _dwellSince = null;

        if (i.OverIsland) _awaySince = null;
        else _awaySince ??= i.NowMs;
        return true;
    }

    /// <summary>
    /// A Done has had its 4 s, and the cursor is not reading it: hovering holds it, and it
    /// leaves 400 ms after the cursor does.
    /// </summary>
    private bool Expired(in IslandInput i) =>
        i.NowMs - _shownAt >= DoneMs
        && !i.OverIsland
        && i.NowMs - (_awaySince ?? i.NowMs) >= LeaveMs;

    /// <summary>
    /// Hide now, and stay hidden until the cursor has left the zone once. A note showing is
    /// dealt with; the next pending one, if any, shows on the following Update.
    /// </summary>
    public void Dismiss()
    {
        if (Note is { } n) Resolve(n.Source);
        Hide();
        _mustLeaveFirst = true;
    }

    private void Hide()
    {
        State = IslandState.Hidden;
        Note = null;
        _dwellSince = null;
        _awaySince = null;
    }
}

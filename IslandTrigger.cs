namespace CcxShell.Core;

/// <summary>
/// Compact is the small pill the island shows by itself while a session works: only the mascot
/// and a status dot, and click-through, so it never stands between you and the tabs under it.
/// Drop is the "Drop here" target a file drag brings out.
/// </summary>
public enum IslandState { Hidden, Compact, Peek, Notify, Drop }

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
/// because it also drops a pending Done (see Update). Dragging is an OLE drag in progress (a
/// file, a link): a held button alone is just as likely a tab or a text selection.
/// </summary>
public readonly record struct IslandInput(
    int CursorX, int CursorY,
    PxRect Monitor, double Scale,
    bool ButtonDown, bool MovingOrSizing, bool Busy,
    bool OverIsland,
    long NowMs,
    bool ClayoActive = false,
    bool Dragging = false);

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

    // A file drag gets a bigger, quicker target than a resting cursor: carrying a file you aim
    // at the top of the screen, not at its first two rows, and only an OLE drag counts, so the
    // looser zone cannot bring the island out while you browse. Overshooting while aiming is
    // forgiven for longer than the cursor's leave.
    public const double DragZoneWidth = 480;
    public const double DragZoneHeight = 40;
    public const long DragDwellMs = 250;
    public const long DragLeaveMs = 1500;
    public const long DoneMs = 4000;

    /// <summary>At most one self-peek in this long, so a busy afternoon does not keep popping it up.</summary>
    public const long SelfPeekEveryMs = 180_000;

    /// <summary>A self-peek leaves after this even while the work runs: it has said its piece.</summary>
    public const long CompactMs = 10_000;

    /// <summary>Resting on the compact pill this long opens the full island. The edge dwell's length, so both feel alike.</summary>
    public const long RestMs = 600;

    /// <summary>The login greeting is out this long: one wave, then gone.</summary>
    public const long GreetMs = 2500;

    /// <summary>
    /// A greeting held back by a fullscreen app or Do Not Disturb gives up after this. Much
    /// later it would only say hello in the middle of something else.
    /// </summary>
    public const long GreetWithinMs = 60_000;

    private long? _dwellSince;
    private long? _awaySince;
    private long? _dragSince;

    // Set when the island is sent away by hand (Esc, click). Without it a cursor still resting
    // at the edge would bring it straight back 600 ms later.
    private bool _mustLeaveFirst;

    // The drag counterpart: the drag going on when the island was sent away (Esc cancels both
    // at once) is ignored until it ends. A new drag is deliberate, so it may bring the island
    // straight back, wherever the cursor has been.
    private bool _dragEndFirst;

    // Every note not yet dealt with, the one showing included. At most one per session: a
    // session has one status, so a newer note from it replaces the older one.
    private readonly List<(IslandNote note, long seq)> _notes = [];
    private long _seq;
    private long _shownAt;

    // Sessions working right now, and those of them the compact pill has already been out for
    // (or skipped for the rate limit) in this run of work. A session leaves both when it stops.
    private readonly HashSet<object> _working = [];
    private readonly HashSet<object> _peeked = [];
    private long? _selfPeekAt;
    private long _compactSince;
    private long? _restSince;

    private bool _greetPending;
    private long? _greetSince;
    private bool _greeting;

    public IslandState State { get; private set; }

    /// <summary>The note on screen while State is Notify, otherwise null.</summary>
    public IslandNote? Note { get; private set; }

    /// <summary>0..1 through the dwell at the edge, for the dwell bar. 0 when not dwelling.</summary>
    public double DwellProgress { get; private set; }

    /// <summary>How long that dwell is in all: shorter for a file drag, so the bar fills in time.</summary>
    public long DwellLengthMs { get; private set; } = DwellMs;

    /// <summary>The compact pill on screen is the login greeting, not a session at work.</summary>
    public bool Greeting => State == IslandState.Compact && _greeting;

    public static bool InZone(in IslandInput i)
    {
        var m = i.Monitor;
        double centre = (m.Left + m.Right) / 2.0;
        return i.CursorY >= m.Top && i.CursorY < m.Top + EdgePx
            && Math.Abs(i.CursorX - centre) <= ZoneWidth * i.Scale / 2;
    }

    public static bool InDragZone(in IslandInput i)
    {
        var m = i.Monitor;
        double centre = (m.Left + m.Right) / 2.0;
        return i.CursorY >= m.Top && i.CursorY < m.Top + DragZoneHeight * i.Scale
            && Math.Abs(i.CursorX - centre) <= DragZoneWidth * i.Scale / 2;
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

    /// <summary>
    /// A session started or stopped working. A start may bring out the compact pill on a later
    /// Update; one held back while Clayo is in front still shows once Clayo goes behind, as
    /// long as the work runs.
    /// </summary>
    public void SetWorking(object source, bool working)
    {
        if (working) _working.Add(source);
        else
        {
            _working.Remove(source);
            _peeked.Remove(source);
        }
    }

    /// <summary>Clayo started at login: say hello once with the compact pill, on a later Update.</summary>
    public void Greet() => _greetPending = true;

    public IslandState Update(in IslandInput i)
    {
        bool zone = InZone(i);
        if (!zone) _mustLeaveFirst = false;
        if (!i.Dragging) _dragEndFirst = false;
        DwellProgress = 0;

        if (_greetPending)
        {
            _greetSince ??= i.NowMs;
            if (i.NowMs - _greetSince >= GreetWithinMs) _greetPending = false;
        }

        if (i.Busy || i.ClayoActive)
        {
            // Clayo in front means you already found it; the hello has nothing left to say.
            if (i.ClayoActive) _greetPending = false;
            // Held, not dropped: a missed permission leaves the session stuck. A Done only
            // reports, and with Clayo in front the sidebar already says it, so that one goes.
            if (i.ClayoActive) _notes.RemoveAll(n => !n.note.Urgent);
            // A fullscreen game or a presentation also outranks a peek asked for a moment ago.
            if (State != IslandState.Hidden) Hide();
            _dwellSince = null;
            return State;
        }

        bool dragZone = InDragZone(i);
        if (State == IslandState.Drop) return Drop(i, dragZone);
        if (DragDwell(i, dragZone)) return State;

        if (ShowNote(i)) return State;
        if (State == IslandState.Notify) Hide();

        if (State == IslandState.Compact) return Compact(i, zone);

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

        if (GreetNow(i) || SelfPeek(i)) return State;

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
        else
        {
            DwellProgress = (double)dwelt / DwellMs;
            DwellLengthMs = DwellMs;
        }
        return State;
    }

    /// <summary>
    /// A file dragged to the edge and held there for the dwell brings out the drop target, over
    /// whatever else shows; a note waits and comes back after. Move/size mode is a window drag,
    /// and only an OLE drag counts, so a tab dragged along the strip never brings it out.
    /// </summary>
    private bool DragDwell(in IslandInput i, bool zone)
    {
        if (!i.Dragging || i.MovingOrSizing || !zone || _dragEndFirst)
        {
            _dragSince = null;
            return false;
        }
        _dragSince ??= i.NowMs;
        long dwelt = i.NowMs - _dragSince.Value;
        if (dwelt < DragDwellMs)
        {
            DwellProgress = (double)dwelt / DragDwellMs;
            DwellLengthMs = DragDwellMs;
            return false;
        }
        Hide();
        State = IslandState.Drop;
        return true;
    }

    /// <summary>
    /// The target stays while the drag goes on over it or the drag zone. A drag that wanders
    /// off has 1.5 s to come back; one that ended elsewhere has nothing left to aim, so it goes
    /// after the usual 400 ms. A drop on it calls Release before the drag ends.
    /// </summary>
    private IslandState Drop(in IslandInput i, bool dragZone)
    {
        if (i.Dragging && (dragZone || i.OverIsland)) _awaySince = null;
        else
        {
            _awaySince ??= i.NowMs;
            if (i.NowMs - _awaySince >= (i.Dragging ? DragLeaveMs : LeaveMs)) Hide();
        }
        return State;
    }

    /// <summary>
    /// Something was dropped. Hidden, and not back until the cursor leaves the edge, or a new
    /// drag starts.
    /// </summary>
    public void Release()
    {
        Hide();
        _mustLeaveFirst = true;
        _dragEndFirst = true;
    }

    /// <summary>
    /// Brings out the compact pill if a session has started working since the last one and
    /// the rate limit allows. A start the limit blocks is skipped, not held: popping up minutes
    /// into the work would be about nothing in particular.
    /// </summary>
    private bool SelfPeek(in IslandInput i)
    {
        bool fresh = false;
        foreach (var s in _working) fresh |= _peeked.Add(s);
        if (!fresh || i.NowMs - _selfPeekAt < SelfPeekEveryMs) return false;

        State = IslandState.Compact;
        _selfPeekAt = _compactSince = i.NowMs;
        _restSince = null;
        _dwellSince = null;
        return true;
    }

    /// <summary>
    /// Shows the login greeting if one is waiting. It is not a self-peek: it is about Clayo
    /// being there, not about any work, so a session starting right after still shows.
    /// </summary>
    private bool GreetNow(in IslandInput i)
    {
        if (!_greetPending) return false;
        _greetPending = false;
        _greeting = true;
        State = IslandState.Compact;
        _compactSince = i.NowMs;
        _restSince = null;
        _dwellSince = null;
        return true;
    }

    private IslandState Compact(in IslandInput i, bool zone)
    {
        // A held button is a tab being dragged along the strip, not a cursor resting on the pill.
        if ((zone || i.OverIsland) && !i.ButtonDown && !i.MovingOrSizing)
        {
            _restSince ??= i.NowMs;
            if (i.NowMs - _restSince >= RestMs)
            {
                State = IslandState.Peek;
                _greeting = false;
                _restSince = null;
                _awaySince = null;
            }
            // Not sent away by the time limit mid-rest: that cursor is about to open it.
            return State;
        }
        _restSince = null;

        bool over = _greeting
            ? i.NowMs - _compactSince >= GreetMs
            : _working.Count == 0 || i.NowMs - _compactSince >= CompactMs;
        if (over) Hide();
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
        _dragEndFirst = true;
    }

    private void Hide()
    {
        State = IslandState.Hidden;
        Note = null;
        _greeting = false;
        _dwellSince = null;
        _awaySince = null;
        _restSince = null;
        _dragSince = null;
    }
}

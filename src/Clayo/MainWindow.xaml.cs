using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using CcxShell.Core;
using CcxShell.UI;
using Microsoft.Win32;

namespace CcxShell;

/// <summary>One row in the folder picker: the leaf to read, the full path to launch in.</summary>
public sealed record FolderChoice(string Name, string FullPath);

/// <summary>
/// One row in the sidebar. Backed by a transcript, a live pane, or both — a session you
/// started in this window has a pane immediately and grows a transcript a moment later.
/// </summary>
public sealed class SessionRow : INotifyPropertyChanged
{
    /// <summary>A placeholder for a Codex pane until its transcript appears (MainWindow.BindCodexPanes).</summary>
    public required string SessionId { get; set; }

    /// <summary>Null until the transcript for this session shows up on disk.</summary>
    public SessionInfo? Info { get; set; }

    /// <summary>Non-null while the session is open in this window.</summary>
    public TerminalPane? Pane { get; set; }

    /// <summary>Used for the row label before any transcript exists.</summary>
    public string FallbackTitle { get; set; } = "new session";

    public string? CustomName { get; set; }
    public string Folder { get; set; } = "";

    /// <summary>A row with no transcript yet is a pane Clayo started, which knows its agent.</summary>
    public AgentKind Agent => Info?.Agent ?? Pane?.Agent ?? AgentKind.Claude;

    /// <summary>Set by MainWindow when both agents are in use, so the tag only shows when it tells rows apart.</summary>
    public bool ShowAgent { get; set; }

    public string AgentTag => Agent == AgentKind.Codex ? "CX" : "CC";

    /// <summary>The badge colours from the setup window: clay for Claude Code, blue for Codex.</summary>
    public string AgentBrush => Agent == AgentKind.Codex ? "#7DBBEB" : "#E39A7E";

    public Visibility AgentVisibility => ShowAgent ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;

    // ------------------------------------------------------------------ labels

    /// <summary>
    /// Your name wins, then the title Claude Code generated — the same text the
    /// `claude --resume` picker shows — then the first prompt, then a placeholder.
    /// </summary>
    public string Name =>
        CustomName
        ?? Info?.AiTitle
        ?? (Info?.Preview is { Length: > 0 } p && p != "(no prompt yet)" ? p : null)
        ?? FallbackTitle;

    public string Subtitle => IsOpen ? StatusText : Age;

    public string Tip =>
        $"{Name}\n{(Info?.ProjectDir ?? Folder)}\n{SessionId}"
        + (BranchOf is { Length: > 0 } b ? $"\n\nBranched from: {b}" : "")
        + (Info?.LastPrompt is { Length: > 0 } lp ? $"\n\nLast: {lp}" : "");

    public string Project =>
        Info?.ProjectName
        ?? (Path.GetFileName(Folder.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : Folder);

    public DateTime When => Info?.LastActivity ?? DateTime.Now;

    public string Age
    {
        get
        {
            var d = DateTime.Now - When;
            if (d.TotalMinutes < 1) return "just now";
            if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} min ago";
            if (d.TotalHours < 24) return $"{(int)d.TotalHours} h ago";
            if (d.TotalDays < 7) return $"{(int)d.TotalDays} d ago";
            return When.ToString("d MMM");
        }
    }

    // ----------------------------------------------------------------- buckets

    public bool IsOpen => Pane is not null;

    /// <summary>
    /// Open first — that is the whole reason this app exists, so you can see at a glance
    /// what you left running. Then today's work, then everything else folded away.
    /// </summary>
    public string Bucket => IsOpen ? "Open" : (When.Date == DateTime.Today ? "Today" : "Earlier");

    /// <summary>Groups form in item order, so sorting on this is what orders the sections.</summary>
    public int BucketOrder => IsOpen ? 0 : (When.Date == DateTime.Today ? 1 : 2);

    /// <summary>
    /// Newest activity anywhere in this row's folder. Sorting on it before the folder name
    /// makes the folder groups fall in recency order, which is what you want from
    /// "sort by date" — otherwise a folder last touched in June sits on top because its
    /// name starts with an A.
    /// </summary>
    public DateTime FolderRank { get; set; }

    // ----------------------------------------------------------------- branches

    /// <summary>Session this one was branched from. Null for a top-level session.</summary>
    public string? ParentId { get; set; }

    /// <summary>Parent's label, for the tooltip. Null when the parent is not in the list.</summary>
    public string? BranchOf { get; set; }

    /// <summary>
    /// Branch hops from the top-level session, and so how far the row indents. Counts only
    /// links whose parent is actually in the list: a branch whose parent transcript is gone
    /// keeps its marker but has nothing to indent under.
    /// </summary>
    public int Depth { get; set; }

    public bool IsBranch => ParentId is not null;

    public Thickness Indent => new(Depth * 15, 0, 0, 0);

    public Visibility BranchMarkVisibility => IsBranch ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Sort key that lands a branch directly beneath the session it came from. A parent's
    /// key is a prefix of every child's, and a prefix sorts first, so one ascending string
    /// comparison yields the whole tree in reading order. The leading timestamp is what
    /// orders threads against each other; the direction is baked into it, so this is sorted
    /// ascending whether you asked for newest or oldest first.
    /// </summary>
    public string ThreadOrder { get; set; } = "";

    // ------------------------------------------------------------------ status

    private PaneStatus? _status;

    public PaneStatus? Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            Raise(nameof(StatusBrush));
            Raise(nameof(StatusText));
            Raise(nameof(Subtitle));
        }
    }

    /// <summary>Status to brush key. Shared with the pane header's dot, which paints the
    /// same three states. Null means no pane, so nothing to report.</summary>
    public static string BrushKeyFor(PaneStatus? status) => status switch
    {
        PaneStatus.Working or PaneStatus.Starting => "Working",
        PaneStatus.NeedsInput => "NeedsInput",
        PaneStatus.Done => "Done",
        PaneStatus.Error => "Failed",
        PaneStatus.Exited => "Ended",
        _ => "Dormant"
    };

    public string StatusBrush => BrushKeyFor(Status);

    public string StatusText => Status switch
    {
        PaneStatus.Working => "running",
        PaneStatus.NeedsInput => "needs you",
        PaneStatus.Done => "done",
        PaneStatus.Error => "api error",
        PaneStatus.Exited => "shell closed",
        PaneStatus.Starting => "starting",
        _ => Age
    };

    // ----------------------------------------------------------------- renaming

    private bool _isEditing;
    private string _editName = "";

    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            if (_isEditing == value) return;
            _isEditing = value;
            if (value) _editName = CustomName ?? Name;
            Raise(nameof(IsEditing));
            Raise(nameof(EditName));
            Raise(nameof(LabelVisibility));
            Raise(nameof(EditorVisibility));
        }
    }

    public string EditName
    {
        get => _editName;
        set { _editName = value; Raise(nameof(EditName)); }
    }

    public Visibility LabelVisibility => _isEditing ? Visibility.Collapsed : Visibility.Visible;
    public Visibility EditorVisibility => _isEditing ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Called after anything that changes what the row should read.</summary>
    public void Refresh()
    {
        Raise(nameof(Name));
        Raise(nameof(Subtitle));
        Raise(nameof(Tip));
        Raise(nameof(AgentVisibility));
        Raise(nameof(StatusBrush));
        Raise(nameof(StatusText));
    }

    private void Raise([CallerMemberName] string? prop = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
}

/// <summary>Turns the brush key on a row into the actual brush.</summary>
public sealed class BrushKeyConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, System.Globalization.CultureInfo c) =>
        Application.Current?.TryFindResource(value as string ?? "Dormant") ?? Brushes.Transparent;

    public object ConvertBack(object value, Type t, object p, System.Globalization.CultureInfo c) =>
        throw new NotSupportedException();
}

public partial class MainWindow : Window
{
    private readonly IAgent _claude;
    private readonly IAgent _codex;

    private IAgent AgentFor(AgentKind kind) => kind == AgentKind.Codex ? _codex : _claude;

    /// <summary>Codex panes waiting for their transcript, in start order. See BindCodexPanes.</summary>
    private readonly List<(SessionRow Row, string? ParentId, DateTime Since)> _unbound = [];

    /// <summary>Both agents' transcripts, watched always; RefreshSessions skips an agent setup's Use left off.</summary>
    private ISessionSource[] Sources => [_claude.Sessions, _codex.Sessions];
    private readonly SessionNames _names = new();
    private readonly SessionParents _parents = new();

    // What each pane's status line was handed, and its folder's git state, for the header strip
    // and the footer's limits.
    private readonly StatusStore _status = new();
    private readonly GitInfo _git = new(TimeSpan.FromSeconds(3));
    private ClayoSettings _settings = ClayoSettings.Load(ClayoSettings.DefaultPath);
    private readonly System.Windows.Threading.DispatcherTimer _gitPoll = new() { Interval = TimeSpan.FromSeconds(3) };
    // The reset countdowns run down, and limits go stale, between Claude's reports.
    private readonly System.Windows.Threading.DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(30) };

    // Panes stay alive when you switch away, so switching back is instant and the
    // process keeps working in the background. PaneHost.Children is the set of them.

    // Rows are kept, not rebuilt, so a live status light does not flicker every time the
    // transcript watcher fires.
    private readonly Dictionary<string, SessionRow> _rows = new(StringComparer.OrdinalIgnoreCase);

    private TerminalPane? _active;
    private string _folder;

    /// <summary>Sessions with a live pane in this window. The island shows it when it peeks.</summary>
    public int OpenSessionCount => PaneHost.Children.OfType<TerminalPane>().Count();

    /// <summary>
    /// A pane entered a state worth telling you about (NeedsYou, Error, Done), or, with a null
    /// kind, moved on so whatever it said before is stale. The island listens.
    /// </summary>
    public event Action<TerminalPane, string, NoteKind?>? SessionNotice;

    /// <summary>A pane went to Working (true) or out of it, closing included. The island peeks on its own for it.</summary>
    public event Action<TerminalPane, bool>? SessionWorking;

    /// <summary>A limit went past the reserve set in Settings, once per limit window. The island listens.</summary>
    public event Action<Meter>? ReserveCrossed;

    private readonly ReserveWatch _reserve = new();

    public MainWindow(string folder, bool startSession)
    {
        _claude = ClaudeAgent.ForThisUser(() => _settings.ClaudePath);
        _codex = CodexAgent.ForThisUser(() => _settings.CodexPath);
        InitializeComponent();
        SettingsView.SetupRequested += () => ShowSetup();
        SettingsView.CloseRequested += HideSettings;
        SettingsView.SettingsChanged += s =>
        {
            _settings = s;
            s.Save(ClayoSettings.DefaultPath);
            ShowStatusBar();
            WarnReserve();
        };
        // Both fire on background threads, for any pane; only the active one's strip shows,
        // and the footer shows the newest limits whichever pane reported them.
        _status.Changed += _ => Dispatcher.BeginInvoke(() =>
        {
            ShowStatusBar();
            WarnReserve();
        });
        _git.Changed += (_, _) => Dispatcher.BeginInvoke(ShowStatusBar);
        _folder = folder;
        ShowFolder(folder);

        foreach (var source in Sources) source.Changed += () => Dispatcher.BeginInvoke(RefreshSessions);
        Loaded += (_, __) =>
        {
            RefreshSessions();
            foreach (var source in Sources) source.StartWatching();
            _status.Start();

            // A hidden window shows no branch, so it reads none.
            _gitPoll.Tick += (_, _) =>
            {
                if (!IsVisible) return;
                foreach (var pane in PaneHost.Children.OfType<TerminalPane>()) _git.Refresh(pane.WorkingDirectory);
            };
            _gitPoll.Start();
            // The reserve is checked hidden too: a script's cached limits change between Claude's reports.
            _clock.Tick += (_, _) =>
            {
                if (IsVisible) ShowStatusBar();
                WarnReserve();
            };
            _clock.Start();
            ShowStatusBar();

            // Opening Clayo in a folder should land you in a live session, not an
            // empty pane. Same thing the Explorer handoff does in AdoptFolder.
            if (startSession) StartNew(_folder);
        };
    }

    // ------------------------------------------------------------- title bar

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    /// <summary>Acrylic: blurs whatever is behind the window. Mica is 2, which tints
    /// from the wallpaper instead and is far subtler — swap this if that reads better.</summary>
    private const int DWMSBT_TRANSIENTWINDOW = 3;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        DarkCaption(hwnd);
        PlaceOnPrimary(hwnd);
    }

    /// <summary>The caption look, shared with the setup window.</summary>
    internal static void DarkCaption(IntPtr hwnd)
    {
        // A white system title bar above a near-black app reads as a bug. This is the
        // supported way to darken it without taking over the whole non-client area.
        int on = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));

        // Glass in the caption. Every Grid in MainWindow paints an opaque brush, so the
        // client area is unaffected and only the non-client strip DWM draws itself picks
        // the material up — glass at the top edge, the terminal below untouched.
        int backdrop = DWMSBT_TRANSIENTWINDOW;
        DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));

        // Default border is light, and against glass it draws a bright rim. COLORREF is
        // 0x00BBGGRR, so Hairline #262C35 goes in byte-reversed.
        int border = 0x00352C26;
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));

        // No return values checked on purpose: dwmapi answers E_INVALIDARG for an
        // attribute the running build doesn't know, so anything older than 22H2 keeps
        // the plain dark title bar instead of failing.
    }

    // -------------------------------------------------------------- placement

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
    private struct POINT { public int X, Y; }

    /// <summary>
    /// Point-based, not window-based. MonitorFromWindow's MONITOR_DEFAULTTOPRIMARY is only a
    /// fallback for a window that intersects no monitor at all — given a window already
    /// touching a screen it returns that screen and ignores the flag, which is exactly the
    /// case we are trying to correct. The primary monitor's origin is (0,0) by definition,
    /// so asking about that point names it unambiguously.
    /// </summary>
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT point, int flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    private const int MONITOR_DEFAULTTOPRIMARY = 1;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    /// <summary>
    /// Open centred on the primary monitor, at a size that fits there.
    ///
    /// This is what WindowStartupLocation="CenterScreen" is supposed to do and, on a desktop
    /// whose monitors run at different scaling, does not. Measured on a 2880x1800 primary at
    /// 200% next to a 1920x1080 secondary at 100%, it produced a 2560x1640 window — sized at
    /// the primary's scale — placed at 2560,-304: title bar above the desktop, body straddling
    /// both screens and running off the right edge. Nothing about that is recoverable with the
    /// mouse, because the bar you would drag it back by is the part that is off-screen.
    ///
    /// Nothing to do with the title bar; it reproduces with the stock system caption. So this
    /// stays regardless of what the chrome looks like.
    ///
    /// Measured against the work area, not the monitor bounds, so the taskbar cannot end up
    /// covering the caption.
    /// </summary>
    private static void PlaceOnPrimary(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var win)) return;

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        var monitor = MonitorFromPoint(default, MONITOR_DEFAULTTOPRIMARY);
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return;

        var work = info.rcWork;
        int workWidth = work.Right - work.Left;
        int workHeight = work.Bottom - work.Top;

        int width = Math.Min(win.Right - win.Left, workWidth);
        int height = Math.Min(win.Bottom - win.Top, workHeight);

        int x = work.Left + (workWidth - width) / 2;
        int y = work.Top + (workHeight - height) / 2;

        SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    // ------------------------------------------------------------------ folder

    /// <param name="prefill">Typed into Claude's input once it is up, not sent (see TerminalPane.Prefill).</param>
    public void AdoptFolder(string folder, string? prefill = null)
    {
        if (!Directory.Exists(folder)) return;
        _folder = folder;
        ShowFolder(folder);
        Reveal();
        // Setup came up instead: there may be no agent to start yet. New session will use the folder.
        if (!IsVisible) return;

        StartNew(folder, pane => pane.Prefill = prefill);
    }

    /// <summary>
    /// Brings the window forward from wherever it is: hidden (closed, or never shown after a
    /// login start), minimized, or behind. RestoreWindow rather than WindowState = Normal, so
    /// a window that was maximized before it was minimized comes back maximized.
    /// The setup window comes first while setup is needed (NeedsSetup), and this window
    /// once you press its Open Clayo.
    /// </summary>
    public void Reveal()
    {
        if (!IsVisible && NeedsSetup)
        {
            ShowSetup();
            return;
        }
        Show();
        if (WindowState == WindowState.Minimized) SystemCommands.RestoreWindow(this);
        Activate();
    }

    /// <summary>
    /// docs/SETUP.md D6: never set up, or none of the agents you use is where it was. Located
    /// only, not run, so asking costs a few file checks.
    /// </summary>
    private bool NeedsSetup
    {
        get
        {
            var detector = AgentDetector.ForThisMachine();
            return !_settings.SetupDone
                || !(_settings.UseClaude && detector.Locate(AgentKind.Claude, _settings.ClaudePath) is not null
                     || _settings.UseCodex && detector.Locate(AgentKind.Codex, _settings.CodexPath) is not null);
        }
    }

    private SetupWindow? _setup;

    /// <summary>
    /// The setup window, on top of this one when it is open (from Settings), else on its own.
    /// Closing it without Open Clayo changes nothing; the next Reveal asks again.
    /// </summary>
    private void ShowSetup()
    {
        if (_setup is not null)
        {
            _setup.Activate();
            return;
        }

        _setup = new SetupWindow(_settings);
        if (IsVisible)
        {
            _setup.Owner = this;
            _setup.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        _setup.Done += s =>
        {
            _settings = s;
            s.Save(ClayoSettings.DefaultPath);
            if (SettingsView.IsVisible) SettingsView.Refresh(s);
            RefreshSessions();
            Reveal();
        };
        _setup.Closed += (_, _) => _setup = null;
        _setup.Show();
    }

    private void ShowFolder(string folder)
    {
        CurrentFolderName.Text = Path.GetFileName(folder.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : folder;
        CurrentFolderPath.Text = folder;
    }

    // ---------------------------------------------------------------- sessions

    // ApplyFilter hands the ListBox a brand-new view, so every section GroupItem is
    // discarded and rebuilt. The expanded state therefore cannot live on the Expander:
    // it has to be kept out here and put back when the container reappears.
    private readonly Dictionary<string, bool> _sectionOpen =
        new(StringComparer.Ordinal) { ["Open"] = true, ["Today"] = true };

    private void Section_Loaded(object sender, RoutedEventArgs e)
    {
        var ex = (Expander)sender;
        ex.IsExpanded = _sectionOpen.TryGetValue(ex.Tag as string ?? "", out var open) && open;
    }

    private void Section_Toggled(object sender, RoutedEventArgs e)
    {
        var ex = (Expander)sender;
        _sectionOpen[ex.Tag as string ?? ""] = ex.IsExpanded;
    }

    private void RefreshSessions()
    {
        // Fold the transcripts into the rows we already have, so live panes keep their
        // identity (and their status light) across a refresh.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // With both agents in use, each row says whose it is.
        bool both = _settings.UseClaude && _settings.UseCodex;
        var codex = _settings.UseCodex ? _codex.Sessions.Scan() : [];
        var infos = (_settings.UseClaude ? _claude.Sessions.Scan() : []).Concat(codex);
        BindCodexPanes(codex);

        // A Codex pane's working / done is exact in its transcript (TerminalPane.ShowTurn).
        foreach (var info in codex)
            if (info.Turn is { } turn && _rows.TryGetValue(info.SessionId, out var open) && open.Pane is { } pane)
                pane.ShowTurn(turn, info.LastActivity);

        foreach (var info in infos)
        {
            seen.Add(info.SessionId);

            if (_rows.TryGetValue(info.SessionId, out var row))
            {
                row.Info = info;
                row.CustomName = _names.Get(info.SessionId);
                row.ShowAgent = both;
                row.Refresh();
            }
            else
            {
                _rows[info.SessionId] = new SessionRow
                {
                    SessionId = info.SessionId,
                    Info = info,
                    CustomName = _names.Get(info.SessionId),
                    Folder = info.ProjectDir,
                    ShowAgent = both
                };
            }
        }

        // Drop rows whose transcript vanished, unless they are open in this window.
        foreach (var gone in _rows.Where(kv => !seen.Contains(kv.Key) && !kv.Value.IsOpen)
                                  .Select(kv => kv.Key).ToList())
        {
            _rows.Remove(gone);
        }

        // Folder groups sort by their newest session, not alphabetically.
        foreach (var byFolder in _rows.Values.GroupBy(r => r.Project, StringComparer.OrdinalIgnoreCase))
        {
            var newest = byFolder.Max(r => r.When);
            foreach (var row in byFolder) row.FolderRank = newest;
        }

        ApplyFilter();
        // A Codex pane's strip is read from the transcript this refresh just read.
        ShowStatusBar();

        if (!Sources.Any(source => source.RootExists))
            EmptyState.Text = "No transcripts found under ~\\.claude or ~\\.codex.\nStart a session and it will show up here.";
    }

    /// <summary>
    /// Codex picks its own ids, so a new Codex pane or fork is re-keyed from its placeholder to
    /// the transcript it turns out to be, once that appears (docs/SETUP.md, "Finding a new
    /// Codex session"). Done before the rows are folded in, so the transcript lands on the
    /// pane's row instead of growing a second one.
    /// </summary>
    private void BindCodexPanes(IReadOnlyList<SessionInfo> codex)
    {
        foreach (var wait in _unbound.ToList())
        {
            var taken = PaneHost.Children.OfType<TerminalPane>().Select(p => p.SessionId).OfType<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (CodexSessionStore.FindStarted(codex, wait.Row.Folder, wait.ParentId, wait.Since, taken) is not { } found)
                continue;

            _unbound.Remove(wait);
            _rows.Remove(wait.Row.SessionId);
            wait.Row.SessionId = found.SessionId;
            wait.Row.Pane!.SessionId = found.SessionId;
            _rows[found.SessionId] = wait.Row;
            if (ReferenceEquals(wait.Row.Pane, _active)) ForkButton.IsEnabled = true;
        }
    }

    /// <summary>Rows, order and grouping the ListBox currently shows. See ApplyFilter.</summary>
    private string? _shownLayout;

    private void ApplyFilter()
    {
        var selected = (Sessions.SelectedItem as SessionRow)?.SessionId;
        var q = Filter.Text.Trim();

        IEnumerable<SessionRow> rows = _rows.Values;
        if (q.Length > 0)
        {
            rows = rows.Where(r =>
                r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                r.Project.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                r.SessionId.StartsWith(q, StringComparison.OrdinalIgnoreCase));
        }

        bool byFolder = GroupBy.SelectedIndex == 0;
        int sort = SortBy.SelectedIndex;   // 0 newest, 1 oldest, 2 name

        LinkThreads(newestFirst: sort != 1);

        var view = new CollectionViewSource { Source = rows.ToList() };

        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SessionRow.Bucket)));
        if (byFolder)
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SessionRow.Project)));

        // Order matters: every group key has to be sorted before the keys nested inside it,
        // or WPF splits one logical group into several.
        view.SortDescriptions.Add(new SortDescription(nameof(SessionRow.BucketOrder), ListSortDirection.Ascending));

        if (byFolder)
        {
            if (sort == 2)
            {
                view.SortDescriptions.Add(new SortDescription(nameof(SessionRow.Project), ListSortDirection.Ascending));
            }
            else
            {
                view.SortDescriptions.Add(new SortDescription(nameof(SessionRow.FolderRank),
                    sort == 1 ? ListSortDirection.Ascending : ListSortDirection.Descending));
                // Tie-break, or two folders touched in the same tick interleave.
                view.SortDescriptions.Add(new SortDescription(nameof(SessionRow.Project), ListSortDirection.Ascending));
            }
        }

        // Sorting by name is alphabetical and nothing else, so a branch keeps its marker
        // and indent but sits wherever its name falls. Both date orders thread instead:
        // ThreadOrder already carries the direction, hence ascending either way.
        view.SortDescriptions.Add(sort == 2
            ? new SortDescription(nameof(SessionRow.Name), ListSortDirection.Ascending)
            : new SortDescription(nameof(SessionRow.ThreadOrder), ListSortDirection.Ascending));

        // The transcript watcher lands here every few seconds while Claude works. Handing
        // the ListBox a new view rebuilds every row and section and jumps the list to the
        // top, so the sidebar blinked on each write. Row text and status lights already
        // update in place, so only swap the view when the rows, their order, or their
        // grouping differ from what is on screen.
        var layout = string.Join("\n", view.View.Cast<SessionRow>().Select(r =>
            $"{r.SessionId}|{r.Bucket}|{(byFolder ? r.Project : "")}|{r.Depth}|{r.ParentId}"));
        if (layout == _shownLayout) return;
        _shownLayout = layout;

        Sessions.ItemsSource = view.View;

        if (selected is not null)
        {
            Sessions.SelectedItem = Sessions.Items.OfType<SessionRow>()
                .FirstOrDefault(r => r.SessionId == selected);
        }

        // Rebuilding the view can leave the ScrollViewer parked mid-list, which hides the
        // Open header. Put it back at the top.
        Sessions.Dispatcher.BeginInvoke(() =>
        {
            if (VisualTreeHelper.GetChildrenCount(Sessions) == 0) return;
            var border = VisualTreeHelper.GetChild(Sessions, 0);
            if (VisualTreeHelper.GetChildrenCount(border) == 0) return;
            (VisualTreeHelper.GetChild(border, 0) as ScrollViewer)?.ScrollToTop();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Deep enough for any real branching, and the thing that stops a parents.json
    /// describing a cycle from spinning the walk below forever.</summary>
    private const int MaxBranchDepth = 8;

    /// <summary>
    /// Hangs every branch off the session it came from: the depth its row indents by, and
    /// the key that keeps it directly beneath its parent once the view sorts.
    /// </summary>
    private void LinkThreads(bool newestFirst)
    {
        foreach (var row in _rows.Values)
        {
            // Codex says in the transcript; Claude's links are only the ones Clayo recorded.
            row.ParentId = row.Info?.ParentId ?? _parents.Get(row.SessionId);
            row.BranchOf = row.ParentId is { } pid && _rows.TryGetValue(pid, out var parent)
                ? parent.Name
                : null;
        }

        foreach (var row in _rows.Values)
        {
            // Climb to the top-level session, collecting the line of descent. Stops early
            // on a parent that is not in the list, which leaves the row at depth 0.
            var chain = new List<SessionRow> { row };
            var walk = row;

            while (chain.Count <= MaxBranchDepth
                   && walk.ParentId is { } pid
                   && _rows.TryGetValue(pid, out var parent))
            {
                chain.Add(parent);
                walk = parent;
            }

            chain.Reverse();
            row.Depth = chain.Count - 1;
            row.ThreadOrder = string.Concat(
                chain.Select(r => Stamp(r.When, newestFirst) + r.SessionId + "/"));
        }
    }

    /// <summary>Fixed width, so comparing the strings orders them the way the numbers do.</summary>
    private static string Stamp(DateTime when, bool newestFirst) =>
        (newestFirst ? DateTime.MaxValue.Ticks - when.Ticks : when.Ticks).ToString("D19");

    private void Filter_TextChanged(object sender, TextChangedEventArgs e)
    {
        SyncSearchHint();
        ApplyFilter();
    }

    private void Filter_FocusChanged(object sender, RoutedEventArgs e) => SyncSearchHint();

    /// <summary>
    /// The hint gets out of the way as soon as the caret lands, not only once you have
    /// typed something — a placeholder sitting behind a live caret reads as real text you
    /// have to delete. It comes back on blur if the box was left empty.
    /// </summary>
    private void SyncSearchHint() =>
        SearchHint.Visibility = Filter.Text.Length == 0 && !Filter.IsKeyboardFocusWithin
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void View_Changed(object sender, SelectionChangedEventArgs e)
    {
        // Fires during InitializeComponent, before the rest of the sidebar exists.
        if (!IsLoaded || Filter is null) return;
        ApplyFilter();
    }

    private void Sessions_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var row = Sessions.SelectedItem as SessionRow;

        // Leaving a row cancels an edit in progress rather than silently keeping it open.
        foreach (var other in e.RemovedItems.OfType<SessionRow>()) other.IsEditing = false;

        if (row?.Pane is not null) Activate(row.Pane, row.Name);
    }

    // -------------------------------------------------------------- open/close

    /// <summary>Double-click is how you open things in Explorer, so it opens them here too.</summary>
    private void Sessions_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Sessions.SelectedItem is SessionRow) OpenSelected();
    }

    private void Sessions_KeyDown(object sender, KeyEventArgs e)
    {
        if (Sessions.SelectedItem is not SessionRow row) return;

        if (e.Key == Key.F2) { row.IsEditing = true; e.Handled = true; }
        else if (e.Key == Key.Enter && !row.IsEditing) { OpenSelected(); e.Handled = true; }
    }

    private void Open_Click(object sender, RoutedEventArgs e) => OpenSelected();

    private void OpenSelected()
    {
        if (Sessions.SelectedItem is not SessionRow row) return;

        if (row.Pane is not null)
        {
            Activate(row.Pane, row.Name);
            return;
        }

        var cwd = Directory.Exists(row.Folder) ? row.Folder : _folder;
        StartPane(SessionLauncher.Plan(AgentFor(row.Agent), LaunchMode.Resume, cwd, row.SessionId), row.Name);
    }

    private void NewSession_Click(object sender, RoutedEventArgs e)
    {
        RecentFolders.ItemsSource = RecentFolderList(8);

        // Cleared so the next pick raises SelectionChanged even if it is the same row
        // the last one was, and so nothing sits pre-highlighted.
        RecentFolders.SelectedIndex = -1;
        FolderPopup.IsOpen = true;
    }

    /// <summary>
    /// Folders we already have sessions in, most recently touched first, with wherever
    /// this window is pointed pinned to the top. Read off the rows already in memory
    /// rather than <see cref="SessionStore.Scan"/>, which re-reads every transcript.
    /// </summary>
    private List<FolderChoice> RecentFolderList(int max)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var choices = new List<FolderChoice>();

        void Add(string folder)
        {
            if (choices.Count >= max) return;
            if (folder.Length == 0 || !seen.Add(folder)) return;
            if (!Directory.Exists(folder)) return;
            choices.Add(new FolderChoice(FolderLeaf(folder), folder));
        }

        Add(_folder);
        foreach (var row in _rows.Values.OrderByDescending(r => r.When)) Add(row.Folder);

        return choices;
    }

    private void RecentFolder_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (RecentFolders.SelectedItem is FolderChoice choice) SpawnIn(choice.FullPath);
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        FolderPopup.IsOpen = false;

        var dialog = new OpenFolderDialog
        {
            Title = "Start a session in",
            InitialDirectory = Directory.Exists(_folder) ? _folder : ""
        };

        if (dialog.ShowDialog(this) == true) SpawnIn(dialog.FolderName);
    }

    /// <summary>
    /// Starts a session in <paramref name="folder"/> and points the window at it, so the
    /// header and the next new session agree with where you just launched.
    /// </summary>
    private void SpawnIn(string folder, Action<TerminalPane>? then = null)
    {
        FolderPopup.IsOpen = false;
        if (!Directory.Exists(folder)) return;

        _folder = folder;
        ShowFolder(folder);

        StartNew(folder, then);
    }

    /// <summary>
    /// A new session with the agent setup picked for New session, or, set to ask each time,
    /// the one you pick from a two-item menu over the New session button. Closing the menu
    /// starts nothing. then gets the pane once it exists.
    /// </summary>
    private void StartNew(string folder, Action<TerminalPane>? then = null)
    {
        void Start(IAgent agent)
        {
            var pane = StartPane(SessionLauncher.Plan(agent, LaunchMode.New, folder), $"new · {FolderLeaf(folder)}");
            then?.Invoke(pane);
        }

        if (_settings.NewSessionKind is { } kind)
        {
            Start(AgentFor(kind));
            return;
        }

        var menu = new ContextMenu { PlacementTarget = NewButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        foreach (var agent in new[] { _claude, _codex })
        {
            var item = new MenuItem { Header = SetupScreen.Name(agent.Kind) };
            item.Click += (_, _) => Start(agent);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    /// <summary>
    /// Starts a session in the picked file's folder and types the file's path at the prompt,
    /// unsent, so you can say what to do with it. Typed on the first Done, the session's first
    /// settle at its prompt: earlier and the keys would reach a CLI still starting up, or a
    /// trust prompt (NeedsInput) instead.
    /// </summary>
    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open a file in a new session",
            InitialDirectory = Directory.Exists(_folder) ? _folder : ""
        };

        if (dialog.ShowDialog(this) != true) return;

        var file = dialog.FileName;
        SpawnIn(Path.GetDirectoryName(file)!, pane =>
        {
            EventHandler<PaneStatus>? ready = null;
            ready = (_, status) =>
            {
                if (status != PaneStatus.Done) return;
                pane.StatusChanged -= ready;
                pane.InsertPaths([file]);
            };
            pane.StatusChanged += ready;
        });
    }

    private static string FolderLeaf(string folder) =>
        Path.GetFileName(folder.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : folder;

    /// <summary>
    /// Hands Claude the files you pick by typing their paths at the prompt — it reads a
    /// path it is given, so there is nothing to upload. Ctrl+V over files copied in
    /// Explorer does the same thing, in the pane itself.
    /// </summary>
    private void Attach_Click(object sender, RoutedEventArgs e)
    {
        if (_active is null) return;

        var dialog = new OpenFileDialog
        {
            Title = "Attach files",
            Multiselect = true,
            InitialDirectory = Directory.Exists(_active.WorkingDirectory)
                ? _active.WorkingDirectory
                : ""
        };

        if (dialog.ShowDialog(this) != true) return;

        _active.InsertPaths(dialog.FileNames);
        _active.FocusTerminal();
    }

    private void Fork_Click(object sender, RoutedEventArgs e)
    {
        var parentId = _active?.SessionId;
        if (parentId is null) return;

        var cwd = _active!.WorkingDirectory;
        var plan = SessionLauncher.Plan(AgentFor(_active.Agent), LaunchMode.Fork, cwd, parentId);

        // Record the link now: the fork rewrites sessionId on every line it copies, so
        // once this returns there is nothing left anywhere that says the two are related.
        // A Codex fork names its parent itself, and has no id of ours yet.
        if (plan.ExpectedSessionId is { } childId) _parents.Set(childId, parentId);

        // A Claude child's id was allocated by us, so the branch is addressable before
        // the transcript for it exists on disk.
        StartPane(plan, $"branch of {Short(parentId)}", forkOf: parentId);
    }

    private void ClosePane_Click(object sender, RoutedEventArgs e)
    {
        if (_active is null) return;
        ClosePane(_active);
    }

    /// <summary>
    /// Closing a session ends the process and drops the row out of Open — it reappears
    /// under Today, because by now it has a transcript.
    /// </summary>
    private void ClosePane(TerminalPane pane)
    {
        pane.Close();
        PaneHost.Children.Remove(pane);
        // Nothing left to jump to, so a pending note about it would only mislead.
        SessionNotice?.Invoke(pane, "", null);
        SessionWorking?.Invoke(pane, false);
        if (_rows.Values.FirstOrDefault(r => ReferenceEquals(r.Pane, pane)) is { } row)
        {
            row.Pane = null;
            row.Status = null;
            row.Refresh();

            // A Codex pane closed before its first message never got a transcript, so there
            // is no session to list.
            if (_unbound.RemoveAll(w => ReferenceEquals(w.Row, row)) > 0) _rows.Remove(row.SessionId);
        }

        _active = PaneHost.Children.OfType<TerminalPane>().LastOrDefault();

        if (_active is not null)
        {
            var title = _active.SessionId is { } aid && _rows.TryGetValue(aid, out var r)
                ? r.Name
                : "session";
            Activate(_active, title);
        }
        else
        {
            PaneTitle.Text = "";
            ShowStatusBar();
            ForkButton.IsEnabled = false;
            AttachButton.IsEnabled = false;
            CloseButton.IsEnabled = false;
            StatusDot.Fill = (Brush)FindResource("Dormant");
            EmptyState.Visibility = Visibility.Visible;
        }

        ApplyFilter();
    }

    // -------------------------------------------------------------- renaming

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (Sessions.SelectedItem is SessionRow row) row.IsEditing = true;
    }

    private void ResetName_Click(object sender, RoutedEventArgs e)
    {
        if (Sessions.SelectedItem is not SessionRow row) return;
        _names.Set(row.SessionId, null);
        row.CustomName = null;
        row.Refresh();
    }

    private void RenameBox_Loaded(object sender, RoutedEventArgs e)
    {
        // The box is created the moment editing starts, so this is where focus belongs.
        if (sender is TextBox box && box.IsVisible)
        {
            box.Focus();
            box.SelectAll();
        }
    }

    private void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || box.DataContext is not SessionRow row) return;

        if (e.Key == Key.Enter)
        {
            CommitRename(row, box.Text);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            row.IsEditing = false;
            e.Handled = true;
        }
    }

    private void RenameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box && box.DataContext is SessionRow row && row.IsEditing)
            CommitRename(row, box.Text);
    }

    private void CommitRename(SessionRow row, string text)
    {
        text = text.Trim();

        // Typing the generated title back in is the same as having no override.
        var custom = text.Length == 0 || text == (row.Info?.AiTitle ?? "") ? null : text;

        _names.Set(row.SessionId, custom);
        row.CustomName = custom;
        row.IsEditing = false;
        row.Refresh();

        if (ReferenceEquals(row.Pane, _active)) PaneTitle.Text = row.Name;
    }

    // ------------------------------------------------------------------- panes

    /// <param name="forkOf">The parent, for a fork: what a Codex fork's transcript is found by.</param>
    private TerminalPane StartPane(LaunchPlan plan, string title, string? forkOf = null)
    {
        var since = DateTime.Now;
        var pane = new TerminalPane(plan);
        var id = pane.SessionId ?? Guid.NewGuid().ToString();

        // Give the session a row immediately, before any transcript exists, so it shows
        // up under Open the instant it starts.
        if (!_rows.TryGetValue(id, out var row))
        {
            row = new SessionRow
            {
                SessionId = id,
                Folder = plan.WorkingDirectory,
                CustomName = _names.Get(id),
                FallbackTitle = title
            };
            _rows[id] = row;
        }

        row.Pane = pane;
        row.Folder = plan.WorkingDirectory;
        row.Status = PaneStatus.Starting;
        row.Refresh();
        if (pane.SessionId is null) _unbound.Add((row, forkOf, since));

        pane.StatusChanged += (_, status) => Dispatcher.BeginInvoke(() =>
        {
            var was = row.Status;
            row.Status = status;
            if (ReferenceEquals(pane, _active)) PaintStatus(status);

            // StatusChanged only fires on a real change, so a repeat cannot notify twice.
            // Done counts only after work: the first settle out of Starting is the shell or a
            // resumed session reaching its prompt, which is nothing you are waiting for. A
            // question or an error is worth knowing whenever it comes, a trust prompt at
            // launch included. Any other state means the session moved on.
            NoteKind? kind = status switch
            {
                PaneStatus.NeedsInput => NoteKind.NeedsYou,
                PaneStatus.Error => NoteKind.Error,
                PaneStatus.Done when was == PaneStatus.Working => NoteKind.Done,
                _ => null,
            };
            SessionNotice?.Invoke(pane, row.Name, kind);
            if (status == PaneStatus.Working) SessionWorking?.Invoke(pane, true);
            else if (was == PaneStatus.Working) SessionWorking?.Invoke(pane, false);
        });

        PaneHost.Children.Add(pane);
        Activate(pane, row.Name);
        ApplyFilter();
        return pane;
    }

    /// <summary>Switches to this pane, for the island's click. Ignored if it was closed meanwhile.</summary>
    public void ShowSession(TerminalPane pane)
    {
        if (!PaneHost.Children.Contains(pane)) return;
        var row = _rows.Values.FirstOrDefault(r => ReferenceEquals(r.Pane, pane));
        Activate(pane, row?.Name ?? "session");
    }

    private void Activate(TerminalPane pane, string title)
    {
        // Picking a session, or starting one, is leaving Settings.
        if (SettingsView.IsVisible) HideSettings();
        foreach (UIElement child in PaneHost.Children)
            child.Visibility = ReferenceEquals(child, pane) ? Visibility.Visible : Visibility.Collapsed;

        _active = pane;
        PaneTitle.Text = title;
        ForkButton.IsEnabled = pane.SessionId is not null;
        AttachButton.IsEnabled = true;
        CloseButton.IsEnabled = true;
        EmptyState.Visibility = Visibility.Collapsed;
        PaintStatus(pane.Status);
        // Read now rather than on the next poll, so the branch shows as the pane does.
        _git.Refresh(pane.WorkingDirectory);
        ShowStatusBar();
        pane.FocusTerminal();
    }

    private void PaintStatus(PaneStatus status) =>
        StatusDot.Fill = (Brush)FindResource(SessionRow.BrushKeyFor(status));

    /// <summary>
    /// The active pane's strip and the footer's limits. A pane Claude has not reported on yet,
    /// or none at all, shows an empty strip, not zeros; the limits are the account's, so they
    /// show whichever pane is open. Each agent has its own account: the footer shows the open
    /// pane's agent's, or Codex's with no pane open when Codex is the only one in use.
    /// </summary>
    private void ShowStatusBar()
    {
        var now = DateTimeOffset.Now;
        bool codex = _active is null ? !_settings.UseClaude : _active.Agent == AgentKind.Codex;
        var limits = codex ? CodexLimits() : _status.Limits;
        HeadStrip.Show(_active?.SessionId is { } id
            ? StatusStrip.For(codex ? CodexStatus(id) : _status.Get(id), _git.Get(_active.WorkingDirectory), limits, _settings, now)
            : null, _settings.Theme);
        FootLimits.Show(StatusStrip.Footer(limits, _settings, now), _settings.Theme);
    }

    /// <summary>A Codex pane's status, from what its transcript says (CodexSessionStore).</summary>
    private SessionStatus? CodexStatus(string id) =>
        _rows.TryGetValue(id, out var row) ? row.Info?.Status : null;

    /// <summary>
    /// Codex's account limits: the newest any of its transcripts reported, not the open
    /// session's, which are as old as its last turn.
    /// </summary>
    private AccountLimits? CodexLimits() => StatusStore.Newest(
        _rows.Values.Where(r => r.Info?.Agent == AgentKind.Codex)
                    .Select(r => r.Info!.Status).OfType<SessionStatus>());

    /// <summary>
    /// Tells the island about a limit that has just gone past the reserve. Only a warning:
    /// prompts are never held back.
    /// </summary>
    private void WarnReserve()
    {
        foreach (var m in _reserve.Crossed(_status.Limits, _settings, DateTimeOffset.Now)) ReserveCrossed?.Invoke(m);
    }

    private static string Short(string id) => id.Length > 8 ? id[..8] : id;

    private bool _quitting;

    /// <summary>Ends Clayo: every session closes and the app exits. The island's and the gear's Quit.</summary>
    public void Quit()
    {
        _quitting = true;
        Close();
    }

    // ---------------------------------------------------------------- settings

    // The gear's menu and the island's carry the same "Start at login" item; both call these.

    /// <summary>
    /// Read fresh each time a menu opens: the entry can also be removed outside Clayo. A dev
    /// build shows what the installed Clayo set but cannot change it (LoginStartup.CanWrite).
    /// </summary>
    internal static void ShowLoginState(MenuItem item)
    {
        var startup = LoginStartup.ForThisUser();
        item.IsChecked = startup.IsOn;
        item.IsEnabled = startup.CanWrite;
    }

    /// <summary>A checkable item has already flipped IsChecked by the time Click arrives.</summary>
    internal static void ApplyLoginState(MenuItem item)
    {
        if (Environment.ProcessPath is { } exe) LoginStartup.ForThisUser().Set(item.IsChecked, exe);
    }

    // A left click opens the menu above the gear, as the folder picker opens above New session.
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        SettingsMenu.PlacementTarget = SettingsButton;
        SettingsMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        SettingsMenu.IsOpen = true;
    }

    private void SettingsMenu_Opened(object sender, RoutedEventArgs e) => ShowLoginState(SettingsLoginItem);

    private void OpenSettings_Click(object sender, RoutedEventArgs e) => ShowSettings();

    /// <summary>
    /// Settings takes the terminal side's place. The terminals are WebView2 windows that no WPF
    /// element can draw over, so they are hidden meanwhile; their sessions keep running.
    /// </summary>
    private void ShowSettings()
    {
        if (SettingsView.IsVisible) return;
        SettingsView.Refresh(_settings);
        PaneHost.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Visible;
        SettingsView.Focus();
    }

    /// <summary>Back to the session you were on, or to the empty state if there is none.</summary>
    private void HideSettings()
    {
        SettingsView.Visibility = Visibility.Collapsed;
        PaneHost.Visibility = Visibility.Visible;
        _active?.FocusTerminal();
    }

    // Ctrl+, opens Settings, Esc closes it. Previewed at the window, so they work wherever the
    // focus is in Clayo's own controls. A terminal keeps its keys: inside one, Esc belongs to
    // Claude, and Ctrl+, reaches only xterm.
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.OemComma && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ShowSettings();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && SettingsView.IsVisible)
        {
            HideSettings();
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }

    private void SettingsLogin_Click(object sender, RoutedEventArgs e) => ApplyLoginState(SettingsLoginItem);

    private void SettingsQuit_Click(object sender, RoutedEventArgs e) => Quit();

    /// <summary>
    /// Closing only hides. The sessions keep running, so a long task is not lost to a reflex
    /// click on X and Remote Control can keep reaching them; the island brings the window back.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_quitting)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        foreach (var source in Sources) source.StopWatching();
        _status.Dispose();
        _gitPoll.Stop();
        _clock.Stop();
        foreach (var pane in PaneHost.Children.OfType<TerminalPane>()) pane.Close();
        base.OnClosing(e);
    }
}

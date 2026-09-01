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

namespace CcxShell;

/// <summary>
/// One row in the sidebar. Backed by a transcript, a live pane, or both — a session you
/// started in this window has a pane immediately and grows a transcript a moment later.
/// </summary>
public sealed class SessionRow : INotifyPropertyChanged
{
    public required string SessionId { get; init; }

    /// <summary>Null until the transcript for this session shows up on disk.</summary>
    public SessionInfo? Info { get; set; }

    /// <summary>Non-null while the session is open in this window.</summary>
    public TerminalPane? Pane { get; set; }

    /// <summary>Used for the row label before any transcript exists.</summary>
    public string FallbackTitle { get; set; } = "new session";

    public string? CustomName { get; set; }
    public string Folder { get; set; } = "";

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

    // ------------------------------------------------------------------ status

    private PaneStatus? _status;

    public PaneStatus? Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            Raise(nameof(Status));
            Raise(nameof(StatusBrush));
            Raise(nameof(StatusText));
            Raise(nameof(Subtitle));
        }
    }

    public string StatusBrush => Status switch
    {
        PaneStatus.Working => "Working",
        PaneStatus.Idle => "NeedsInput",
        PaneStatus.Exited => "Ended",
        PaneStatus.Starting => "Working",
        _ => "Dormant"
    };

    public string StatusText => Status switch
    {
        PaneStatus.Working => "running",
        PaneStatus.Idle => "needs you",
        PaneStatus.Exited => "finished",
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
        Raise(nameof(Age));
        Raise(nameof(StatusBrush));
        Raise(nameof(StatusText));
    }

    private void Raise([CallerMemberName] string? prop = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
}

/// <summary>Turns the brush key on a row into the actual brush.</summary>
public sealed class BrushKeyConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type t, object p, System.Globalization.CultureInfo c) =>
        Application.Current?.TryFindResource(value as string ?? "Dormant") ?? Brushes.Transparent;

    public object ConvertBack(object value, Type t, object p, System.Globalization.CultureInfo c) =>
        throw new NotSupportedException();
}

public partial class MainWindow : Window
{
    private readonly SessionStore _store = new();
    private readonly SessionNames _names = new();

    // Panes stay alive when you switch away, so switching back is instant and the
    // process keeps working in the background.
    private readonly Dictionary<string, TerminalPane> _panes = new(StringComparer.OrdinalIgnoreCase);

    // Rows are kept, not rebuilt, so a live status light does not flicker every time the
    // transcript watcher fires.
    private readonly Dictionary<string, SessionRow> _rows = new(StringComparer.OrdinalIgnoreCase);

    private TerminalPane? _active;
    private string _folder;

    public MainWindow(string folder)
    {
        InitializeComponent();
        _folder = folder;
        ShowFolder(folder);

        _store.Changed += () => Dispatcher.BeginInvoke(RefreshSessions);
        Loaded += (_, __) =>
        {
            RefreshSessions();
            _store.StartWatching();

            // Opening Clayo in a folder should land you in a live session, not an
            // empty pane. Same thing the Explorer handoff does in AdoptFolder.
            StartPane(SessionLauncher.Plan(LaunchMode.New, _folder),
                      title: $"new · {Path.GetFileName(_folder.TrimEnd('\\'))}");
        };
    }

    // ------------------------------------------------------------- title bar

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // A white system title bar above a near-black app reads as a bug. This is the
        // supported way to darken it without taking over the whole non-client area.
        var hwnd = new WindowInteropHelper(this).Handle;
        int on = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
    }

    // ------------------------------------------------------------------ folder

    public void AdoptFolder(string folder)
    {
        if (!Directory.Exists(folder)) return;
        _folder = folder;
        ShowFolder(folder);

        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();

        StartPane(SessionLauncher.Plan(LaunchMode.New, folder),
                  title: $"new · {Path.GetFileName(folder.TrimEnd('\\'))}");
    }

    private void ShowFolder(string folder)
    {
        CurrentFolderName.Text = Path.GetFileName(folder.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : folder;
        CurrentFolderPath.Text = folder;
    }

    // ---------------------------------------------------------------- sessions

    private void RefreshSessions()
    {
        // Fold the transcripts into the rows we already have, so live panes keep their
        // identity (and their status light) across a refresh.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var info in _store.Scan())
        {
            seen.Add(info.SessionId);

            if (_rows.TryGetValue(info.SessionId, out var row))
            {
                row.Info = info;
                row.CustomName = _names.Get(info.SessionId);
                row.Refresh();
            }
            else
            {
                _rows[info.SessionId] = new SessionRow
                {
                    SessionId = info.SessionId,
                    Info = info,
                    CustomName = _names.Get(info.SessionId),
                    Folder = info.ProjectDir
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

        if (!_store.RootExists)
            EmptyState.Text = "No transcripts found under ~\\.claude\\projects.\nStart a session and it will show up here.";
    }

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

        view.SortDescriptions.Add(sort switch
        {
            1 => new SortDescription(nameof(SessionRow.When), ListSortDirection.Ascending),
            2 => new SortDescription(nameof(SessionRow.Name), ListSortDirection.Ascending),
            _ => new SortDescription(nameof(SessionRow.When), ListSortDirection.Descending)
        });

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

    private void Filter_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = Filter.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyFilter();
    }

    private void View_Changed(object sender, SelectionChangedEventArgs e)
    {
        // Fires during InitializeComponent, before the rest of the sidebar exists.
        if (!IsLoaded || Filter is null) return;
        ApplyFilter();
    }

    private void Sessions_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var row = Sessions.SelectedItem as SessionRow;
        ResumeButton.IsEnabled = row is not null;

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
    private void Resume_Click(object sender, RoutedEventArgs e) => OpenSelected();

    private void OpenSelected()
    {
        if (Sessions.SelectedItem is not SessionRow row) return;

        if (row.Pane is not null)
        {
            Activate(row.Pane, row.Name);
            return;
        }

        var cwd = Directory.Exists(row.Folder) ? row.Folder : _folder;
        StartPane(SessionLauncher.Plan(LaunchMode.Resume, cwd, row.SessionId), row.Name);
    }

    private void NewSession_Click(object sender, RoutedEventArgs e)
    {
        StartPane(SessionLauncher.Plan(LaunchMode.New, _folder),
                  title: $"new · {Path.GetFileName(_folder.TrimEnd('\\'))}");
    }

    private void Fork_Click(object sender, RoutedEventArgs e)
    {
        var parentId = _active?.SessionId;
        if (parentId is null) return;

        var cwd = _active!.WorkingDirectory;
        var plan = SessionLauncher.Plan(LaunchMode.Fork, cwd, parentId);

        // The child's id was allocated by us, so the branch is addressable before
        // the transcript for it exists on disk.
        StartPane(plan, $"branch of {Short(parentId)}");
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
        var id = pane.SessionId;

        pane.Close();
        PaneHost.Children.Remove(pane);
        if (id is not null)
        {
            _panes.Remove(id);
            if (_rows.TryGetValue(id, out var row))
            {
                row.Pane = null;
                row.Status = null;
                row.Refresh();
            }
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
            ForkButton.IsEnabled = false;
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

    private void StartPane(LaunchPlan plan, string title)
    {
        var pane = new TerminalPane(plan, plan.ExpectedSessionId);
        var id = pane.SessionId ?? Guid.NewGuid().ToString();
        _panes[id] = pane;

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

        pane.StatusChanged += (_, status) => Dispatcher.BeginInvoke(() =>
        {
            row.Status = status;
            if (ReferenceEquals(pane, _active)) PaintStatus(status);
        });

        PaneHost.Children.Add(pane);
        Activate(pane, row.Name);
        ApplyFilter();
    }

    private void Activate(TerminalPane pane, string title)
    {
        foreach (UIElement child in PaneHost.Children)
            child.Visibility = ReferenceEquals(child, pane) ? Visibility.Visible : Visibility.Collapsed;

        _active = pane;
        PaneTitle.Text = title;
        ForkButton.IsEnabled = pane.SessionId is not null;
        CloseButton.IsEnabled = true;
        EmptyState.Visibility = Visibility.Collapsed;
        PaintStatus(pane.Status);
        pane.FocusTerminal();
    }

    private void PaintStatus(PaneStatus status)
    {
        StatusDot.Fill = (Brush)FindResource(status switch
        {
            PaneStatus.Working => "Working",
            PaneStatus.Idle => "NeedsInput",
            PaneStatus.Exited => "Ended",
            _ => "Working"
        });
    }

    private static string Short(string id) => id.Length > 8 ? id[..8] : id;

    protected override void OnClosing(CancelEventArgs e)
    {
        _store.StopWatching();
        foreach (var pane in _panes.Values) pane.Close();
        base.OnClosing(e);
    }
}

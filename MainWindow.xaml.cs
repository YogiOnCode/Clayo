using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using CcxShell.Core;
using CcxShell.UI;

namespace CcxShell;

/// <summary>View wrapper so the sidebar can show a relative age without a converter.</summary>
public sealed class SessionRow
{
    public required SessionInfo Info { get; init; }

    public string Preview => Info.Preview;
    public string Project => Info.ProjectName;
    public DateTime When => Info.LastActivity;
    public string Age => Relative(Info.LastActivity);
    public string Tip => $"{Info.Preview}\n{Info.ProjectDir}\n{Info.SessionId}";

    /// <summary>Today's work stays open; everything older folds away under "Earlier".</summary>
    public string Bucket => Info.LastActivity.Date == DateTime.Today ? "Today" : "Earlier";

    /// <summary>Groups form in item order, so sorting on this is what puts Today first.</summary>
    public int BucketOrder => Info.LastActivity.Date == DateTime.Today ? 0 : 1;

    /// <summary>
    /// Newest activity anywhere in this row's folder. Sorting on it before the folder name
    /// makes the folder groups themselves fall in recency order, which is what you want
    /// from "sort by date" — otherwise a folder last touched in June sits at the top
    /// because its name starts with an A.
    /// </summary>
    public DateTime FolderRank { get; set; }

    private static string Relative(DateTime when)
    {
        var d = DateTime.Now - when;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours} h ago";
        if (d.TotalDays < 7) return $"{(int)d.TotalDays} d ago";
        return when.ToString("d MMM");
    }
}

public partial class MainWindow : Window
{
    private readonly SessionStore _store = new();

    // Panes stay alive when you switch away, so switching back is instant and the
    // process keeps working in the background.
    private readonly Dictionary<string, TerminalPane> _panes = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<SessionRow> _all = new();
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
            StartPane(SessionLauncher.Plan(LaunchMode.New, _folder), sessionId: null,
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

        StartPane(SessionLauncher.Plan(LaunchMode.New, folder), sessionId: null,
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
        var selected = (Sessions.SelectedItem as SessionRow)?.Info.SessionId;

        _all.Clear();
        foreach (var s in _store.Scan())
            _all.Add(new SessionRow { Info = s });

        // Stamp every row with its folder's newest activity, so folder groups can be
        // ordered by recency rather than alphabetically.
        foreach (var byFolder in _all.GroupBy(r => r.Project, StringComparer.OrdinalIgnoreCase))
        {
            var newest = byFolder.Max(r => r.When);
            foreach (var row in byFolder) row.FolderRank = newest;
        }

        ApplyFilter();

        if (selected is not null)
        {
            Sessions.SelectedItem = Sessions.Items
                .OfType<SessionRow>()
                .FirstOrDefault(r => r.Info.SessionId == selected);
        }

        if (!_store.RootExists)
            EmptyState.Text = "No transcripts found under ~\\.claude\\projects.\nStart a session and it will show up here.";
    }

    private void ApplyFilter()
    {
        var q = Filter.Text.Trim();

        IEnumerable<SessionRow> rows = _all;
        if (q.Length > 0)
        {
            rows = rows.Where(r =>
                r.Preview.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                r.Project.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                r.Info.SessionId.StartsWith(q, StringComparison.OrdinalIgnoreCase));
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
            2 => new SortDescription(nameof(SessionRow.Preview), ListSortDirection.Ascending),
            _ => new SortDescription(nameof(SessionRow.When), ListSortDirection.Descending)
        });

        Sessions.ItemsSource = view.View;

        // Rebuilding the view can leave the ScrollViewer parked mid-list, which hides the
        // Today header. Put it back at the top.
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

        if (row is null) return;

        // Already open? Switch to it. Never resume the same id twice — two live
        // transcripts on one session interleave into a single unusable log.
        if (_panes.TryGetValue(row.Info.SessionId, out var existing))
        {
            Activate(existing, Describe(row.Info));
        }
    }

    /// <summary>Double-click is how you open things in Explorer, so it opens them here too.</summary>
    private void Sessions_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (Sessions.SelectedItem is SessionRow) OpenSelected();
    }

    private void Resume_Click(object sender, RoutedEventArgs e) => OpenSelected();

    private void OpenSelected()
    {
        if (Sessions.SelectedItem is not SessionRow row) return;

        if (_panes.TryGetValue(row.Info.SessionId, out var existing))
        {
            Activate(existing, Describe(row.Info));
            return;
        }

        var cwd = Directory.Exists(row.Info.ProjectDir) ? row.Info.ProjectDir : _folder;
        StartPane(SessionLauncher.Plan(LaunchMode.Resume, cwd, row.Info.SessionId),
                  row.Info.SessionId, Describe(row.Info));
    }

    private void NewSession_Click(object sender, RoutedEventArgs e)
    {
        StartPane(SessionLauncher.Plan(LaunchMode.New, _folder), sessionId: null,
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
        StartPane(plan, plan.ExpectedSessionId,
                  $"branch of {Short(parentId)} · {Path.GetFileName(cwd.TrimEnd('\\'))}");
    }

    // ------------------------------------------------------------------- panes

    private void StartPane(LaunchPlan plan, string? sessionId, string title)
    {
        var pane = new TerminalPane(plan, sessionId);
        var key = pane.SessionId ?? Guid.NewGuid().ToString();
        _panes[key] = pane;

        pane.StatusChanged += (_, status) =>
        {
            if (ReferenceEquals(pane, _active)) PaintStatus(status);
        };

        PaneHost.Children.Add(pane);
        Activate(pane, title);
    }

    private void Activate(TerminalPane pane, string title)
    {
        foreach (UIElement child in PaneHost.Children)
            child.Visibility = ReferenceEquals(child, pane) ? Visibility.Visible : Visibility.Collapsed;

        _active = pane;
        PaneTitle.Text = title;
        ForkButton.IsEnabled = pane.SessionId is not null;
        EmptyState.Visibility = Visibility.Collapsed;
        PaintStatus(pane.Status);
        pane.FocusTerminal();
    }

    private void PaintStatus(PaneStatus status)
    {
        StatusDot.Fill = (Brush)FindResource(status switch
        {
            PaneStatus.Working => "Working",
            PaneStatus.Exited => "Ended",
            _ => "Idle"
        });
    }

    private static string Describe(SessionInfo s) =>
        $"{s.ProjectName} · {Short(s.SessionId)}";

    private static string Short(string id) => id.Length > 8 ? id[..8] : id;

    protected override void OnClosing(CancelEventArgs e)
    {
        _store.StopWatching();
        foreach (var pane in _panes.Values) pane.Close();
        base.OnClosing(e);
    }
}

using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CcxShell.Core;
using CcxShell.UI;
using Microsoft.Web.WebView2.Core;

namespace CcxShell;

/// <summary>One card in the Pages view. Its picture arrives once the thumbnail is made.</summary>
public sealed class PageCard(PageEntry entry, string sub) : INotifyPropertyChanged
{
    public PageEntry Entry { get; } = entry;
    public string Title => Entry.Title;
    public string Sub { get; } = sub;
    public string Kind => Entry.Deck ? "Slides" : "Page";

    private ImageSource? _thumb;
    public ImageSource? Thumb
    {
        get => _thumb;
        set { _thumb = value; PropertyChanged?.Invoke(this, new(nameof(Thumb))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Every page from every session (docs/PAGES.md, P5): search, filters, sort and a grid of
/// cards to open, export, show in Explorer or delete. MainWindow shows it in the terminal
/// side's place and does what needs a session.
/// </summary>
public partial class PagesView : UserControl
{
    /// <summary>The × or Esc. MainWindow goes back to the session you were on.</summary>
    public event Action? CloseRequested;

    /// <summary>Open the page in its session's viewer.</summary>
    public event Action<PageEntry>? OpenRequested;

    /// <summary>Open or focus the page's session.</summary>
    public event Action<PageEntry>? SessionRequested;

    /// <summary>A filter or the sort changed; MainWindow keeps it.</summary>
    public event Action<ClayoSettings>? SettingsChanged;

    /// <summary>The sidebar name of a session, or null when Clayo doesn't know it.</summary>
    public Func<string, string?> SessionName { get; set; } = _ => null;

    private PageIndex _index = new();
    private ClayoSettings _settings = new();
    private IReadOnlyList<PageEntry> _all = [];
    private bool _loading;
    private int _shown;   // which Show the thumbnail loop belongs to

    public PagesView() => InitializeComponent();

    /// <summary>Scans the projects for pages, then shows them with the filters kept in settings.</summary>
    public void Show(PageIndex index, ClayoSettings settings, IEnumerable<string> projects)
    {
        _index = index;
        _settings = settings;
        _index.Scan(projects);
        _all = _index.Recent(int.MaxValue);
        PageThumbs.Prune(_all.Select(p => p.Path));

        _loading = true;
        Fill(ProjectBy, "All projects", _all.Select(p => p.Project).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(PageIndex.ProjectName, StringComparer.CurrentCultureIgnoreCase)
            .Select(p => (p, PageIndex.ProjectName(p))), settings.PagesProject);
        Fill(AgentBy, "All agents", [(nameof(AgentKind.Claude), "Claude"), (nameof(AgentKind.Codex), "Codex")], settings.PagesAgent);
        Fill(SortBy, null, Enum.GetValues<PageSort>().Select(s => (s.ToString(), "Sort: " + SortLabel(s))), settings.PagesSort.ToString());
        SlidesOnly.IsChecked = settings.PagesSlides;
        FillSessions();
        _loading = false;
        Shown();
        Cards.Focus();
    }

    private static string SortLabel(PageSort s) => s switch
    {
        PageSort.Changed => "Last changed",
        PageSort.Name => "Name",
        PageSort.Project => "Project",
        _ => "Newest",
    };

    /// <summary>A combo's choices: "all" (Tag null) first unless it has none, then each value; picks `selected`.</summary>
    private static void Fill(ComboBox box, string? all, IEnumerable<(string Value, string Label)> choices, string? selected)
    {
        box.Items.Clear();
        if (all is not null) box.Items.Add(new ComboBoxItem { Content = all });
        foreach (var (value, label) in choices) box.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, selected)) ?? box.Items[0];
    }

    private static string? Picked(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string;

    // The sessions of the project picked, so the list stays short.
    private void FillSessions()
    {
        var project = Picked(ProjectBy);
        var sessions = _all.Where(p => p.SessionId is not null && (project is null || string.Equals(p.Project, project, StringComparison.OrdinalIgnoreCase)))
            .Select(p => p.SessionId!).Distinct()
            .Select(id => (id, SessionName(id) ?? "closed session " + id[..Math.Min(8, id.Length)]));
        Fill(SessionBy, "All sessions", sessions, Picked(SessionBy));
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (ReferenceEquals(sender, ProjectBy))
        {
            _loading = true;
            FillSessions();
            _loading = false;
        }
        var s = _settings with
        {
            PagesProject = Picked(ProjectBy),
            PagesAgent = Picked(AgentBy),
            PagesSlides = SlidesOnly.IsChecked == true,
            PagesSort = Enum.TryParse<PageSort>(Picked(SortBy), out var sort) ? sort : PageSort.Newest
        };
        if (s != _settings)
        {
            _settings = s;
            SettingsChanged?.Invoke(s);
        }
        Shown();
    }

    /// <summary>The cards the filters let through; their pictures follow one at a time.</summary>
    private void Shown()
    {
        var filter = new PageFilter(Search.Text, _settings.PagesProject, _settings.PagesAgent, Picked(SessionBy), _settings.PagesSlides, _settings.PagesSort);
        var cards = PageIndex.Filter(_all, filter).Select(p => new PageCard(p, SubOf(p))).ToList();
        Cards.ItemsSource = cards;
        Count.Text = cards.Count == _all.Count ? $"{_all.Count}" : $"{cards.Count} of {_all.Count}";
        Empty.Text = _all.Count == 0 ? "No pages yet. Type /page in a session to make one." : "No pages match.";
        Empty.Visibility = cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _ = Thumbs(cards, ++_shown);
    }

    private string SubOf(PageEntry p)
    {
        var agent = p.Agent switch { nameof(AgentKind.Claude) => " · CC", nameof(AgentKind.Codex) => " · CX", _ => "" };
        return $"{PageIndex.ProjectName(p.Project)}{agent} · {SessionRow.AgeOf(p.Changed.ToLocalTime())}";
    }

    // Shows the pictures there are, then makes the missing or stale ones, one at a time, until
    // the view is shown again.
    private async Task Thumbs(List<PageCard> cards, int shown)
    {
        foreach (var card in cards) if (PageThumbs.IsFresh(card.Entry.Path)) card.Thumb = Load(PageThumbs.PathFor(card.Entry.Path));
        foreach (var card in cards.Where(c => c.Thumb is null))
        {
            if (shown != _shown || !IsVisible) return;
            try
            {
                await PageExport.Run(card.Entry.Path, PageThumbs.PathFor(card.Entry.Path), PageExportKind.Thumb);
                card.Thumb = Load(PageThumbs.PathFor(card.Entry.Path));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException
                                          or System.Runtime.InteropServices.COMException or WebView2RuntimeNotFoundException) { }
        }
    }

    private static ImageSource? Load(string file)
    {
        if (!File.Exists(file)) return null;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;   // the file stays free to be made again
        image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        image.UriSource = new Uri(file);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private List<PageEntry> Selected => [.. Cards.SelectedItems.OfType<PageCard>().Select(c => c.Entry)];

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    private void Cards_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as DependencyObject) is { } hit && ItemsControl.ContainerFromElement(Cards, hit) is ListBoxItem { DataContext: PageCard card })
            OpenRequested?.Invoke(card.Entry);
    }

    private void Cards_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && Selected.Count > 0) Delete(Selected);
        else if (e.Key == Key.Enter && Selected is [var one]) OpenRequested?.Invoke(one);
        else return;
        e.Handled = true;
    }

    private void Cards_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var pages = Selected;
        CardMenu.Items.Clear();
        if (pages.Count == 0) { e.Handled = true; return; }

        MenuItem Item(string header, Action act, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => act();
            CardMenu.Items.Add(item);
            return item;
        }

        bool one = pages.Count == 1;
        Item("Open", () => OpenRequested?.Invoke(pages[0]), one);
        Item("Go to session", () => SessionRequested?.Invoke(pages[0]), one && pages[0].SessionId is { } id && SessionName(id) is not null);
        var export = Item("Export", () => { }, one);
        foreach (var (label, kind) in new[] { ("PDF", PageExportKind.Pdf), ("PNG image", PageExportKind.Png) })
        {
            var sub = new MenuItem { Header = label };
            sub.Click += async (_, _) => await TerminalPane.Export(Window.GetWindow(this), pages[0].Path, kind);
            export.Items.Add(sub);
        }
        Item("Save a copy…", async () => await TerminalPane.SaveCopy(Window.GetWindow(this), pages[0].Path), one);
        Item("Show in Explorer", () => System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{pages[0].Path}\"")?.Dispose(), one);
        CardMenu.Items.Add(new Separator());
        Item(one ? "Delete" : $"Delete {pages.Count} pages", () => Delete(pages)).InputGestureText = "Del";
    }

    /// <summary>To the Recycle Bin, after a confirm naming how many.</summary>
    private void Delete(List<PageEntry> pages)
    {
        var what = pages.Count == 1 ? $"\"{pages[0].Title}\"" : $"{pages.Count} pages";
        if (MessageBox.Show(Window.GetWindow(this)!, $"Move {what} to the Recycle Bin?", "Clayo",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

        var gone = new List<string>();
        foreach (var page in pages)
        {
            try
            {
                if (File.Exists(page.Path))
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(page.Path,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                gone.Add(page.Path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException) { }
        }
        _index.Remove(gone);
        _all = _index.Recent(int.MaxValue);
        Shown();
    }
}

using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CcxShell.Core;

/// <summary>
/// One page an agent wrote, as the index keeps it. Times are UTC. SessionId is null and Agent
/// empty for a page found in a project's folder that no session was seen writing. Deck is
/// read from the file and Shared from PageShares, not kept here.
/// </summary>
public sealed record PageEntry(
    string Path, string Title, string Project, string? SessionId, string Agent, DateTime Created, DateTime Changed,
    bool Deck = false, bool Shared = false);

public enum PageSort { Newest, Changed, Name, Project }

/// <summary>What the Pages view shows: a search of titles, the filters (null: any) and the order.</summary>
public sealed record PageFilter(
    string Search = "", string? Project = null, string? Agent = null, string? SessionId = null, bool Slides = false,
    PageSort Sort = PageSort.Newest, bool Shared = false);

/// <summary>
/// Every page from every session, in pages.json, so the Pages list can show them with no pane
/// open. Added to when a session writes a page; a page whose file is gone drops out. No WPF.
/// </summary>
public sealed partial class PageIndex
{
    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clayo", "pages.json");

    private readonly string _file;
    private List<PageEntry> _pages = [];

    public PageIndex(string? file = null)
    {
        _file = file ?? DefaultPath;
        try
        {
            // Read and written field by field: no reflection, so it also runs trimmed.
            if (File.Exists(_file) && JsonNode.Parse(File.ReadAllText(_file)) is JsonArray pages)
                _pages = [.. pages.OfType<JsonObject>().Select(p => new PageEntry(
                    (string)p["path"]!, (string)p["title"]!, (string)p["project"]!, (string?)p["session"], (string)p["agent"]!,
                    (DateTime)p["created"]!, (DateTime)p["changed"]!))];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException
                                      or InvalidOperationException or ArgumentNullException or FormatException) { }
    }

    /// <summary>The pages whose files are still there.</summary>
    public int Count => _pages.Count(p => File.Exists(p.Path));

    /// <summary>
    /// Adds the pages a session wrote, or brings the ones already known up to date, with their
    /// text twins (PageTwin).
    /// </summary>
    public void Record(IEnumerable<string> paths, string project, string? sessionId, string agent)
    {
        var written = paths.Where(File.Exists).ToList();
        foreach (var path in written)
        {
            var now = File.GetLastWriteTimeUtc(path);
            int at = _pages.FindIndex(p => Same(p.Path, path));
            var entry = new PageEntry(path, TitleOf(path), project, sessionId, agent, at < 0 ? now : _pages[at].Created, now);
            if (at < 0) _pages.Add(entry);
            else _pages[at] = entry;
            PageTwin.Update(path);
        }
        Save(written);
    }

    /// <summary>
    /// Adds the pages in these projects' .clayo/pages that the index doesn't know, such as ones
    /// written while Clayo was closed: no session, no agent.
    /// </summary>
    public void Scan(IEnumerable<string> projects)
    {
        var found = new List<string>();
        foreach (var project in projects.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var folder = System.IO.Path.Combine(project, ".clayo", "pages");
            if (!Directory.Exists(folder)) continue;
            try
            {
                // Only the pages: their twins and index.md are .md.
                foreach (var path in Directory.EnumerateFiles(folder, "*.html"))
                    if (!_pages.Any(p => Same(p.Path, path)))
                    {
                        _pages.Add(new PageEntry(path, TitleOf(path), project, null, "",
                            File.GetCreationTimeUtc(path), File.GetLastWriteTimeUtc(path)));
                        PageTwin.Update(path);
                        found.Add(path);
                    }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        if (found.Count > 0) Save(found);
    }

    /// <summary>Takes these pages and their twins out of the index (the pages are deleted by the caller).</summary>
    public void Remove(IEnumerable<string> paths)
    {
        var gone = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = _pages.Where(p => gone.Contains(p.Path)).Select(p => p.Path).ToList();
        if (removed.Count == 0) return;
        _pages.RemoveAll(p => gone.Contains(p.Path));
        removed.ForEach(PageTwin.Delete);
        Save(removed);
    }

    /// <summary>
    /// The pages last changed, newest first, as their files are now: an edit reaches the
    /// transcript before the file, so the time and title recorded then may be a step behind.
    /// </summary>
    public IReadOnlyList<PageEntry> Recent(int max)
    {
        var gone = _pages.Where(p => !File.Exists(p.Path)).Select(p => p.Path).ToList();
        _pages = [.. _pages.Where(p => File.Exists(p.Path)).Select(p => p with { Changed = File.GetLastWriteTimeUtc(p.Path) })];
        var recent = _pages.OrderByDescending(p => p.Changed).Take(max).Select(p => p with { Title = TitleOf(p.Path), Deck = IsDeck(p.Path) }).ToList();
        if (gone.Count > 0)
        {
            gone.ForEach(PageTwin.Delete);
            Save(gone);
        }
        return recent;
    }

    /// <summary>The pages a filter lets through, in its order.</summary>
    public static IReadOnlyList<PageEntry> Filter(IEnumerable<PageEntry> pages, PageFilter f)
    {
        var shown = pages.Where(p =>
            (f.Search.Length == 0 || p.Title.Contains(f.Search.Trim(), StringComparison.OrdinalIgnoreCase))
            && (f.Project is null || Same(p.Project, f.Project))
            && (f.Agent is null || p.Agent == f.Agent)
            && (f.SessionId is null || p.SessionId == f.SessionId)
            && (!f.Slides || p.Deck)
            && (!f.Shared || p.Shared));
        return f.Sort switch
        {
            PageSort.Changed => [.. shown.OrderByDescending(p => p.Changed)],
            PageSort.Name => [.. shown.OrderBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase)],
            PageSort.Project => [.. shown.OrderBy(p => ProjectName(p.Project), StringComparer.CurrentCultureIgnoreCase).ThenByDescending(p => p.Changed)],
            _ => [.. shown.OrderByDescending(p => p.Created)],
        };
    }

    /// <summary>The folder's own name, as the sidebar shows a project.</summary>
    public static string ProjectName(string project) =>
        System.IO.Path.GetFileName(project.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : project;

    private static bool IsDeck(string path)
    {
        try { return PageHtml.IsDeck(File.ReadAllText(path)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleTag();

    /// <summary>The page's title, or its file name when it has none.</summary>
    public static string TitleOf(string path)
    {
        try
        {
            // The title is in the head: the first few KB, whatever the page embeds after it.
            using var reader = new StreamReader(path);
            var head = new char[8192];
            int n = reader.ReadBlock(head, 0, head.Length);
            var title = System.Net.WebUtility.HtmlDecode(TitleTag().Match(new string(head, 0, n)).Groups[1].Value).Trim();
            if (title.Length > 0) return title;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return System.IO.Path.GetFileNameWithoutExtension(path);
    }

    // Writes pages.json, and index.md in the folders of the pages that changed (PageTwin).
    private void Save(IEnumerable<string> changed)
    {
        foreach (var folder in changed.Select(System.IO.Path.GetDirectoryName).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
            PageTwin.WriteIndex(folder, _pages.Where(p => Same(System.IO.Path.GetDirectoryName(p.Path) ?? "", folder)));
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_file)!);
            var pages = new JsonArray([.. _pages.Select(p => new JsonObject
            {
                ["path"] = p.Path, ["title"] = p.Title, ["project"] = p.Project, ["session"] = p.SessionId,
                ["agent"] = p.Agent, ["created"] = p.Created, ["changed"] = p.Changed
            })]);
            File.WriteAllText(_file, pages.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

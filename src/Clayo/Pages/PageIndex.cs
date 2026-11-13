using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CcxShell.Core;

/// <summary>One page an agent wrote, as the index keeps it. Times are UTC.</summary>
public sealed record PageEntry(
    string Path, string Title, string Project, string? SessionId, string Agent, DateTime Created, DateTime Changed);

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

    /// <summary>Adds the pages a session wrote, or brings the ones already known up to date.</summary>
    public void Record(IEnumerable<string> paths, string project, string? sessionId, string agent)
    {
        foreach (var path in paths.Where(File.Exists))
        {
            var now = File.GetLastWriteTimeUtc(path);
            int at = _pages.FindIndex(p => Same(p.Path, path));
            var entry = new PageEntry(path, TitleOf(path), project, sessionId, agent, at < 0 ? now : _pages[at].Created, now);
            if (at < 0) _pages.Add(entry);
            else _pages[at] = entry;
        }
        Save();
    }

    /// <summary>
    /// The pages last changed, newest first, as their files are now: an edit reaches the
    /// transcript before the file, so the time and title recorded then may be a step behind.
    /// </summary>
    public IReadOnlyList<PageEntry> Recent(int max)
    {
        int before = _pages.Count;
        _pages = [.. _pages.Where(p => File.Exists(p.Path)).Select(p => p with { Changed = File.GetLastWriteTimeUtc(p.Path) })];
        var recent = _pages.OrderByDescending(p => p.Changed).Take(max).Select(p => p with { Title = TitleOf(p.Path) }).ToList();
        if (_pages.Count != before) Save();
        return recent;
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

    private void Save()
    {
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

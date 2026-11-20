using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CcxShell.Core;

/// <summary>
/// A page's public link: the gist, its owner and the file in it. Live when the page was small
/// enough for gistpreview (GitHubGist.FitsLive) the last time it was sent; else githack.
/// </summary>
public sealed record PageShare(string GistId, string Owner, string File, bool Live = false)
{
    public string Url => Live ? GitHubGist.PreviewUrl(GistId, File) : GitHubGist.CachedPreviewUrl(Owner, GistId, File);
}

/// <summary>
/// Which pages have a public link, in
/// shares.json beside pages.json. Read fresh on every call, so the viewer and the Pages view
/// never write over each other. No WPF.
/// </summary>
public sealed class PageShares(string? file = null)
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clayo", "shares.json");

    private readonly string _file = file ?? DefaultPath;

    public PageShare? Get(string page) =>
        Pages(Read()).FirstOrDefault(s => Same(s.Page, page)).Share;

    /// <summary>Every page with a link.</summary>
    public IReadOnlyDictionary<string, PageShare> All() =>
        Pages(Read()).ToDictionary(s => s.Page, s => s.Share, StringComparer.OrdinalIgnoreCase);

    public void Set(string page, PageShare? share)
    {
        var o = Read();
        var pages = Pages(o).Where(s => !Same(s.Page, page)).ToList();
        if (share is not null) pages.Add((page, share));
        o["pages"] = new JsonArray([.. pages.Select(s => new JsonObject
        {
            ["page"] = s.Page, ["gist"] = s.Share.GistId, ["owner"] = s.Share.Owner, ["file"] = s.Share.File, ["live"] = s.Share.Live
        })]);
        Write(o);
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<(string Page, PageShare Share)> Pages(JsonObject o) =>
        (o["pages"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(p => (Page: Text(p, "page"), Gist: Text(p, "gist"), Owner: Text(p, "owner"), File: Text(p, "file"),
                // Shares made before live links have no "live": they keep githack until updated.
                Live: p["live"] is JsonValue l && l.TryGetValue<bool>(out var live) && live))
            // The id goes into an API path: only one shaped like a gist's (GitHubGist.IsGistId).
            .Where(p => p is { Page: not null, Gist: not null, Owner: not null, File: not null } && GitHubGist.IsGistId(p.Gist))
            .Select(p => (p.Page!, new PageShare(p.Gist!, p.Owner!, p.File!, p.Live)));

    private static string? Text(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    private JsonObject Read()
    {
        try { return File.Exists(_file) && JsonNode.Parse(File.ReadAllText(_file)) is JsonObject o ? o : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return []; }
    }

    private void Write(JsonObject o)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

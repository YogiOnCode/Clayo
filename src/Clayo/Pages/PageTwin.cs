using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CcxShell.Core;

/// <summary>
/// A page's text twin (D18): `<name>.md` beside `<name>.html`, made by Clayo whenever the page
/// changes, so another session reads the page without parsing HTML and the agent writes nothing
/// more. Headings and text as markdown, tables as tables, a chart's data as a table, Mermaid as
/// a mermaid block, a deck's slides between `---`. No WPF.
/// </summary>
public static partial class PageTwin
{
    /// <summary>The list of a folder's pages and their twins (IndexFile), rewritten with the index.</summary>
    public const string IndexFile = "index.md";

    /// <summary>The twin's path: the page's with .md, except index.html's, which would be the list.</summary>
    public static string PathOf(string page) =>
        Path.GetFileNameWithoutExtension(page).Equals("index", StringComparison.OrdinalIgnoreCase)
            ? page + ".md" : Path.ChangeExtension(page, ".md");

    /// <summary>Writes the twin when it is missing or older than the page.</summary>
    public static void Update(string page)
    {
        try
        {
            var twin = PathOf(page);
            if (!File.Exists(page) || File.Exists(twin) && File.GetLastWriteTimeUtc(twin) >= File.GetLastWriteTimeUtc(page)) return;
            File.WriteAllText(twin, Make(File.ReadAllText(page), Path.GetFileName(page), File.GetLastWriteTime(page)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Deletes the twin of a page that is gone.</summary>
    public static void Delete(string page)
    {
        try { File.Delete(PathOf(page)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Writes a folder's index.md: one line per page, last changed first, or deletes it when the
    /// folder has none left.
    /// </summary>
    public static void WriteIndex(string folder, IEnumerable<PageEntry> pages)
    {
        var file = Path.Combine(folder, IndexFile);
        try
        {
            var lines = pages.Where(p => File.Exists(p.Path)).OrderByDescending(p => File.GetLastWriteTimeUtc(p.Path))
                .Select(p => $"- {p.Title}: `{Path.GetFileName(PathOf(p.Path))}` (page `{Path.GetFileName(p.Path)}`), changed {File.GetLastWriteTime(p.Path):yyyy-MM-dd HH:mm}")
                .ToList();
            if (lines.Count == 0) { File.Delete(file); return; }
            if (!Directory.Exists(folder)) return;
            File.WriteAllText(file, "# Pages\n\nEach page's text is in its `.md` twin: read that, not the `.html`.\n\n" + string.Join("\n", lines) + "\n");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The twin of a page's HTML: its title first, then a line naming the file and when it was written.</summary>
    public static string Make(string html, string file, DateTime written)
    {
        var root = Parse(html.ReplaceLineEndings("\n"));
        var title = Find(root, n => n.Tag == "title") is { } t ? Squash(Decode(Raw(t))) : "";
        if (title.Length == 0) title = Path.GetFileNameWithoutExtension(file);
        var main = Find(root, n => n.Tag == "main") ?? Find(root, n => n.Tag == "body") ?? root;

        var md = new Md(title);
        string body;
        if (Has(main, "deck"))
            body = string.Join("\n\n---\n\n", main.Kids.OfType<Node>().Where(n => Has(n, "slide"))
                .Select(slide => { var blocks = new List<string>(); md.Container(slide, blocks); return string.Join("\n\n", blocks); }));
        else
        {
            var blocks = new List<string>();
            md.Container(main, blocks);
            body = string.Join("\n\n", blocks);
        }
        return $"# {title}\n\n`.clayo/pages/{file}` · written {written:yyyy-MM-dd HH:mm}\n\n{body}\n";
    }

    // A small tree: enough for the markup the guide and templates use. Kids are strings or nodes.
    private sealed class Node(string tag, string attrs)
    {
        public readonly string Tag = tag;
        public readonly string Attrs = attrs;
        public readonly List<object> Kids = [];
    }

    private static readonly HashSet<string> Void = ["area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr"];
    private static readonly HashSet<string> InlineTags = ["a", "abbr", "b", "strong", "i", "em", "code", "span", "small", "time", "mark", "kbd",
        "sub", "sup", "s", "u", "br", "img", "cite", "label", "q", "var", "samp", "del", "ins", "progress", "meter"];
    private static readonly HashSet<string> Skip = ["head", "style", "template", "noscript", "svg", "canvas", "button", "hr"];

    [GeneratedRegex(@"<!--.*?(?:-->|$)|<![^>]*>|<(/?)([a-zA-Z][\w-]*)((?:[^>""']|""[^""]*""|'[^']*')*)>|<|[^<]+", RegexOptions.Singleline)]
    private static partial Regex Token();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    private static Node Parse(string html)
    {
        var root = new Node("", "");
        var open = new List<Node> { root };
        int at = 0;
        while (at < html.Length && Token().Match(html, at) is { Success: true } m)
        {
            at = m.Index + m.Length;
            if (!m.Groups[2].Success)
            {
                if (!m.Value.StartsWith("<!")) open[^1].Kids.Add(m.Value);   // text, and a lone <
                continue;
            }
            var tag = m.Groups[2].Value.ToLowerInvariant();
            if (m.Groups[1].Length > 0)
            {
                // A closing tag closes up to its opener; one with no opener is ignored.
                int i = open.FindLastIndex(n => n.Tag == tag);
                if (i > 0) open.RemoveRange(i, open.Count - i);
                continue;
            }
            // A new li, p, row or cell closes an open one, as HTML does.
            if (open[^1].Tag == tag && tag is "li" or "p" or "tr" || tag is "td" or "th" && open[^1].Tag is "td" or "th")
                open.RemoveAt(open.Count - 1);
            var node = new Node(tag, m.Groups[3].Value);
            open[^1].Kids.Add(node);
            if (Void.Contains(tag) || m.Groups[3].Value.EndsWith('/')) continue;
            if (tag is "script" or "style" or "title" or "textarea")
            {
                int end = html.IndexOf("</" + tag, at, StringComparison.OrdinalIgnoreCase);
                if (end < 0) end = html.Length;
                node.Kids.Add(html[at..end]);
                at = end < html.Length && html.IndexOf('>', end) is >= 0 and var gt ? gt + 1 : html.Length;
                continue;
            }
            open.Add(node);
        }
        return root;
    }

    private static Node? Find(Node n, Func<Node, bool> match)
    {
        foreach (var kid in n.Kids.OfType<Node>())
            if ((match(kid) ? kid : Find(kid, match)) is { } found) return found;
        return null;
    }

    private static string? Attr(Node n, string name) =>
        Regex.Match(n.Attrs, $@"(?:^|\s){name}\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.IgnoreCase) is { Success: true } m
            ? WebUtility.HtmlDecode(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value)
            : null;

    private static bool Has(Node n, string cls) => Attr(n, "class") is { } c && c.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(cls);

    // All the text inside, as written.
    private static string Raw(Node n) => string.Concat(n.Kids.Select(k => k as string ?? Raw((Node)k)));

    private static string Decode(string s) => WebUtility.HtmlDecode(s);

    private static string Squash(string s) => Spaces().Replace(s, " ").Trim();

    // Lines trimmed and their spaces squashed, empty ones dropped.
    private static string Lines(string s) =>
        string.Join("\n", s.Split('\n').Select(l => Spaces().Replace(l, " ").Trim()).Where(l => l.Length > 0));

    private sealed class Md(string title)
    {
        private bool _titleSeen;

        /// <summary>A container's children as blocks: runs of text and inline tags become one paragraph.</summary>
        public void Container(Node n, List<string> blocks)
        {
            var text = new StringBuilder();
            void Flush()
            {
                if (Lines(text.ToString()) is { Length: > 0 } p) blocks.Add(p);
                text.Clear();
            }
            foreach (var kid in n.Kids)
            {
                if (kid is Node k && !InlineTags.Contains(k.Tag))
                {
                    Flush();
                    Block(k, blocks);
                }
                else text.Append(Inline(kid));
            }
            Flush();
        }

        private void Block(Node n, List<string> blocks)
        {
            if (n.Tag == "script")
            {
                if (Has(n, "chart") && Chart(Raw(n)) is { } chart) blocks.Add(chart);
                return;
            }
            if (Skip.Contains(n.Tag)) return;
            if (n.Tag is ['h', >= '1' and <= '6'])
            {
                var heading = Lines(Inner(n)).Replace('\n', ' ');
                // The title is the twin's first heading already.
                if (n.Tag == "h1" && !_titleSeen && heading == title) { _titleSeen = true; return; }
                if (heading.Length > 0) blocks.Add($"{new string('#', n.Tag[1] - '0')} {heading}");
                return;
            }
            if (n.Tag is "ul" or "ol") { if (List(n, "") is { Length: > 0 } list) blocks.Add(list); return; }
            if (n.Tag == "table") { if (Table(n) is { } table) blocks.Add(table); return; }
            if (n.Tag == "pre")
            {
                var code = string.Join("\n", Decode(Raw(n)).Split('\n').Select(l => l.TrimEnd())).Trim('\n');
                blocks.Add($"```{(Has(n, "mermaid") ? "mermaid" : "")}\n{code}\n```");
                return;
            }
            if (n.Tag == "blockquote" || Has(n, "callout"))
            {
                var inside = new List<string>();
                Container(n, inside);
                if (inside.Count > 0) blocks.Add(string.Join("\n>\n", inside.Select(b => "> " + b.Replace("\n", "\n> "))));
                return;
            }
            // Text that is only layout, as short lines: `312 passed · 4 failed`.
            if (Has(n, "kpis")) { if (Joined(n.Kids.OfType<Node>().Where(k => Has(k, "kpi")).Select(Kpi)) is { } kpis) blocks.Add(kpis); return; }
            if (Has(n, "kpi")) { blocks.Add(Kpi(n)); return; }
            if (Has(n, "row")) { if (Joined(n.Kids.Select(k => Lines(Inline(k)).Replace('\n', ' '))) is { } row) blocks.Add(row); return; }
            Container(n, blocks);
        }

        private static string? Joined(IEnumerable<string> parts) =>
            string.Join(" · ", parts.Where(p => p.Length > 0)) is { Length: > 0 } s ? s : null;

        private string Kpi(Node kpi)
        {
            string Part(string tag) => kpi.Kids.OfType<Node>().FirstOrDefault(k => k.Tag == tag) is { } k ? Squash(Inner(k)) : "";
            var note = Part("small");
            return Squash($"{Part("b")} {Part("span")}") + (note.Length > 0 ? $" ({note})" : "");
        }

        private string List(Node list, string indent)
        {
            var lines = new List<string>();
            int i = 0;
            foreach (var li in list.Kids.OfType<Node>().Where(k => k.Tag == "li"))
            {
                var text = new StringBuilder();
                var nested = new List<string>();
                foreach (var kid in li.Kids)
                    if (kid is Node { Tag: "ul" or "ol" } sub) nested.Add(List(sub, indent + "  "));
                    else text.Append(Inline(kid));
                lines.Add($"{indent}{(list.Tag == "ol" ? $"{++i}." : "-")} {Lines(text.ToString()).Replace("\n", " ")}");
                lines.AddRange(nested.Where(s => s.Length > 0));
            }
            return string.Join("\n", lines);
        }

        private string? Table(Node table)
        {
            var rows = new List<List<string>>();
            void Rows(Node n)
            {
                foreach (var kid in n.Kids.OfType<Node>())
                    if (kid.Tag == "tr") rows.Add([.. kid.Kids.OfType<Node>().Where(c => c.Tag is "td" or "th")
                        .Select(c => Lines(Inner(c)).Replace('\n', ' ').Replace("|", "\\|"))]);
                    else if (kid.Tag is "thead" or "tbody" or "tfoot") Rows(kid);
            }
            Rows(table);
            return Grid(rows);
        }

        // Rows as a markdown table, the first as its header.
        private static string? Grid(List<List<string>> rows)
        {
            rows.RemoveAll(r => r.Count == 0);
            if (rows.Count == 0) return null;
            int width = rows.Max(r => r.Count);
            string Row(List<string> r) => "| " + string.Join(" | ", r.Concat(Enumerable.Repeat("", width - r.Count))) + " |";
            return string.Join("\n", [Row(rows[0]), "|" + string.Concat(Enumerable.Repeat("---|", width)), .. rows.Skip(1).Select(Row)]);
        }

        // A chart's data, as a table: a row per label, a column per series; for a scatter, a row per point.
        private static string? Chart(string json)
        {
            try
            {
                if (JsonNode.Parse(json) is not JsonObject chart) return null;
                static string Value(JsonNode? v) => v is JsonValue s && s.TryGetValue<string>(out var str) ? str : v?.ToJsonString() ?? "";
                var series = (chart["series"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
                var rows = new List<List<string>>();
                if (chart["labels"] is JsonArray labels)
                {
                    rows.Add([Value(chart["xLabel"]), .. series.Select(s => Value(s["name"]))]);
                    rows.AddRange(labels.Select((label, i) => (List<string>)
                        [Value(label), .. series.Select(s => s["data"] is JsonArray d && i < d.Count ? Value(d[i]) : "")]));
                }
                else
                {
                    rows.Add(["", chart["xLabel"] is { } x ? Value(x) : "x", chart["yLabel"] is { } y ? Value(y) : "y"]);
                    foreach (var s in series)
                        foreach (var point in (s["data"] as JsonArray)?.OfType<JsonArray>() ?? [])
                            rows.Add([Value(s["name"]), .. point.Take(2).Select(Value)]);
                }
                var head = $"{(chart["title"] is { } title ? $"**{Value(title)}**, a" : "A")} {Value(chart["type"])} chart";
                if (chart["yLabel"] is { } unit && chart["labels"] is not null) head += $" ({Value(unit)})";
                return Grid(rows) is { } grid ? head + ":\n\n" + grid : head;
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { return null; }
        }

        private string Inner(Node n) => string.Concat(n.Kids.Select(Inline));

        // Text and inline tags as markdown; a block inside an inline context as its text.
        private string Inline(object kid)
        {
            if (kid is string s) return Spaces().Replace(Decode(s), " ");
            var n = (Node)kid;
            if (Skip.Contains(n.Tag) || n.Tag == "script") return "";
            var inner = Inner(n);
            string Wrap(string mark) => inner.Trim() is { Length: > 0 } t ? $"{mark}{t}{mark}" : "";
            return n.Tag switch
            {
                "br" => "\n",
                "b" or "strong" => Wrap("**"),
                "i" or "em" => Wrap("*"),
                "code" or "kbd" or "samp" => Squash(Decode(Raw(n))) is { Length: > 0 } c ? $"`{c}`" : "",
                "a" when Attr(n, "href") is { } href && Regex.IsMatch(href, "^(https?|mailto):", RegexOptions.IgnoreCase) && inner.Trim().Length > 0
                    => $"[{inner.Trim()}]({href})",
                "img" => Attr(n, "alt") is { Length: > 0 } alt ? $"[image: {alt}]" : "",
                "progress" or "meter" => $"{Attr(n, "value")}/{Attr(n, "max") ?? "1"}",
                "cite" => $" — {inner.Trim()}",
                "time" => inner + " ",
                _ when InlineTags.Contains(n.Tag) => Has(n, "badge") ? $" {inner.Trim()} " : inner,
                _ => $" {inner} ",
            };
        }
    }
}

#:property TargetFramework=net10.0-windows
#:project ../src/Clayo/CcxShell.csproj
using CcxShell.Core;

// Pages: how a page an agent wrote is sealed before it is shown, and how its folder is kept out
// of git. Temp folders only.
int fail = 0;
void Check<T>(string name, T got, T want)
{
    var ok = EqualityComparer<T>.Default.Equals(got, want);
    if (!ok) fail++;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}  got={got} want={want}");
}

const string Meta = "<meta http-equiv=\"Content-Security-Policy\"";

// The policy goes in first.
var page = "<!doctype html>\n<html lang=\"en\"><head>\n<meta charset=\"utf-8\">\n<link rel=\"stylesheet\" href=\"clayo-page.css\">\n</head><body>\n<script>var a = 1;</script>\n<script src=\"clayo-page.js\"></script>\n</body></html>";
var s = PageHtml.Seal(page);
Check("the policy comes straight after the doctype", s.StartsWith("<!doctype html>" + Meta), true);
Check("  before any script", s.IndexOf(Meta) < s.IndexOf("<script"), true);
Check("  once", s.Split(Meta).Length - 1, 1);
Check("  and the page follows unchanged", s.EndsWith("<script>var a = 1;</script>\n<script src=\"https://ccx.assets/pages/clayo-page.js\"></script>\n</body></html>"), true);

Check("a page with no head and no doctype starts with it", PageHtml.Seal("<h1>hi</h1><script>x()</script>").StartsWith(Meta), true);
Check("a doctype in other case is kept first", PageHtml.Seal("  <!DOCTYPE HTML><p>").StartsWith("  <!DOCTYPE HTML>" + Meta), true);
// A policy placed after the page's own <head> could be swallowed by a comment the page opens.
var trick = PageHtml.Seal("<!doctype html><!-- <head> --><script>fetch('https://example.com')</script>");
Check("a page can't comment the policy out", trick.IndexOf(Meta) < trick.IndexOf("<!--"), true);

// What the policy allows.
Check("no connections", PageHtml.Csp.Contains("connect-src 'none'"), true);
Check("no form posts", PageHtml.Csp.Contains("form-action 'none'"), true);
Check("nothing by default", PageHtml.Csp.StartsWith("default-src 'none'"), true);
Check("images only from the page and Clayo", PageHtml.Csp.Contains("img-src data: blob: https://ccx.assets;"), true);

// The links to Clayo's two files point at the copies Clayo serves; nothing else is touched.
Check("the stylesheet link", s.Contains("href=\"https://ccx.assets/pages/clayo-page.css\""), true);
Check("single quotes and ./", PageHtml.Seal("<link href='./clayo-page.css'><script src = './clayo-page.js'></script>")[(Meta.Length)..].Contains("href='https://ccx.assets/pages/clayo-page.css'><script src = 'https://ccx.assets/pages/clayo-page.js'>"), true);
foreach (var other in new[]
{
    "<link rel=\"stylesheet\" href=\"other.css\">",
    "<link rel=\"stylesheet\" href=\"my-clayo-page.css\">",
    "<link rel=\"stylesheet\" href=\"sub/clayo-page.css\">",
    "<script src=\"https://cdn.jsdelivr.net/npm/d3@7.9.0/dist/d3.min.js\"></script>",
    "<a href=\"https://example.com/clayo-page.js\">x</a>",
    "<p>clayo-page.css</p>",
})
    Check($"left alone: {other}", PageHtml.Seal(other).EndsWith(">" + other), true);

// A theme goes in after Clayo's stylesheet and before anything the page styles itself (P12).
const string Theme = ":root {\n  --accent: red;\n}\n";
var own = page.Replace("</head>", "<style>h1 { color: blue; }</style>\n</head>");
var themed = PageHtml.Seal(own, Theme);
const string Link = "<link rel=\"stylesheet\" href=\"https://ccx.assets/pages/clayo-page.css\">";
Check("a theme comes straight after the stylesheet link", themed.Contains(Link + "<style>:root {   --accent: red; } </style>"), true);
Check("  before the page's own style", themed.IndexOf("--accent: red") < themed.IndexOf("color: blue"), true);
Check("  on one line, so the page's line numbers stay", themed.Split('\n').Length, PageHtml.Seal(own).Split('\n').Length);
Check("  and can't close its own tag", PageHtml.Seal(own, "a{}</style><script>x()</script>").Contains("a{}<\\/style><script>x()<\\/script></style>"), true);
Check("a page with no link gets no theme", PageHtml.Seal("<!doctype html><link rel=\"stylesheet\" href=\"other.css\"><h1>hi</h1>", Theme).Contains("--accent"), false);
Check("the default theme adds nothing", PageHtml.Seal(own, PageTheme.Css(PageTheme.Default)), PageHtml.Seal(own));
Check("nor does a name that is not a theme", PageTheme.Css("../../secret"), "");
foreach (var theme in PageTheme.All.Where(t => t.Id != PageTheme.Default))
{
    var css = PageTheme.Css(theme.Id);
    Check($"{theme.Id}: tokens only, one short rule",
        css.Split('\n').Length <= 40 && System.Text.RegularExpressions.Regex.IsMatch(css, @"^/\*[^*]*\*/\s*:root \{(\s*--[\w-]+:[^;{}]+;)+\s*\}\s*$"), true);
    var tokens = PageTheme.Tokens(theme.Id);
    Check("  with a colour for every part of a card, light and dark",
        new[] { "--bg", "--surface", "--border", "--heading", "--muted", "--accent", "--c1", "--title-bg", "--title-text" }
            .All(t => PageTheme.Colour(tokens, t, false) is not null && PageTheme.Colour(tokens, t, true) is not null), true);
}
var basic = PageTheme.Tokens(PageTheme.Default);
Check("the default's deck tokens are the page's own", (PageTheme.Colour(basic, "--title-bg", true), PageTheme.Colour(basic, "--title-text", false)),
    (PageTheme.Colour(basic, "--bg", true), PageTheme.Colour(basic, "--heading", false)));
Check("  and it has no band", PageTheme.Colour(basic, "--slide-accent", false), null);

// A deck is a page whose main is a .deck: the viewer offers Present for it.
Check("a deck", PageHtml.IsDeck("<body><main class=\"deck\"><section class=\"slide\">"), true);
Check("  with other classes", PageHtml.IsDeck("<main id=m class='wide deck dark'>"), true);
Check("a page is not", PageHtml.IsDeck("<main class=\"page\"><p class=\"deck\">deck</p>"), false);
Check("  nor a word that only starts so", PageHtml.IsDeck("<main class=\"decked\">"), false);

// Keeping .clayo out of git.
var dir = Directory.CreateTempSubdirectory("clayo-pages").FullName;
try
{
    var repo = Path.Combine(dir, "repo");
    var sub = Path.Combine(repo, "src", "app");
    Directory.CreateDirectory(Path.Combine(repo, ".git"));
    Directory.CreateDirectory(sub);
    var exclude = Path.Combine(repo, ".git", "info", "exclude");

    PageFolder.HideFromGit(repo);
    Check("a repo gets .clayo/ in its exclude file", File.ReadAllText(exclude), ".clayo/\n");
    PageFolder.HideFromGit(repo);
    Check("  once", File.ReadAllText(exclude), ".clayo/\n");

    File.WriteAllText(exclude, "# local\n*.tmp");
    PageFolder.HideFromGit(sub);
    Check("from a folder below the root, after what is there", File.ReadAllText(exclude), "# local\n*.tmp\n.clayo/\n");

    var plain = Path.Combine(dir, "plain");
    Directory.CreateDirectory(plain);
    PageFolder.HideFromGit(plain);
    Check("a folder that is not a repo: nothing written", Directory.EnumerateFileSystemEntries(plain).Any(), false);

    // Setup: the guide, templates and shared files land in the project's .clayo (P4).
    var assets = Path.Combine(dir, "assets");
    Directory.CreateDirectory(Path.Combine(assets, "templates"));
    File.WriteAllText(Path.Combine(assets, "page-guide.md"), "guide 1");
    File.WriteAllText(Path.Combine(assets, "clayo-page.css"), "css");
    File.WriteAllText(Path.Combine(assets, "clayo-page.js"), "js");
    Directory.CreateDirectory(Path.Combine(assets, "themes"));
    File.WriteAllText(Path.Combine(assets, "themes", "swiss.css"), "swiss");
    File.WriteAllText(Path.Combine(assets, "templates", "report.html"), "report");
    var clayo = Path.Combine(plain, ".clayo");
    PageFolder.Setup(plain, assets);
    Check("setup: the guide", File.ReadAllText(Path.Combine(clayo, "page-guide.md")), "guide 1");
    Check("setup: a template", File.ReadAllText(Path.Combine(clayo, "templates", "report.html")), "report");
    Check("setup: the stylesheet beside the pages", File.ReadAllText(Path.Combine(clayo, "pages", "clayo-page.css")), "css");
    var mine = Path.Combine(clayo, "pages", "mine.html");
    File.WriteAllText(mine, "<title> Test &amp; results </title>");
    File.WriteAllText(Path.Combine(assets, "page-guide.md"), "guide 2");
    File.SetLastWriteTimeUtc(Path.Combine(assets, "page-guide.md"), DateTime.UtcNow.AddMinutes(1));
    Check("setup: the themes stay Clayo's", Directory.Exists(Path.Combine(clayo, "themes")), false);
    File.WriteAllText(Path.Combine(clayo, "pages", "clayo-page.js"), "edited");
    File.SetLastWriteTimeUtc(Path.Combine(clayo, "pages", "clayo-page.js"), DateTime.UtcNow.AddMinutes(1));
    PageFolder.Setup(plain, assets, Theme);
    Check("setup again: a newer guide replaces the old", File.ReadAllText(Path.Combine(clayo, "page-guide.md")), "guide 2");
    Check("  a copy that is not older is left", File.ReadAllText(Path.Combine(clayo, "pages", "clayo-page.js")), "edited");
    var sheet = Path.Combine(clayo, "pages", "clayo-page.css");
    Check("  with a theme: the stylesheet ends with it", File.ReadAllText(sheet).StartsWith("css\n") && File.ReadAllText(sheet).EndsWith(Theme), true);
    var written = File.GetLastWriteTimeUtc(sheet);
    PageFolder.Setup(plain, assets, Theme);
    Check("  the same theme again: not written again", File.GetLastWriteTimeUtc(sheet), written);
    PageFolder.Setup(plain, assets, ":root { --accent: blue; }");
    Check("  another theme: written again", File.ReadAllText(sheet).EndsWith(":root { --accent: blue; }"), true);
    PageFolder.Setup(plain, assets);
    Check("  the default again: Clayo's stylesheet alone", File.ReadAllText(sheet), "css");

    // Claude's /page (P14): written once, kept up to date, a user's own never touched.
    var cmdRepo = Path.Combine(dir, "cmd");
    Directory.CreateDirectory(Path.Combine(cmdRepo, ".git"));
    var cmd = Path.Combine(cmdRepo, ".claude", "commands", "page.md");
    Check("claude /page: new, so not loaded yet", PageFolder.ClaudeCommand(cmdRepo), false);
    Check("  written", File.ReadAllText(cmd), PagePrompt.ClaudeCommand);
    Check("  kept out of git", File.ReadAllText(Path.Combine(cmdRepo, ".git", "info", "exclude")), ".claude/commands/page.md\n");
    Check("  there: used", PageFolder.ClaudeCommand(cmdRepo), true);
    Check("  an older one of Clayo's is brought up to date", PageFolder.ClaudeCommand(cmdRepo, PagePrompt.ClaudeCommand + "x") && File.ReadAllText(cmd).EndsWith("x"), true);
    File.WriteAllText(cmd, "my own page command");
    Check("  a user's own: not used", PageFolder.ClaudeCommand(cmdRepo), false);
    Check("  and not touched", File.ReadAllText(cmd), "my own page command");
    Check("  a page is never touched", File.ReadAllText(mine), "<title> Test &amp; results </title>");

    // The index of every page.
    var json = Path.Combine(dir, "pages.json");
    var other = Path.Combine(clayo, "pages", "other.html");
    File.WriteAllText(other, "<p>no title</p>");
    var t0 = new DateTime(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc);
    File.SetLastWriteTimeUtc(mine, t0);
    File.SetLastWriteTimeUtc(other, t0.AddHours(1));
    var index = new PageIndex(json);
    index.Record([mine, other, Path.Combine(clayo, "pages", "never-written.html")], plain, "s1", "Claude");
    Check("index: the pages that exist are added", index.Count, 2);
    var recent = index.Recent(10);
    Check("  last changed first", string.Join(",", recent.Select(p => Path.GetFileName(p.Path))), "other.html,mine.html");
    Check("  the title from <title>", recent[1].Title, "Test & results");
    Check("  else the file name", recent[0].Title, "other");
    Check("  its session and project", (recent[1].SessionId, recent[1].Project, recent[1].Agent), ("s1", plain, "Claude"));

    File.WriteAllText(mine, "<title>Renamed</title>");
    File.SetLastWriteTimeUtc(mine, t0.AddHours(2));
    index.Record([mine], plain, "s1", "Claude");
    recent = new PageIndex(json).Recent(10);   // read back from the file
    Check("an update: still two", recent.Count, 2);
    Check("  now first, with its new title", (Path.GetFileName(recent[0].Path), recent[0].Title), ("mine.html", "Renamed"));
    Check("  created stays, changed moves", (recent[0].Created, recent[0].Changed), (t0, t0.AddHours(2)));
    Check("  the list is cut to what is asked", index.Recent(1).Count, 1);

    File.Delete(other);
    Check("a deleted file drops out", string.Join(",", index.Recent(10).Select(p => Path.GetFileName(p.Path))), "mine.html");
    Check("  from the file too", new PageIndex(json).Count, 1);
    File.WriteAllText(json, "not json");
    Check("a broken index reads as empty", new PageIndex(json).Count, 0);
}
finally { Directory.Delete(dir, recursive: true); }

Console.WriteLine(fail == 0 ? "\nall checks passed" : $"\n{fail} FAILED");
return fail;

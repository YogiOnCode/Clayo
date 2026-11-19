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

    // The Pages view (P5): a scan finds pages written outside Clayo; filter, sort, remove.
    var scanned = new PageIndex(Path.Combine(dir, "scan.json"));
    scanned.Record([mine], plain, "s1", "Claude");
    var outside = Path.Combine(clayo, "pages", "outside.html");
    File.WriteAllText(outside, "<title>Written outside</title><main class=\"deck\"></main>");
    scanned.Scan([plain, plain, Path.Combine(dir, "no-such-project")]);
    var all = scanned.Recent(int.MaxValue);
    Check("scan: a page written outside Clayo is found", all.Count, 2);
    var found = all.First(p => p.Path == outside);
    Check("  with no session or agent", (found.SessionId, found.Agent, found.Project), ((string?)null, "", plain));
    Check("  a deck is known as one", (found.Deck, all.First(p => p.Path == mine).Deck), (true, false));
    scanned.Scan([plain]);
    Check("  a second scan adds nothing", scanned.Recent(int.MaxValue).Count, 2);

    var t = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
    PageEntry E(string name, string project, string agent, string? session, int created, int changed, bool deck = false) =>
        new(name, name, project, session, agent, t.AddDays(created), t.AddDays(changed), deck);
    PageEntry[] fixture =
    [
        E("Beta", @"C:\a\web", "Claude", "s1", 1, 5),
        E("alpha", @"C:\b\api", "Codex", "s2", 2, 3, deck: true),
        E("Gamma report", @"C:\a\web", "Claude", "s3", 3, 4),
    ];
    string Names(PageFilter f) => string.Join(",", PageIndex.Filter(fixture, f).Select(p => p.Title));
    Check("filter: newest first by default", Names(new()), "Gamma report,alpha,Beta");
    Check("  last changed", Names(new(Sort: PageSort.Changed)), "Beta,Gamma report,alpha");
    Check("  name, any case", Names(new(Sort: PageSort.Name)), "alpha,Beta,Gamma report");
    Check("  project, then last changed", Names(new(Sort: PageSort.Project)), "alpha,Beta,Gamma report");
    Check("  search in titles", Names(new(Search: "REPORT ")), "Gamma report");
    Check("  a project, any case", Names(new(Project: @"c:\A\WEB")), "Gamma report,Beta");
    Check("  an agent", Names(new(Agent: "Codex")), "alpha");
    Check("  a session", Names(new(SessionId: "s1")), "Beta");
    Check("  slides only", Names(new(Slides: true)), "alpha");
    Check("  together: none match", Names(new(Agent: "Codex", Project: @"C:\a\web")), "");

    scanned.Remove([outside.ToUpperInvariant()]);
    Check("remove: the page leaves the index", scanned.Recent(int.MaxValue).Select(p => p.Path).Single(), mine);
    Check("  and the file says so", new PageIndex(Path.Combine(dir, "scan.json")).Count, 1);

    // Thumbnails: named from the page, fresh until it changes, pruned with the index.
    var thumbs = Path.Combine(dir, "thumbs");
    Directory.CreateDirectory(thumbs);
    Check("thumb: the name ignores the path's case", PageThumbs.PathFor(mine, thumbs), PageThumbs.PathFor(mine.ToUpperInvariant(), thumbs));
    Check("  none yet: not fresh", PageThumbs.IsFresh(mine, thumbs), false);
    File.WriteAllText(PageThumbs.PathFor(mine, thumbs), "png");
    File.SetLastWriteTimeUtc(PageThumbs.PathFor(mine, thumbs), File.GetLastWriteTimeUtc(mine).AddMinutes(1));
    Check("  made after the page: fresh", PageThumbs.IsFresh(mine, thumbs), true);
    File.SetLastWriteTimeUtc(mine, DateTime.UtcNow.AddHours(1));
    Check("  the page changed since: not", PageThumbs.IsFresh(mine, thumbs), false);
    File.WriteAllText(PageThumbs.PathFor(outside, thumbs), "png");
    PageThumbs.Prune([mine], thumbs);
    Check("  a removed page's picture is pruned, the others kept",
        (File.Exists(PageThumbs.PathFor(outside, thumbs)), File.Exists(PageThumbs.PathFor(mine, thumbs))), (false, true));

    // Save a copy (P7): one file with everything inside, libraries only when used.
    var lib = Path.Combine(dir, "lib");
    Directory.CreateDirectory(lib);
    File.WriteAllText(Path.Combine(lib, "clayo-page.css"), ".a { color: red } /* $1 </style> */");
    File.WriteAllText(Path.Combine(lib, "clayo-page.js"), "var page = '$0 </script>'; var c = '<!--';");
    File.WriteAllText(Path.Combine(lib, "chart.umd.min.js"), "var CHART;");
    File.WriteAllText(Path.Combine(lib, "mermaid.min.js"), "var MERMAID;");
    const string Cdn = "https://cdn.jsdelivr.net/npm/d3@7.9.0/dist/d3.min.js";
    var fetched = new List<string>();
    Task<string?> Fetch(string url) { fetched.Add(url); return Task.FromResult<string?>(url == Cdn ? "var D3;" : null); }
    string Bundle(string body) => PageBundle.Make(
        $"<!doctype html><head><link rel=\"stylesheet\" href=\"clayo-page.css\"></head><body>{body}<script src=\"clayo-page.js\"></script></body>",
        ":root { --accent: blue; }", lib, Fetch).Result;

    var plainCopy = Bundle("<p>hi</p>");
    Check("copy: sealed", plainCopy.Contains("Content-Security-Policy"), true);
    Check("  the stylesheet inside, then the theme", plainCopy.IndexOf(".a { color: red }") is > 0 and var at && at < plainCopy.IndexOf("--accent: blue"), true);
    Check("  the script inside", plainCopy.Contains("var page = '$0 <\\/script>';"), true);
    Check("  an HTML comment opener in it is escaped", plainCopy.Contains("var c = '<\\!--';"), true);
    Check("  nothing linked from Clayo", plainCopy.Contains("ccx.assets/pages/clayo-page"), false);
    Check("  a closing tag in a file can't end its tag early", plainCopy.Contains("/* $1 <\\/style> */"), true);
    Check("  no charts: no Chart.js, no Mermaid", (plainCopy.Contains("CHART"), plainCopy.Contains("MERMAID")), (false, false));
    var chartCopy = Bundle("<script type=\"application/json\" class=\"chart\">{}</script>");
    Check("a chart: Chart.js, before Clayo's script", chartCopy.IndexOf("var CHART;") is > 0 and var c && c < chartCopy.IndexOf("var page"), true);
    Check("  and still no Mermaid", chartCopy.Contains("MERMAID"), false);
    Check("a diagram: Mermaid", Bundle("<pre class=\"mermaid\">graph TD</pre>").Contains("var MERMAID;"), true);
    var cdnCopy = Bundle($"<script src=\"{Cdn}\"></script><script src=\"https://unpkg.com/x@1.0.0/x.js\"></script><script src=\"https://example.com/y.js\"></script>");
    Check("a CDN script it can get: inside", cdnCopy.Contains("<script>var D3;</script>") && !cdnCopy.Contains(Cdn), true);
    Check("  one it can't: its link kept", cdnCopy.Contains("src=\"https://unpkg.com/x@1.0.0/x.js\""), true);
    Check("  another host: never fetched", fetched.Any(u => u.Contains("example.com")), false);

    // Before a page leaves the PC (P7, D16): each kind is found, on its line.
    string Found(string text) => string.Join("|", SecretScan.Find(text).Select(f => $"{f.Line}:{f.What}"));
    Check("scan: a GitHub token", Found("<p>\nghp_" + new string('a', 36)), "2:a GitHub token");
    Check("  a fine-grained one", Found("github_pat_" + new string('B', 30)), "1:a GitHub token");
    Check("  an sk- key", Found("key sk-proj-" + new string('x', 30)), "1:an API key (sk-)");
    Check("  an AWS key", Found("AKIAIOSFODNN7EXAMPLE"), "1:an AWS key");
    Check("  a Slack token", Found("xoxb-1234567890-abcdef"), "1:a Slack token");
    Check("  a private key", Found("-----BEGIN OPENSSH PRIVATE KEY-----"), "1:a private key");
    Check("  KEY=value", Found("DB_PASSWORD=hunter2hunter2"), "1:a KEY=value secret");
    Check("  a Windows path", Found(@"see C:\Users\yogi\notes.txt"), "1:a local path");
    Check("  a Mac path", Found("at /Users/yogi/x"), "1:a local path");
    Check("  localhost and private hosts", Found("http://localhost:3000\n192.168.1.20\nbuild.corp"), "1:an internal host|2:an internal host|3:an internal host");
    Check("  a path as JS writes it", Found(@"{""dir"":""C:\\Users\\yogi\\x""}"), "1:a local path");
    Check("  with forward slashes", Found("C:/Users/yogi/x"), "1:a local path");
    Check("  a network share", Found(@"\\fileserver\team"), "1:a local path");
    Check("  a Stripe key", Found("sk_live_" + new string('a', 24)), "1:a Stripe key");
    Check("  a Google key", Found("AIza" + new string('b', 35)), "1:a Google API key");
    Check("  a JWT", Found("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.abcdefghijklmnop"), "1:a JWT");
    Check("  a bearer header", Found("Authorization: Bearer abcdefghijklmnopqrstuvwxyz"), "1:a bearer token");
    Check("  a password in a URL", Found("postgres://app:s3cretpw@db.example.com/x"), "1:a password in a URL");
    var slow = string.Concat(Enumerable.Repeat("a.", 200_000));
    var watch = System.Diagnostics.Stopwatch.StartNew();
    var slowFound = Found(slow);
    Check("  a line made to be slow: done fast, and flagged or clean", watch.Elapsed.TotalSeconds < 10 && slowFound is "" or "1:a line too long to check", true);
    Check("  nothing in plain words", Found("A token of thanks. The secret is good docs. Users/home pages."), "");
    Check("  a placeholder value is not a secret", Found("API_KEY=<your key>"), "");
    foreach (var template in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Assets", "pages", "templates"), "*.html"))
        Check($"  no false hit on {Path.GetFileName(template)}", Found(File.ReadAllText(template)), "");

    // Public links: kept per page, with the login said yes to; the Shared filter.
    var shares = new PageShares(Path.Combine(dir, "shares.json"));
    Check("shares: none yet", (shares.Login, shares.Get(mine)), ((string?)null, (PageShare?)null));
    shares.Login = "gh";
    const string Gist = "0123456789abcdef0123456789abcdef";
    shares.Set(mine, new PageShare(Gist, "someone", "mine.html"));
    var again = new PageShares(Path.Combine(dir, "shares.json"));
    Check("  read back", (again.Login, again.Get(mine.ToUpperInvariant())?.GistId), ("gh", Gist));
    Check("  its link", again.Get(mine)!.Url, $"https://gist.githack.com/someone/{Gist}/raw/mine.html");
    again.Set(mine, new PageShare(Gist, "someone", "my page.html", Live: true));
    Check("  a live one: gistpreview", new PageShares(Path.Combine(dir, "shares.json")).Get(mine)!.Url, $"https://gistpreview.github.io/?{Gist}/my%20page.html");
    Check("live: a page at the limit", GitHubGist.FitsLive(new string('a', GitHubGist.LiveMax)), true);
    Check("  a byte over", GitHubGist.FitsLive(new string('a', GitHubGist.LiveMax - 1) + "é"), false);
    Check("gist scope: a classic token with it can make gists", GitHubGist.CanGist("repo, read:org, gist"), true);
    Check("  without it: can't", GitHubGist.CanGist("repo, read:org, workflow"), false);
    Check("  no scopes at all: can't", GitHubGist.CanGist(""), false);
    Check("  not a classic token (no header): GitHub decides", GitHubGist.CanGist(null), true);
    File.WriteAllText(Path.Combine(dir, "old.json"), "{\"pages\":[{\"page\":\"p\",\"gist\":\"" + Gist + "\",\"owner\":\"o\",\"file\":\"f\"}]}");
    Check("  a share from before live links is not live", new PageShares(Path.Combine(dir, "old.json")).Get("p")?.Live, false);
    again.Set(outside, new PageShare("../repos/me/proj/git/refs/heads/main", "someone", "x.html"));
    Check("  an id that isn't a gist's is never read back", again.Get(outside), (PageShare?)null);
    Check("  nor sent", GitHubGist.IsGistId("../repos/x"), false);
    again.Set(mine, null);
    Check("  removed, the login kept", (again.Get(mine), again.Login), ((PageShare?)null, "gh"));
    var pathDir = Path.Combine(dir, "bin");
    Directory.CreateDirectory(pathDir);
    File.WriteAllText(Path.Combine(pathDir, "gh.exe"), "");
    Check("tools: found on PATH by full path", GitHubGist.OnPath("gh", $".;bin;{pathDir}"), Path.Combine(pathDir, "gh.exe"));
    Check("  never in a relative folder", GitHubGist.OnPath("gh", $".;bin"), (string?)null);
    File.WriteAllText(Path.Combine(dir, "shares.json"), "{\"login\":5,\"pages\":[{\"page\":1,\"gist\":\"" + Gist + "\",\"owner\":\"o\",\"file\":\"f\"}]}");
    Check("  odd types in the file read as nothing", (new PageShares(Path.Combine(dir, "shares.json")).Login, new PageShares(Path.Combine(dir, "shares.json")).All().Count), ((string?)null, 0));
    Check("  a broken entry is skipped", new PageShares(Path.Combine(dir, "shares.json")).All().Count, 0);
    Check("filter: shared only", string.Join(",", PageIndex.Filter([fixture[0], fixture[1] with { Shared = true }], new(Shared: true)).Select(p => p.Title)), "alpha");

    // The text twin (P9, D18): markdown made from the page, for other sessions to read.
    var fixtures = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "checks", "fixtures");
    if (!Directory.Exists(fixtures)) fixtures = Path.Combine(Environment.CurrentDirectory, "fixtures");
    var made = new DateTime(2026, 11, 18, 12, 0, 0);
    foreach (var name in new[] { "report", "dashboard", "slides" })
    {
        var html = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", "pages", "templates", name + ".html"));
        var twin = PageTwin.Make(html, name + ".html", made);
        Check($"twin: {name}.html matches its fixture", twin, File.ReadAllText(Path.Combine(fixtures, $"twin-{name}.md")).ReplaceLineEndings("\n"));
        if (name == "report") Console.WriteLine($"      report twin: {twin.Length} of {html.Length} bytes ({100.0 * twin.Length / html.Length:F0}%)");
    }
    string Twin(string body) => PageTwin.Make($"<!doctype html><head><title>T</title></head><body>{body}</body>", "t.html", made);
    Check("  the title first, then the file and when", Twin("<main class=\"page\"><h1>T</h1></main>"), "# T\n\n`.clayo/pages/t.html` · written 2026-11-18 12:00\n\n\n");
    Check("  a chart gives a data table",
        Twin("<script type=\"application/json\" class=\"chart\">{\"type\":\"bar\",\"title\":\"Fails\",\"yLabel\":\"n\",\"labels\":[\"auth\",\"api\"],\"series\":[{\"name\":\"a\",\"data\":[4,0]},{\"name\":\"b\",\"data\":[1]}]}</script>")
            .Contains("**Fails**, a bar chart (n):\n\n|  | a | b |\n|---|---|---|\n| auth | 4 | 1 |\n| api | 0 |  |"), true);
    Check("  a scatter, a row per point", Twin("<script type=\"application/json\" class=\"chart\">{\"type\":\"scatter\",\"series\":[{\"name\":\"s\",\"data\":[[1,2],[3,4]]}]}</script>")
        .Contains("|  | x | y |\n|---|---|---|\n| s | 1 | 2 |\n| s | 3 | 4 |"), true);
    Check("  broken chart JSON is left out", Twin("<p>a</p><script type=\"application/json\" class=\"chart\">{nope</script>").EndsWith("\n\na\n"), true);
    Check("  Mermaid as a mermaid block", Twin("<pre class=\"mermaid\">\nflowchart LR\n  A --&gt; B\n</pre>").Contains("```mermaid\nflowchart LR\n  A --> B\n```"), true);
    var deckTwin = Twin("<main class=\"deck\"><section class=\"slide\"><h2>One</h2></section><section class=\"slide\"><h2>Two</h2></section><section class=\"slide\"><h2>Three</h2></section></main>");
    Check("  a deck: one section per slide", deckTwin.EndsWith("## One\n\n---\n\n## Two\n\n---\n\n## Three\n"), true);
    Check("  layout as short lines", Twin("<section class=\"kpis\"><div class=\"kpi good\"><b>312</b><span>passed</span></div><div class=\"kpi bad\"><b>4</b><span>failed</span></div></section>")
        .Contains("\n312 passed · 4 failed\n"), true);
    Check("  no script, style or image data", Twin("<style>p{}</style><script>var x;</script><p>a <img alt=\"logo\" src=\"data:image/png;base64,AAAA\"></p>").EndsWith("\n\na [image: logo]\n"), true);
    Check("index.html's twin is not the list", Path.GetFileName(PageTwin.PathOf(@"C:\p\.clayo\pages\index.html")), "index.html.md");

    // The twin and index.md follow the index.
    var twinDir = Path.Combine(dir, "twin", ".clayo", "pages");
    Directory.CreateDirectory(twinDir);
    var one = Path.Combine(twinDir, "one.html");
    var two = Path.Combine(twinDir, "two.html");
    File.WriteAllText(one, "<title>First</title><main class=\"page\"><p>hello</p></main>");
    File.WriteAllText(two, "<title>Second</title>");
    var twins = new PageIndex(Path.Combine(dir, "twins.json"));
    twins.Record([one, two], Path.Combine(dir, "twin"), "s1", "Claude");
    var list = Path.Combine(twinDir, PageTwin.IndexFile);
    Check("record: the twin is written beside the page", File.Exists(Path.Combine(twinDir, "one.md")) && File.ReadAllText(Path.Combine(twinDir, "one.md")).Contains("\nhello\n"), true);
    Check("  index.md lists every page", (File.ReadAllText(list).Contains("- First: `one.md` (page `one.html`)"), File.ReadAllText(list).Contains("- Second: `two.md`")), (true, true));
    File.WriteAllText(one, "<title>First</title><main class=\"page\"><p>changed</p></main>");
    File.SetLastWriteTimeUtc(one, DateTime.UtcNow.AddMinutes(1));
    twins.Record([one], Path.Combine(dir, "twin"), "s1", "Claude");
    Check("  a change rewrites the twin", File.ReadAllText(Path.Combine(twinDir, "one.md")).Contains("\nchanged\n"), true);
    twins.Scan([Path.Combine(dir, "twin")]);
    Check("  the scan lists no twin as a page", twins.Recent(int.MaxValue).Count, 2);
    twins.Remove([two]);
    Check("remove: the twin goes too", File.Exists(Path.Combine(twinDir, "two.md")), false);
    Check("  and index.md drops it", File.ReadAllText(list).Contains("Second"), false);
    File.Delete(one);
    twins.Recent(10);
    Check("a page that drops out takes its twin", File.Exists(Path.Combine(twinDir, "one.md")), false);
    Check("  none left: no index.md", File.Exists(list), false);
}
finally { Directory.Delete(dir, recursive: true); }

Console.WriteLine(fail == 0 ? "\nall checks passed" : $"\n{fail} FAILED");
return fail;

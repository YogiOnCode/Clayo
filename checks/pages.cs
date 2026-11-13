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
    File.WriteAllText(Path.Combine(clayo, "pages", "clayo-page.css"), "edited");
    File.SetLastWriteTimeUtc(Path.Combine(clayo, "pages", "clayo-page.css"), DateTime.UtcNow.AddMinutes(1));
    PageFolder.Setup(plain, assets);
    Check("setup again: a newer guide replaces the old", File.ReadAllText(Path.Combine(clayo, "page-guide.md")), "guide 2");
    Check("  a copy that is not older is left", File.ReadAllText(Path.Combine(clayo, "pages", "clayo-page.css")), "edited");
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

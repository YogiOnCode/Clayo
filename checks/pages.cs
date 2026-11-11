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
}
finally { Directory.Delete(dir, recursive: true); }

Console.WriteLine(fail == 0 ? "\nall checks passed" : $"\n{fail} FAILED");
return fail;

#:property TargetFramework=net10.0-windows
#:project ../src/Clayo/CcxShell.csproj
// A file-based app is set up for native AOT, which turns COM off; WebView2 is COM.
#:property PublishAot=false
using CcxShell.Core;
using Microsoft.Web.WebView2.Core;

// clayo-page: what the report says for a given finding, and what the check finds in three small
// pages shown off-screen. The second half needs the WebView2 runtime and is skipped without it.
int fail = 0;
void Check<T>(string name, T got, T want)
{
    var ok = EqualityComparer<T>.Default.Equals(got, want);
    if (!ok) fail++;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}  got={got} want={want}");
}

// The report
var clean = PageReport.Format(new PageCheckResult("test-results.html", 48 * 1024, 0, [], [], [], ["a-1280.png", "a-390.png"]));
Check("a clean page: every line ok", clean.Text,
    "test-results.html  48 KB  page\nok   no console errors\nok   no blocked requests\nok   fits at 1280px and 390px\nscreenshots: a-1280.png, a-390.png");
Check("  exit 0", clean.Exit, 0);

var broken = PageReport.Format(new PageCheckResult("b.html", 300, 6,
    ["x is not defined (line 12)"], ["blocked: https://example.com/a.png (img-src)"], ["overflow at 390px: table.table is 612px wide"], []));
Check("a broken deck: a FAIL per finding, ok for none", broken.Text,
    "b.html  1 KB  deck, 6 slides\nFAIL x is not defined (line 12)\nFAIL blocked: https://example.com/a.png (img-src)\nFAIL overflow at 390px: table.table is 612px wide");
Check("  exit 1", broken.Exit, 1);

var many = PageReport.Format(new PageCheckResult("m.html", 2048, 1, [.. Enumerable.Range(1, 8).Select(i => $"error {i}")], [], [], []));
Check("many errors: the first five and a count", many.Text,
    "m.html  2 KB  deck, 1 slide\nFAIL error 1\nFAIL error 2\nFAIL error 3\nFAIL error 4\nFAIL error 5\n     and 3 more like these\nok   no blocked requests\nok   fits at 1280px and 390px");

// The pages, shown for real
try { CoreWebView2Environment.GetAvailableBrowserVersionString(); }
catch (WebView2RuntimeNotFoundException)
{
    Console.WriteLine("SKIP  no WebView2 runtime here: the pages were not shown");
    Console.WriteLine(fail == 0 ? "\nall checks passed" : $"\n{fail} FAILED");
    return fail;
}

var fixtures = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "checks", "fixtures");
if (!Directory.Exists(fixtures)) fixtures = Path.Combine(Environment.CurrentDirectory, "fixtures");
var dir = Directory.CreateTempSubdirectory("clayo-pagecheck").FullName;
try
{
    PageCheckResult Run(string name)
    {
        var (result, failure) = PageCheck.Check(Path.Combine(fixtures, name), dir);
        Check($"{name}: checked", failure, null);
        return result ?? new PageCheckResult(name, 0, 0, [], [], [], []);
    }

    var good = Run("page-clean.html");
    Check("a clean page: nothing found", $"{good.Errors.Count} {good.Blocked.Count} {good.Overflow.Count} {good.Slides}", "0 0 0 0");
    Check("  two screenshots, written", good.Shots.Count == 2 && Directory.GetFiles(dir, "page-clean-*.png").All(f => new FileInfo(f).Length > 1000), true);

    var bad = Run("page-broken.html");
    Check("a script error, with the file's own line", bad.Errors.Any(e => e.Contains("notDefined") && e.Contains("(line 11)")), true);
    Check("a blocked request", bad.Blocked.Any(b => b.Contains("https://example.com/chart.png") && b.Contains("img-src")), true);
    Check("overflow at the phone width only", string.Join("|", bad.Overflow), "overflow at 390px: div.card is 620px wide");

    var deck = Run("page-deck.html");
    Check("a deck: its slides counted", deck.Slides, 2);
    Check("  and the slide that is too full named", string.Join("|", deck.Overflow), "slide 2: its content doesn't fit the slide");

    // Export (P6): a deck one slide per 16:9 PDF page or one PNG per slide, a page on A4 or as one image.
    string[] Export(string name, string to, PageExportKind kind)
    {
        var (files, failure) = PageCheck.OnOwnThread(
            () => PageExport.Run(Path.Combine(fixtures, name), Path.Combine(dir, to), kind), TimeSpan.FromSeconds(40));
        Check($"{name} to {to}: exported", failure, null);
        return [.. files ?? []];
    }
    // A PDF's pages and their size in points, read from its text: enough for a count.
    (int Pages, string Box) Pdf(string file)
    {
        var text = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(file));
        var box = System.Text.RegularExpressions.Regex.Match(text, @"/MediaBox\s*\[\s*0 0 (\d+)[.\d]* (\d+)");
        return (System.Text.RegularExpressions.Regex.Matches(text, @"/Type\s*/Page\b(?!s)").Count, $"{box.Groups[1].Value}x{box.Groups[2].Value}");
    }
    Check("a deck as PDF: a 16:9 page per slide", Pdf(Export("page-deck.html", "deck.pdf", PageExportKind.Pdf)[0]), (2, "960x540"));
    Check("a page as PDF: A4", Pdf(Export("page-clean.html", "clean.pdf", PageExportKind.Pdf)[0]).Box, "594x841");
    var slidePngs = Export("page-deck.html", "deck.png", PageExportKind.Png);
    Check("a deck as PNG: one per slide, in a folder",
        string.Join(",", slidePngs.Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/'))), "deck/slide-01.png,deck/slide-02.png");
    Check("  each one written", slidePngs.All(f => new FileInfo(f).Length > 1000), true);
    Check("  and not the same picture twice", File.ReadAllBytes(slidePngs[0]).SequenceEqual(File.ReadAllBytes(slidePngs[1])), false);
    var pagePng = Export("page-clean.html", "clean.png", PageExportKind.Png);
    Check("a page as PNG: one file", pagePng.Length == 1 && new FileInfo(pagePng[0]).Length > 1000, true);
}
finally { Directory.Delete(dir, recursive: true); }

Console.WriteLine(fail == 0 ? "\nall checks passed" : $"\n{fail} FAILED");
return fail;

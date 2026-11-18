using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CcxShell.Core;

/// <summary>
/// A page as one file that opens anywhere with no network (D14): sealed as the viewer shows it,
/// in the theme picked, with Clayo's stylesheet and script inside it, Chart.js and Mermaid only
/// when the page uses them, and the CDN scripts and stylesheets it links (D4) fetched once into
/// %LOCALAPPDATA%\Clayo\cdn. No WPF.
/// </summary>
public static partial class PageBundle
{
    public static string CdnCache => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clayo", "cdn");

    [GeneratedRegex(@"<link\b[^>]*\bhref\s*=\s*([""'])https://ccx\.assets/pages/clayo-page\.css\1[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex StyleLink();

    [GeneratedRegex(@"<script\b[^>]*\bsrc\s*=\s*([""'])https://ccx\.assets/pages/clayo-page\.js\1[^>]*>\s*</script\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptTag();

    [GeneratedRegex(@"<script\b[^>]*\bsrc\s*=\s*([""'])(https://(?:cdnjs\.cloudflare\.com|cdn\.jsdelivr\.net|unpkg\.com)/[^""']+)\1[^>]*>\s*</script\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex CdnScript();

    [GeneratedRegex(@"<link\b(?=[^>]*\brel\s*=\s*[""']?stylesheet)[^>]*\bhref\s*=\s*([""'])(https://(?:cdnjs\.cloudflare\.com|cdn\.jsdelivr\.net|unpkg\.com)/[^""']+)\1[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex CdnStyle();

    [GeneratedRegex(@"<script\b[^>]*\bclass\s*=\s*[""'][^""']*\bchart\b", RegexOptions.IgnoreCase)]
    private static partial Regex UsesChart();

    [GeneratedRegex(@"<pre\b[^>]*\bclass\s*=\s*[""'][^""']*\bmermaid\b", RegexOptions.IgnoreCase)]
    private static partial Regex UsesMermaid();

    /// <summary>
    /// The one file. fetch gets a CDN file's text, or null when it can't; a file it can't get
    /// keeps its link, so the copy still works online.
    /// </summary>
    public static async Task<string> Make(string html, string themeCss, string assets, Func<string, Task<string?>> fetch)
    {
        html = PageHtml.Seal(html, themeCss);
        string Read(string name) => File.ReadAllText(Path.Combine(assets, name));

        // Lambdas, not "$" strings: the files are full of "$" that Regex.Replace would read.
        html = StyleLink().Replace(html, _ => Style(Read("clayo-page.css")), 1);
        var libs = new StringBuilder();
        if (UsesChart().IsMatch(html)) libs.Append(Script(Read("chart.umd.min.js")));
        if (UsesMermaid().IsMatch(html)) libs.Append(Script(Read("mermaid.min.js")));
        html = ScriptTag().Replace(html, _ => libs + Script(Read("clayo-page.js")), 1);

        foreach (var (pattern, wrap) in new (Regex, Func<string, string>)[] { (CdnScript(), Script), (CdnStyle(), Style) })
            foreach (var m in pattern.Matches(html).Reverse())
                if (await fetch(m.Groups[2].Value) is { } text)
                    html = html[..m.Index] + wrap(text) + html[(m.Index + m.Length)..];
        return html;
    }

    // Inside the tag, "</script" or "</style" would end it early, and "<!--" with a "<script"
    // after it would swallow the markup that follows. Both escapes mean the same in JS strings
    // and regexes.
    private static string Script(string js) =>
        $"<script>{js.Replace("</script", "<\\/script", StringComparison.OrdinalIgnoreCase).Replace("<!--", "<\\!--")}</script>";
    private static string Style(string css) => $"<style>{css.Replace("</style", "<\\/style", StringComparison.OrdinalIgnoreCase)}</style>";

    // Mermaid, the largest library a page might link, is about 3 MB.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 16 * 1024 * 1024 };

    /// <summary>A CDN file from the cache, else fetched and kept there; null when it can't be had.</summary>
    public static async Task<string?> FetchCached(string url)
    {
        var file = Path.Combine(CdnCache, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url))));
        try
        {
            if (File.Exists(file)) return await File.ReadAllTextAsync(file);
            var text = await Http.GetStringAsync(url);
            Directory.CreateDirectory(CdnCache);
            await File.WriteAllTextAsync(file, text);
            return text;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or HttpRequestException or TaskCanceledException) { return null; }
    }
}

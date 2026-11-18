using System.Text.RegularExpressions;

namespace CcxShell.Core;

/// <summary>
/// Gets a page an agent wrote ready to show: sealed with a Content-Security-Policy, and its
/// links to Clayo's stylesheet and script pointed at the copies Clayo ships. The viewer and
/// `clayo --page-check` both show exactly this. No WPF.
/// </summary>
public static partial class PageHtml
{
    /// <summary>Where the shared page assets are served from.</summary>
    public const string AssetsUrl = "https://ccx.assets/pages/";

    private const string Cdns = "https://cdnjs.cloudflare.com https://cdn.jsdelivr.net https://unpkg.com";

    /// <summary>
    /// Scripts and styles from the page itself, Clayo's assets and three CDNs; images and fonts
    /// only from inside the page; no connections and no form posts. A page can't send anything
    /// anywhere.
    /// </summary>
    public const string Csp =
        "default-src 'none'; " +
        $"script-src 'unsafe-inline' https://ccx.assets {Cdns}; " +
        $"style-src 'unsafe-inline' https://ccx.assets {Cdns}; " +
        "img-src data: blob: https://ccx.assets; " +
        "font-src data: https://ccx.assets; " +
        "connect-src 'none'; form-action 'none'";

    [GeneratedRegex(@"^\s*<!doctype[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex Doctype();

    // href="clayo-page.css" and src="clayo-page.js", as the guide has them, with or without ./
    [GeneratedRegex(@"\b(href|src)(\s*=\s*)([""'])(?:\./)?(clayo-page\.(?:css|js))\3", RegexOptions.IgnoreCase)]
    private static partial Regex AssetLink();

    [GeneratedRegex(@"<main[^>]*\bclass\s*=\s*[""'][^""']*\bdeck\b", RegexOptions.IgnoreCase)]
    private static partial Regex DeckMain();

    // The stylesheet's link once it points at Clayo's copy.
    [GeneratedRegex(@"<link\b[^>]*\bhref\s*=\s*([""'])https://ccx\.assets/pages/clayo-page\.css\1[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex StyleLink();

    /// <summary>Whether the page is a slide deck: its main is a .deck.</summary>
    public static bool IsDeck(string html) => DeckMain().IsMatch(html);

    /// <summary>
    /// The page with the policy as the first thing in it, straight after the doctype, where the
    /// browser makes it the first element of the head whatever the page's own markup says: a
    /// policy put after a tag the page wrote could be commented out by the page.
    /// A theme (PageTheme) goes in straight after the link to Clayo's stylesheet: after its
    /// rules, before any the page wrote. A page that doesn't link the stylesheet gets none.
    /// </summary>
    public static string Seal(string html, string themeCss = "")
    {
        var meta = $"<meta http-equiv=\"Content-Security-Policy\" content=\"{Csp}\">";
        int at = Doctype().Match(html) is { Success: true } m ? m.Length : 0;
        html = html[..at] + meta + html[at..];
        html = AssetLink().Replace(html, $"$1$2$3{AssetsUrl}$4$3");
        if (themeCss.Length == 0 || StyleLink().Match(html) is not { Success: true } link) return html;
        // On one line, so the line numbers in the page's own errors stay the file's.
        var style = $"<style>{themeCss.ReplaceLineEndings(" ").Replace("</", "<\\/")}</style>";
        return html.Insert(link.Index + link.Length, style);
    }
}

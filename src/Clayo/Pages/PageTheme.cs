using System.IO;
using System.Text.RegularExpressions;

namespace CcxShell.Core;

/// <summary>
/// A look for pages: a small stylesheet that redefines clayo-page.css's tokens and nothing else
/// (Assets/pages/themes). Clayo applies it wherever a page gets its stylesheet (PageHtml.Seal,
/// PageFolder.Setup), so an agent writes nothing for it and pages made before get it too. No WPF.
/// </summary>
public sealed partial record PageTheme(string Id, string Name, string About)
{
    /// <summary>The stylesheet as it ships: no theme file, no CSS added.</summary>
    public const string Default = "clayo";

    public static readonly PageTheme[] All =
    [
        new(Default, "Clayo", "Graphite and amber, flat. Sits beside the terminal without a seam."),
        new("swiss", "Swiss", "Black, white and one red. Square corners, heavy headings."),
        new("editorial", "Editorial", "Warm paper and wine red with serif headings, like a printed report."),
        new("boardroom", "Boardroom", "Navy and white with a gold band, for a deck that goes to a meeting."),
        new("aurora", "Aurora", "Soft violet, round corners, a violet to magenta title slide."),
    ];

    private static string Assets => Path.Combine(AppContext.BaseDirectory, "Assets", "pages");

    /// <summary>A theme's CSS. Empty for the default, a name that isn't a theme, or a file that can't be read.</summary>
    public static string Css(string id, string? assets = null)
    {
        if (id == Default || All.All(t => t.Id != id)) return "";
        try { return File.ReadAllText(Path.Combine(assets ?? Assets, "themes", id + ".css")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ""; }
    }

    /// <summary>The CSS of the theme picked in Settings, read fresh: `clayo-page` runs with no window.</summary>
    public static string CurrentCss() => Css(ClayoSettings.Load(ClayoSettings.DefaultPath).PageThemeId);

    [GeneratedRegex(@"(--[\w-]+)\s*:\s*([^;]+);")]
    private static partial Regex Token();

    [GeneratedRegex(@"var\((--[\w-]+)\)")]
    private static partial Regex Var();

    [GeneratedRegex(@"light-dark\(\s*([^,()]+),\s*([^,()]+)\)")]
    private static partial Regex LightDark();

    [GeneratedRegex(@"#[0-9a-fA-F]{6}\b")]
    private static partial Regex Hex();

    /// <summary>The tokens a theme ends up with: the stylesheet's own, then the theme's over them.</summary>
    public static Dictionary<string, string> Tokens(string id, string? assets = null)
    {
        var tokens = new Dictionary<string, string>();
        string sheet;
        try { sheet = File.ReadAllText(Path.Combine(assets ?? Assets, "clayo-page.css")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { sheet = ""; }
        // The stylesheet's tokens are its first rule; a theme is tokens only.
        int end = sheet.IndexOf('}');
        foreach (var css in new[] { end < 0 ? "" : sheet[..end], Css(id, assets) })
            foreach (Match m in Token().Matches(css))
                tokens[m.Groups[1].Value] = m.Groups[2].Value.Trim();
        return tokens;
    }

    /// <summary>A token's value with the tokens it names filled in; empty when there is none.</summary>
    public static string Value(IReadOnlyDictionary<string, string> tokens, string name)
    {
        var value = tokens.GetValueOrDefault(name, "");
        for (int i = 0; i < 4 && value.Contains("var("); i++)
            value = Var().Replace(value, m => tokens.GetValueOrDefault(m.Groups[1].Value, ""));
        return value;
    }

    /// <summary>
    /// A token's colour in one scheme, as #rrggbb, for drawing a theme's card: the first colour
    /// of a gradient, null for a token with none (transparent, a font).
    /// </summary>
    public static string? Colour(IReadOnlyDictionary<string, string> tokens, string name, bool dark)
    {
        var value = LightDark().Replace(Value(tokens, name), m => m.Groups[dark ? 2 : 1].Value.Trim());
        return Hex().Match(value) is { Success: true } hex ? hex.Value : null;
    }
}

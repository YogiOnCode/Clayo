namespace CcxShell.Core;

/// <summary>What `clayo --page-check` found in one page. Each list holds lines ready to print.</summary>
public sealed record PageCheckResult(
    string Name,
    long Bytes,
    int Slides,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Blocked,
    IReadOnlyList<string> Overflow,
    IReadOnlyList<string> Shots);

/// <summary>The check's findings as the few plain lines an agent reads. No WPF.</summary>
public static class PageReport
{
    public const int Clean = 0, Problems = 1, CouldNotRun = 2;

    // An agent reads every line it is given: a page with thirty errors says the first few.
    private const int Most = 5;

    /// <summary>The report and the exit code: 0 when every line is ok, 1 when not.</summary>
    public static (string Text, int Exit) Format(PageCheckResult r)
    {
        var lines = new List<string>
        {
            $"{r.Name}  {Math.Max(1, (r.Bytes + 512) / 1024)} KB  {(r.Slides > 0 ? $"deck, {r.Slides} slide{(r.Slides == 1 ? "" : "s")}" : "page")}"
        };
        Section(lines, r.Errors, "no console errors");
        Section(lines, r.Blocked, "no blocked requests");
        Section(lines, r.Overflow, "fits at 1280px and 390px");
        if (r.Shots.Count > 0) lines.Add("screenshots: " + string.Join(", ", r.Shots));

        bool clean = r.Errors.Count + r.Blocked.Count + r.Overflow.Count == 0;
        return (string.Join("\n", lines), clean ? Clean : Problems);
    }

    private static void Section(List<string> lines, IReadOnlyList<string> found, string ok)
    {
        if (found.Count == 0) lines.Add("ok   " + ok);
        foreach (var f in found.Take(Most)) lines.Add("FAIL " + f);
        if (found.Count > Most) lines.Add($"     and {found.Count - Most} more like these");
    }
}

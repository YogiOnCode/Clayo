using System.IO;
using System.Text;

namespace CcxShell.Core;

/// <summary>
/// What `/page` becomes before it reaches the agent, and what Page mode says. The same words
/// for every agent: nothing here depends on one CLI's commands. No WPF.
/// </summary>
public static class PagePrompt
{
    public const string Command = "/page";

    public const string ModeOn = "Page mode on: offer pages as `.clayo/page-guide.md` says, for the rest of this session.";

    public const string ModeOff = "Stop offering pages unless I ask.";

    /// <summary>
    /// Whether a line is `/page` or starts `/page `: `/pages`, a line with anything before the
    /// command, and a line carried on with `\` go through as typed.
    /// </summary>
    public static bool IsCommand(string line) =>
        line.StartsWith(Command, StringComparison.Ordinal) && !line.EndsWith('\\') &&
        (line.Length == Command.Length || char.IsWhiteSpace(line[Command.Length]));

    /// <summary>
    /// The page prompt for a /page line, else null: short, since the guide says the rest.
    /// openPage is the page the viewer shows, named so a change lands on that file.
    /// </summary>
    public static string? Rewrite(string line, string? openPage = null)
    {
        if (!IsCommand(line)) return null;
        var text = line[Command.Length..].Trim().TrimEnd('.');
        if (text.Length == 0) text = "a page of what we just discussed";
        return $"Make a Clayo page as `.clayo/page-guide.md` says{OpenNote(openPage)}: {text}.";
    }

    /// <summary>What names the open page: added to the line, or to Claude's /page.</summary>
    public static string OpenNote(string? openPage) =>
        openPage is null ? "" : $" (open: `.clayo/pages/{Path.GetFileName(openPage)}`)";

    /// <summary>
    /// Claude's own /page, written into the project (PageFolder.ClaudeCommand), so Claude reads
    /// this and the terminal shows only the line you typed. ClaudeMarker marks it as Clayo's.
    /// </summary>
    public const string ClaudeCommand =
        "---\n" +
        "description: Make a Clayo page (written by Clayo)\n" +
        "argument-hint: what the page should show\n" +
        "---\n" +
        "Make a Clayo page as `.clayo/page-guide.md` says: $ARGUMENTS\n\n" +
        "Nothing after the colon: a page of what we just discussed. " +
        "`(open: <file>)` at the end names the page open in Clayo.\n";

    public const string ClaudeMarker = "(written by Clayo)";
}

/// <summary>
/// The line being typed at an agent's prompt, followed from the keys alone: the agent draws
/// its own input box and never says what is in it. Text is null once the keys can no longer be
/// followed (an arrow key, a paste of several lines), until the line is sent or cleared.
/// </summary>
public sealed class TypedLine
{
    private readonly StringBuilder _text = new();
    private bool _lost;

    public string? Text => _lost ? null : _text.ToString();

    public void Type(ReadOnlySpan<byte> keys)
    {
        var s = Encoding.UTF8.GetString(keys);
        // Focus in and out, which the terminal reports with no key pressed.
        if (s is "\e[I" or "\e[O") return;
        // A paste: the text between the marks.
        if (s.StartsWith("\e[200~") && s.EndsWith("\e[201~")) s = s[6..^6].Replace('\r', '\n');

        foreach (var c in s)
        {
            if (c is '\r' or '\x03' or '\x15') { _text.Clear(); _lost = false; }   // sent, Ctrl+C, Ctrl+U
            else if (c is '\x7f' or '\b') { if (_text.Length > 0) _text.Length--; }
            else if (c < ' ') _lost = true;
            else _text.Append(c);
        }
    }
}

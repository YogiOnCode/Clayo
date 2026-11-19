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
    /// The line in Codex's input box, from the screen row the cursor is on: what follows its `›`,
    /// else null. For when the keys can't say (an arrow key, a line Codex kept after an error).
    /// </summary>
    public static string? CodexRow(string? row)
    {
        var text = row?.TrimStart();
        return text is ['›', ..] ? text[1..].Trim() : null;
    }

    /// <summary>
    /// Clears Codex's input box wherever the caret is: End, then delete to the start.
    /// </summary>
    public const string CodexClear = "\x05\x15";

    /// <summary>
    /// The page prompt for a /page line, else null: short, since the guide says the rest.
    /// openPage is the page the viewer shows, named so a change lands on that file.
    /// </summary>
    public static string? Rewrite(string line, string? openPage = null)
    {
        if (!IsCommand(line)) return null;
        var text = line[Command.Length..].Trim().TrimEnd('.');
        if (text.Length == 0) text = "a page of what we just discussed";
        return $"Make a Clayo page as `.clayo/page-guide.md` says: {text}.{OpenNote(openPage)}";
    }

    /// <summary>
    /// What names the open page, by its text twin (PageTwin), at the end of a line: the twin to
    /// read, beside the page to edit. When to add it is OpenPageNote's call.
    /// </summary>
    public static string OpenNote(string? openPage) =>
        openPage is null ? "" : $" (page open: .clayo/pages/{Path.GetFileName(PageTwin.PathOf(openPage))})";

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
        "`(page open: <name>.md)` at the end names the page open in Clayo by its text twin: " +
        "a change to the page goes in the `.html` beside it.\n";

    public const string ClaudeMarker = "(written by Clayo)";
}

/// <summary>
/// When a line sent names the open page (PagePrompt.OpenNote): the first prompt after the page
/// is opened or changes, not every one, so the terminal stays quiet; a /page always, so a change
/// lands on that page. Not a slash command or a shell line (`!`), where it would be an argument.
/// </summary>
public sealed class OpenPageNote
{
    private (string Page, DateTime Changed)? _named;

    /// <summary>The page was opened again: name it on the next prompt.</summary>
    public void Forget() => _named = null;

    /// <summary>What to add to this line, sent now with this page open, changed then; else "".</summary>
    public string For(string line, string? page, DateTime changed)
    {
        if (page is null) return "";
        if (!PagePrompt.IsCommand(line))
        {
            var text = line.TrimStart();
            if (text.Length == 0 || text[0] is '/' or '!' || _named == (page, changed)) return "";
        }
        _named = (page, changed);
        return PagePrompt.OpenNote(page);
    }
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

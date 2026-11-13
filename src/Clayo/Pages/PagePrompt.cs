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

    public const string ModeOn =
        "For the rest of this session: when a reply would be clearer as a page (numbers, comparisons, diagrams, " +
        "anything to present), answer in chat as usual and end with one line: 'Want this as a page?'. " +
        "Make a Clayo page, following `.clayo/page-guide.md`, only after I say yes.";

    public const string ModeOff = "Stop offering pages unless I ask.";

    /// <summary>
    /// The page prompt for a line that is `/page` or starts `/page `, else null: `/pages`, a
    /// line with anything before the command, and a line carried on with `\` go through as
    /// typed. openPage is the page the viewer shows, named so a change lands on that file.
    /// </summary>
    public static string? Rewrite(string line, string? openPage = null)
    {
        if (!line.StartsWith(Command, StringComparison.Ordinal) || line.EndsWith('\\')) return null;
        var rest = line[Command.Length..];
        if (rest.Length > 0 && !char.IsWhiteSpace(rest[0])) return null;

        var text = rest.Trim().TrimEnd('.');
        if (text.Length == 0) text = "a page of what we just discussed";
        var open = openPage is null ? "" :
            $"The page open now is `.clayo/pages/{Path.GetFileName(openPage)}`; if this is a change to it, edit that file, don't make a new one. ";
        return "Make a Clayo page: read `.clayo/page-guide.md` first (skip if you already have this session). " +
               $"{open}Then: {text}. Write it to `.clayo/pages/`, check it once, and reply with one line saying what it shows.";
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

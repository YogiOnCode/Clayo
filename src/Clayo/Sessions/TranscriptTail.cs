using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CcxShell.Core;

/// <summary>
/// An open pane's transcript, read as it grows, a few new lines at a time. Two things come out
/// of it: the last markdown file the session wrote inside its folder (the doc chip), and when
/// you last sent a prompt (which clears the image chip). Prompts from before the pane opened
/// are skipped. Never writes.
/// </summary>
public sealed class TranscriptTail
{
    // A resumed session's transcript can run to a few MB. Its first read starts this far from
    // the end: the lines since the pane opened are all there, and the timestamp skips the rest.
    private const long FirstWindow = 512 * 1024;

    private readonly string _folder;
    private readonly DateTime _since;
    private readonly bool _codex;
    private string? _file;
    private long _offset;

    /// <summary>The newest .md the session wrote or edited inside its folder, if any.</summary>
    public string? Doc { get; private set; }

    /// <summary>When you last sent a prompt, UTC. MinValue until you do.</summary>
    public DateTime PromptAt { get; private set; } = DateTime.MinValue;

    public TranscriptTail(string folder, DateTime sinceUtc, bool codex)
    {
        _folder = Path.GetFullPath(folder).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        _since = sinceUtc;
        _codex = codex;
    }

    /// <summary>Reads what was added since the last call. True when a doc was written meanwhile.</summary>
    public bool Read(string file)
    {
        bool wrote = false;
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            bool mid = false;
            if (!string.Equals(file, _file, StringComparison.OrdinalIgnoreCase))
            {
                _file = file;
                _offset = Math.Max(0, stream.Length - FirstWindow);
                mid = _offset > 0;
            }
            if (stream.Length <= _offset) return false;

            stream.Position = _offset;
            var bytes = new byte[stream.Length - _offset];
            stream.ReadExactly(bytes);

            // Only whole lines: the last one may still be being written.
            int end = Array.LastIndexOf(bytes, (byte)'\n');
            if (end < 0) return false;
            _offset += end + 1;

            var lines = Encoding.UTF8.GetString(bytes, 0, end).Split('\n');
            for (int i = mid ? 1 : 0; i < lines.Length; i++)
                wrote |= ReadLine(lines[i]);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return wrote;
    }

    private bool ReadLine(string line)
    {
        // Most lines are neither; only these few are parsed.
        bool doc = line.Contains(".md", StringComparison.OrdinalIgnoreCase);
        // A prompt you sent: Claude's user line that is not a tool result or Claude's own meta
        // line, or Codex's user message (UserText below drops the context Codex sends as user
        // messages of its own).
        bool prompt = _codex
            ? line.Contains("\"role\":\"user\"", StringComparison.Ordinal)
            : line.Contains("\"type\":\"user\"", StringComparison.Ordinal)
              && !line.Contains("\"tool_result\"", StringComparison.Ordinal)
              && !line.Contains("\"isMeta\":true", StringComparison.Ordinal);
        if (!doc && !prompt) return false;

        using var json = SessionStore.TryParse(line);
        if (json is null) return false;
        var el = json.RootElement;

        // Only a prompt sent since the pane opened clears its images. A doc written before then
        // still counts: a resumed session shows the last doc it wrote, if it is still there.
        if (prompt
            && SessionStore.GetString(el, "timestamp") is { } ts
            && DateTime.TryParse(ts, null, System.Globalization.DateTimeStyles.RoundtripKind, out var at)
            && (at = at.ToUniversalTime()) >= _since
            && (!_codex || CodexSessionStore.UserText(line) is not null)) PromptAt = at;
        if (!doc) return false;

        var found = (_codex ? CodexPaths(line) : ClaudePaths(el))
            .Select(Inside).LastOrDefault(p => p is not null);
        if (found is null) return false;
        Doc = found;
        return true;
    }

    /// <summary>The file_path of each Write or Edit in an assistant line.</summary>
    private static IEnumerable<string> ClaudePaths(JsonElement el)
    {
        if (!el.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object
            || !msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) yield break;
        foreach (var block in content.EnumerateArray())
            if (block.ValueKind == JsonValueKind.Object
                && SessionStore.GetString(block, "type") == "tool_use"
                && SessionStore.GetString(block, "name") is "Write" or "Edit" or "MultiEdit"
                && block.TryGetProperty("input", out var input)
                && SessionStore.GetString(input, "file_path") is { } path)
                yield return path;
    }

    // An apply_patch names each file on its own line. Read off the raw line, so it does not
    // matter which field of Codex's JSON carries the patch. The path is taken a JSON escape at a
    // time, so the \\ in a Windows path such as C:\\notes is not mistaken for the line break.
    private static readonly Regex PatchFile = new(@"\*\*\* (?:Add|Update) File: ((?:[^\\""]|\\.)+?)\\n", RegexOptions.Compiled);

    private static IEnumerable<string> CodexPaths(string line)
    {
        foreach (Match m in PatchFile.Matches(line))
        {
            string? path = null;
            try { using var s = JsonDocument.Parse($"\"{m.Groups[1].Value}\""); path = s.RootElement.GetString(); }
            catch (JsonException) { }
            if (path is not null) yield return path;
        }
    }

    /// <summary>The full path when it is a .md inside the session's folder that still exists, else null.</summary>
    private string? Inside(string path)
    {
        if (!path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var full = Path.GetFullPath(Path.Combine(_folder, path));
            return full.StartsWith(_folder, StringComparison.OrdinalIgnoreCase) && File.Exists(full) ? full : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}

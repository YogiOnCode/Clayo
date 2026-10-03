using System.IO;
using System.Text;
using System.Text.Json;

namespace CcxShell.Core;

public sealed class SessionInfo
{
    public required string SessionId { get; init; }

    public AgentKind Agent { get; init; } = AgentKind.Claude;

    /// <summary>The session this one was branched from, when the transcript says so (Codex). Claude's
    /// forks keep no such link; Clayo records theirs itself (SessionParents).</summary>
    public string? ParentId { get; init; }

    /// <summary>Working directory read from the transcript, not un-mangled from the folder name.</summary>
    public required string ProjectDir { get; init; }

    public string ProjectName => Path.GetFileName(ProjectDir.TrimEnd('\\', '/')) is { Length: > 0 } n
        ? n
        : ProjectDir;

    /// <summary>First human prompt. Fallback only — <see cref="AiTitle"/> is the good name.</summary>
    public string Preview { get; set; } = "";

    /// <summary>
    /// The title Claude Code generates for the session — the same text the `claude --resume`
    /// picker shows. Null on the handful of transcripts too short to have earned one.
    /// </summary>
    public string? AiTitle { get; set; }

    /// <summary>Most recent prompt, for the second line of the row.</summary>
    public string? LastPrompt { get; set; }

    public DateTime LastActivity { get; set; }
}

/// <summary>
/// Reads Claude Code transcripts from %USERPROFILE%\.claude\projects and keeps the list fresh.
/// Never writes into that tree.
/// </summary>
public sealed class SessionStore : ISessionSource
{
    private readonly string _root;
    private readonly Dictionary<string, (long size, DateTime mtime, SessionInfo info)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;
    private System.Threading.Timer? _debounce;

    /// <summary>Fired on a background thread after the transcript tree settles.</summary>
    public event Action? Changed;

    public SessionStore(string? root = null)
    {
        _root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "projects");
    }

    public bool RootExists => Directory.Exists(_root);

    public IReadOnlyList<SessionInfo> Scan()
    {
        if (!Directory.Exists(_root)) return Array.Empty<SessionInfo>();

        var results = new List<SessionInfo>();

        foreach (var file in Directory.EnumerateFiles(_root, "*.jsonl", SearchOption.AllDirectories))
        {
            // subagents/agent-*.jsonl are tool transcripts, not sessions you can resume.
            // Roughly 40 of the 217 files under the tree are these.
            if (string.Equals(Path.GetFileName(Path.GetDirectoryName(file)), "subagents",
                              StringComparison.OrdinalIgnoreCase)) continue;

            FileInfo fi;
            try { fi = new FileInfo(file); }
            catch { continue; }

            if (fi.Length == 0) continue;

            // Cheap invalidation: size + mtime. Transcripts are append-only in practice.
            if (_cache.TryGetValue(file, out var hit) && hit.size == fi.Length && hit.mtime == fi.LastWriteTimeUtc)
            {
                results.Add(hit.info);
                continue;
            }

            var parsed = Parse(fi);
            if (parsed is null) continue;

            _cache[file] = (fi.Length, fi.LastWriteTimeUtc, parsed);
            results.Add(parsed);
        }

        return results.OrderByDescending(s => s.LastActivity).ToList();
    }

    private static SessionInfo? Parse(FileInfo fi)
    {
        string? cwd = null;
        string? sessionId = null;
        string preview = "";

        try
        {
            // Head: cwd and sessionId appear on the first real line. First human prompt is
            // usually within the first few, but tool noise can push it down.
            using var reader = new StreamReader(fi.FullName, Encoding.UTF8);
            for (int i = 0; i < 40; i++)
            {
                var line = reader.ReadLine();
                if (line is null) break;
                if (line.Length == 0) continue;

                JsonElement el;
                try { el = JsonDocument.Parse(line).RootElement; }
                catch (JsonException) { continue; }

                cwd ??= GetString(el, "cwd");
                sessionId ??= GetString(el, "sessionId");

                if (preview.Length == 0 && GetString(el, "type") == "user")
                {
                    var text = ExtractUserText(el);
                    if (!string.IsNullOrWhiteSpace(text)) preview = Shorten(text, 120);
                }

                if (cwd is not null && sessionId is not null && preview.Length > 0) break;
            }
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }

        var (aiTitle, lastPrompt) = ReadTail(fi);

        // Session id falls back to the filename, which is the uuid.
        sessionId ??= Path.GetFileNameWithoutExtension(fi.Name);

        if (string.IsNullOrWhiteSpace(cwd))
        {
            // Last resort: the mangled folder name. Lossy — a path with a real dash in it
            // can't be told apart from a separator — so it's display-only, never used as cwd.
            cwd = fi.Directory?.Name ?? "(unknown)";
        }

        return new SessionInfo
        {
            SessionId = sessionId,
            ProjectDir = cwd,
            Preview = preview.Length > 0 ? preview : "(no prompt yet)",
            AiTitle = aiTitle,
            LastPrompt = lastPrompt,
            LastActivity = fi.LastWriteTime
        };
    }

    /// <summary>
    /// ai-title and last-prompt are rewritten as the conversation grows, so the useful copy is
    /// the last one. Transcripts run to a few MB, so read a window off the end rather than the
    /// whole file: measured across 177 real transcripts, every one that has a title has it
    /// inside the final 96 KB.
    /// </summary>
    private static (string? aiTitle, string? lastPrompt) ReadTail(FileInfo fi, int window = 96 * 1024)
    {
        string? title = null;
        string? prompt = null;

        try
        {
            using var fs = new FileStream(fi.FullName, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete);
            long start = Math.Max(0, fs.Length - window);
            fs.Seek(start, SeekOrigin.Begin);

            using var reader = new StreamReader(fs, Encoding.UTF8);

            // Seeking lands mid-line and mid-UTF-8. Throw the fragment away.
            if (start > 0) reader.ReadLine();

            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (line.Length == 0) continue;

                // Substring test first — JSON-parsing every line of a 96 KB window is wasteful
                // when only a handful are the two types we want.
                bool maybeTitle = line.Contains("\"ai-title\"", StringComparison.Ordinal);
                bool maybePrompt = line.Contains("\"last-prompt\"", StringComparison.Ordinal);
                if (!maybeTitle && !maybePrompt) continue;

                JsonElement el;
                try { el = JsonDocument.Parse(line).RootElement; }
                catch (JsonException) { continue; }

                switch (GetString(el, "type"))
                {
                    case "ai-title":
                        title = GetString(el, "aiTitle") ?? title;
                        break;
                    case "last-prompt":
                        prompt = GetString(el, "lastPrompt") ?? prompt;
                        break;
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        var cleanTitle = Clean(title ?? "");
        var cleanPrompt = Clean(prompt ?? "");

        return (cleanTitle.Length > 0 ? Shorten(cleanTitle, 90) : null,
                cleanPrompt.Length > 0 ? Shorten(cleanPrompt, 120) : null);
    }

    internal static string? GetString(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object
        && el.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    /// <summary>
    /// message.content is either a plain string or an array of blocks. Tool results and
    /// slash-command wrappers are skipped so the preview shows something a human typed.
    /// </summary>
    private static string ExtractUserText(JsonElement line)
    {
        if (line.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True)
            return "";

        if (!line.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object)
            return "";
        if (!msg.TryGetProperty("content", out var content))
            return "";

        if (content.ValueKind == JsonValueKind.String)
            return Clean(content.GetString() ?? "");

        if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object) continue;
                if (GetString(block, "type") != "text") continue;
                var t = Clean(GetString(block, "text") ?? "");
                if (t.Length > 0) return t;
            }
        }

        return "";
    }

    internal static string Clean(string s)
    {
        s = s.Trim();
        // Slash commands and hook output arrive wrapped in pseudo-XML.
        if (s.StartsWith('<') && s.Contains('>')) return "";
        if (s.StartsWith("Caveat:", StringComparison.Ordinal)) return "";
        return s;
    }

    internal static string Shorten(string s, int max)
    {
        s = string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return s.Length <= max ? s : s[..max].TrimEnd() + "…";
    }

    public void StartWatching()
    {
        if (!Directory.Exists(_root) || _watcher is not null) return;

        _watcher = new FileSystemWatcher(_root, "*.jsonl")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true
        };

        // Claude writes a line at a time; without debouncing this fires constantly.
        void Bump(object? _, FileSystemEventArgs __) =>
            _debounce?.Change(600, Timeout.Infinite);

        _debounce = new System.Threading.Timer(_ => Changed?.Invoke(), null, Timeout.Infinite, Timeout.Infinite);
        _watcher.Changed += Bump;
        _watcher.Created += Bump;
        _watcher.Deleted += Bump;
        _watcher.Renamed += Bump;
    }

    public void StopWatching()
    {
        _watcher?.Dispose();
        _watcher = null;
        _debounce?.Dispose();
        _debounce = null;
    }
}

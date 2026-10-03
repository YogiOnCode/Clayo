using System.IO;
using System.Text;
using System.Text.Json;

namespace CcxShell.Core;

/// <summary>
/// Reads Codex transcripts from CODEX_HOME (else ~\.codex) as sidebar rows, the way
/// SessionStore does for Claude Code. Never writes there. The layout is in docs/SETUP.md,
/// "Codex, as found on this machine".
/// </summary>
public sealed class CodexSessionStore(string home) : ISessionSource
{
    // A transcript only grows, and resuming one keeps its file time (docs/SETUP.md,
    // "Recency"), so its length is what says it changed.
    private readonly Dictionary<string, (long size, SessionInfo info)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;
    private System.Threading.Timer? _debounce;

    public event Action? Changed;

    public static CodexSessionStore ForThisUser() => new(
        Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } home
            ? home
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"));

    private string SessionsDir => Path.Combine(home, "sessions");

    public bool RootExists => Directory.Exists(SessionsDir);

    public IReadOnlyList<SessionInfo> Scan()
    {
        if (!RootExists) return [];

        var titles = Titles();
        var results = new List<SessionInfo>();
        foreach (var file in Directory.EnumerateFiles(SessionsDir, "rollout-*.jsonl", SearchOption.AllDirectories))
        {
            long size;
            try { size = new FileInfo(file).Length; }
            catch (IOException) { continue; }
            if (size == 0) continue;

            if (!_cache.TryGetValue(file, out var hit) || hit.size != size)
            {
                if (Parse(file) is not { } parsed) continue;
                hit = (size, parsed);
                _cache[file] = hit;
            }

            // Codex names sessions itself and keeps the names apart from the transcript; a
            // rename there changes no transcript, so the title is looked up every scan.
            hit.info.AiTitle = titles.GetValueOrDefault(hit.info.SessionId);
            results.Add(hit.info);
        }
        return results.OrderByDescending(s => s.LastActivity).ToList();
    }

    /// <summary>session_index.jsonl: one line per naming, so the last line for an id wins.</summary>
    private Dictionary<string, string> Titles()
    {
        var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var reader = new StreamReader(Open(Path.Combine(home, "session_index.jsonl")), Encoding.UTF8);
            while (reader.ReadLine() is { } line)
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (SessionStore.GetString(doc.RootElement, "id") is { } id
                        && SessionStore.Clean(SessionStore.GetString(doc.RootElement, "thread_name") ?? "") is { Length: > 0 } name)
                        titles[id] = SessionStore.Shorten(name, 90);
                }
                catch (JsonException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return titles;
    }

    /// <summary>
    /// Line 0 is session_meta: id, folder, and for a fork the session it came from. It also
    /// carries Codex's whole instructions (about 19 KB), so only the head and a window off
    /// the end are read, never the middle.
    /// </summary>
    private static SessionInfo? Parse(string file)
    {
        string? id, cwd, parent;
        DateTime started;
        string preview = "";
        try
        {
            using var reader = new StreamReader(Open(file), Encoding.UTF8);
            if (reader.ReadLine() is not { } first) return null;
            using (var meta = JsonDocument.Parse(first))
            {
                if (SessionStore.GetString(meta.RootElement, "type") != "session_meta"
                    || !meta.RootElement.TryGetProperty("payload", out var p)) return null;
                id = SessionStore.GetString(p, "id");
                cwd = SessionStore.GetString(p, "cwd");
                parent = SessionStore.GetString(p, "forked_from_id");
                started = Time(SessionStore.GetString(p, "timestamp")) ?? File.GetLastWriteTime(file);
            }
            if (id is null) return null;

            // The first thing you typed. Codex puts its own context in front as user messages
            // too, wrapped in pseudo-XML, which Clean drops.
            for (int i = 0; i < 60 && preview.Length == 0 && reader.ReadLine() is { } line; i++)
                preview = UserText(line) ?? "";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }

        var (last, lastPrompt) = ReadTail(file);
        return new SessionInfo
        {
            SessionId = id,
            Agent = AgentKind.Codex,
            ParentId = parent,
            ProjectDir = cwd ?? Path.GetDirectoryName(file) ?? "(unknown)",
            Preview = preview.Length > 0 ? SessionStore.Shorten(preview, 120) : "(no prompt yet)",
            LastPrompt = lastPrompt is null ? null : SessionStore.Shorten(lastPrompt, 120),
            LastActivity = last ?? started
        };
    }

    /// <summary>
    /// Last active is the last line's own timestamp: a resumed session's file can keep its old
    /// time. The last prompt comes from the same window. Measured: a whole turn is far below it.
    /// </summary>
    private static (DateTime? last, string? lastPrompt) ReadTail(string file, int window = 96 * 1024)
    {
        DateTime? last = null;
        string? prompt = null;
        try
        {
            using var fs = Open(file);
            long start = Math.Max(0, fs.Length - window);
            fs.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            if (start > 0) reader.ReadLine();

            while (reader.ReadLine() is { } line)
            {
                // A line still being written doesn't parse; the one before it stands.
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    last = Time(SessionStore.GetString(doc.RootElement, "timestamp")) ?? last;
                }
                catch (JsonException) { continue; }
                if (UserText(line) is { } text) prompt = text;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return (last, prompt);
    }

    /// <summary>A response_item message from the user, as typed; null for anything else.</summary>
    private static string? UserText(string line)
    {
        // Substring test first: most lines are tool calls and reasoning.
        if (!line.Contains("\"role\":\"user\"", StringComparison.Ordinal)) return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (SessionStore.GetString(root, "type") != "response_item"
                || !root.TryGetProperty("payload", out var p)
                || SessionStore.GetString(p, "type") != "message"
                || !p.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.Array) return null;
            foreach (var block in content.EnumerateArray())
                if (SessionStore.Clean(SessionStore.GetString(block, "text") ?? "") is { Length: > 0 } text) return text;
        }
        catch (JsonException) { }
        return null;
    }

    private static DateTime? Time(string? iso) =>
        DateTimeOffset.TryParse(iso, out var t) ? t.LocalDateTime : null;

    private static FileStream Open(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    public void StartWatching()
    {
        if (!Directory.Exists(home) || _watcher is not null) return;

        // The transcripts and session_index.jsonl (renames) are both *.jsonl under home.
        _watcher = new FileSystemWatcher(home, "*.jsonl")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true
        };
        void Bump(object? _, FileSystemEventArgs __) => _debounce?.Change(600, Timeout.Infinite);
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

using System.IO;
using System.Text;
using System.Text.Json;

namespace CcxShell.Core;

/// <summary>Where a Codex session's last turn stands, from its task_started / task_complete / turn_aborted events.</summary>
public enum TurnState { Working, Done, Stopped }

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
        string? model = null, effort = null;
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
            // The model comes first, in the turn's turn_context.
            for (int i = 0; i < 60 && preview.Length == 0 && reader.ReadLine() is { } line; i++)
            {
                preview = UserText(line) ?? "";
                if (model is null && line.Contains("\"turn_context\"", StringComparison.Ordinal))
                    (model, effort) = TurnContext(line) ?? (null, null);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }

        var tail = ReadTail(file);
        var last = tail.Last;
        if (tail.Model is not null) (model, effort) = (tail.Model, tail.Effort);
        return new SessionInfo
        {
            SessionId = id,
            Agent = AgentKind.Codex,
            ParentId = parent,
            ProjectDir = cwd ?? Path.GetDirectoryName(file) ?? "(unknown)",
            Preview = preview.Length > 0 ? SessionStore.Shorten(preview, 120) : "(no prompt yet)",
            LastPrompt = tail.Prompt is null ? null : SessionStore.Shorten(tail.Prompt, 120),
            LastActivity = last ?? started,
            Started = started,
            Turn = tail.Turn,
            // What the strip shows, from the same lines: Codex has no status line to relay.
            Status = tail.ContextSize > 0 || model is not null
                ? new SessionStatus(id, model, cwd, tail.ContextUsed, tail.ContextSize,
                    tail.ContextSize > 0 ? (int)(tail.ContextUsed * 100 / tail.ContextSize) : 0,
                    effort ?? "", tail.FiveHour, tail.SevenDay, last ?? started)
                : null
        };
    }

    /// <summary>
    /// The session a pane Clayo started turned out to be (docs/SETUP.md, "Finding a new Codex
    /// session"): for a fork, the one naming parentId; else a non-fork in the pane's folder.
    /// Either way started after the pane and not another pane's. Oldest first, so two panes
    /// in one folder bind in start order.
    /// </summary>
    public static SessionInfo? FindStarted(IEnumerable<SessionInfo> sessions, string folder, string? parentId,
                                           DateTime since, ISet<string> taken) =>
        sessions.Where(s => s.Started >= since && !taken.Contains(s.SessionId)
                            && (parentId is null
                                ? s.ParentId is null && string.Equals(Path.TrimEndingDirectorySeparator(s.ProjectDir),
                                      Path.TrimEndingDirectorySeparator(folder), StringComparison.OrdinalIgnoreCase)
                                : s.ParentId == parentId))
                .MinBy(s => s.Started);

    /// <summary>What the end of a transcript says. Context and limits are from the last token_count.</summary>
    private sealed record Tail(DateTime? Last, string? Prompt, TurnState? Turn, string? Model, string? Effort,
                               long ContextUsed, long ContextSize, Limit? FiveHour, Limit? SevenDay);

    /// <summary>
    /// Last active is the last line's own timestamp: a resumed session's file can keep its old
    /// time. The last prompt, the turn's state and the latest token count come from the same
    /// window. Measured: a whole turn is far below it.
    /// </summary>
    private static Tail ReadTail(string file, int window = 96 * 1024)
    {
        DateTime? last = null;
        string? prompt = null, model = null, effort = null;
        TurnState? turn = null;
        long used = 0, size = 0;
        Limit? fiveHour = null, sevenDay = null;
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
                    var root = doc.RootElement;
                    last = Time(SessionStore.GetString(root, "timestamp")) ?? last;
                    var type = SessionStore.GetString(root, "type");
                    if (type == "turn_context")
                        (model, effort) = TurnContext(line) ?? (model, effort);
                    else if (type == "event_msg" && root.TryGetProperty("payload", out var p))
                        switch (SessionStore.GetString(p, "type"))
                        {
                            case "task_started": turn = TurnState.Working; break;
                            case "task_complete": turn = TurnState.Done; break;
                            case "turn_aborted": turn = TurnState.Stopped; break;
                            case "token_count": Tokens(p, ref used, ref size, ref fiveHour, ref sevenDay); break;
                        }
                }
                catch (JsonException) { continue; }
                if (UserText(line) is { } text) prompt = text;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return new Tail(last, prompt, turn, model, effort, used, size, fiveHour, sevenDay);
    }

    /// <summary>The model and reasoning effort a turn ran with. Effort is null when Codex left it to its default.</summary>
    private static (string? model, string? effort)? TurnContext(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("payload", out var p)) return null;
            string? effort = p.TryGetProperty("collaboration_mode", out var mode)
                             && mode.ValueKind == JsonValueKind.Object
                             && mode.TryGetProperty("settings", out var set)
                ? SessionStore.GetString(set, "reasoning_effort")
                : null;
            return (SessionStore.GetString(p, "model"), effort);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// token_count: the context in use is the last request's total against the model's
    /// window. Codex's limits come as primary and secondary windows; only a 5-hour (300 min)
    /// or 7-day (10080 min) one maps onto the strip's two, so another length is left out.
    /// </summary>
    private static void Tokens(JsonElement p, ref long used, ref long size, ref Limit? fiveHour, ref Limit? sevenDay)
    {
        if (p.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object)
        {
            if (info.TryGetProperty("last_token_usage", out var u) && u.ValueKind == JsonValueKind.Object
                && u.TryGetProperty("total_tokens", out var t) && t.TryGetInt64(out var total)) used = total;
            if (info.TryGetProperty("model_context_window", out var w) && w.TryGetInt64(out var window)) size = window;
        }
        if (!p.TryGetProperty("rate_limits", out var limits) || limits.ValueKind != JsonValueKind.Object) return;
        foreach (var name in new[] { "primary", "secondary" })
        {
            if (!limits.TryGetProperty(name, out var l) || l.ValueKind != JsonValueKind.Object
                || !l.TryGetProperty("used_percent", out var pct) || !pct.TryGetDouble(out var percent)
                || !l.TryGetProperty("window_minutes", out var mins) || !mins.TryGetInt32(out var minutes)) continue;
            DateTimeOffset? resets = l.TryGetProperty("resets_at", out var r) && r.TryGetInt64(out var unix)
                ? DateTimeOffset.FromUnixTimeSeconds(unix) : null;
            var limit = new Limit((int)Math.Floor(percent), resets);
            if (minutes == 300) fiveHour = limit;
            else if (minutes == 10080) sevenDay = limit;
        }
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

using System.Globalization;
using System.IO;
using System.Text.Json;

namespace CcxShell.Core;

/// <summary>A usage limit as Claude Code reports it: percent used, floored, and when it resets.</summary>
public sealed record Limit(int Percent, DateTimeOffset? ResetsAt);

/// <summary>The account's 5-hour and 7-day limits, and when Claude last reported them.</summary>
public sealed record AccountLimits(Limit? FiveHour, Limit? SevenDay, DateTime Updated);

/// <summary>What one Claude session last told its status line.</summary>
public sealed record SessionStatus(
    string SessionId,
    string? Model,
    string? Cwd,
    long ContextUsed,
    long ContextSize,
    int ContextPercent,
    string Effort,
    Limit? FiveHour,
    Limit? SevenDay,
    DateTime Updated);

/// <summary>
/// Reads the status files the relay leaves in %LOCALAPPDATA%\Clayo\status (see StatusRelay),
/// one per session, and keeps them fresh. Clayo only shows what Claude hands its status line
/// (and, for the limits, what the user's own status line script saved).
/// </summary>
public sealed class StatusStore : IDisposable
{
    private readonly string _dir;
    private readonly Func<string> _fallbackEffort;
    private readonly string _usageCache;
    private readonly Dictionary<string, SessionStatus> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private FileSystemWatcher? _watcher;

    /// <summary>Fired on a background thread when a session's file brings a new status.</summary>
    public event Action<SessionStatus>? Changed;

    public StatusStore(string? dir = null, Func<string>? fallbackEffort = null, string? usageCache = null)
    {
        _dir = dir ?? StatusRelay.StatusDir;
        _fallbackEffort = fallbackEffort ?? DefaultEffort;
        _usageCache = usageCache ?? UsageCachePath;
    }

    public SessionStatus? Get(string sessionId)
    {
        lock (_lock) return _byId.GetValueOrDefault(sessionId);
    }

    /// <summary>
    /// The limits are the account's, not a session's, so the newest report wins. A session
    /// that has not called the API yet has none, and must not blank the ones we have. Some
    /// accounts' Claude Code sends none at all; then the user's script's cache stands in.
    /// </summary>
    public AccountLimits? Limits
    {
        get
        {
            AccountLimits? fromClaude;
            lock (_lock)
            {
                var newest = _byId.Values
                    .Where(s => s.FiveHour is not null || s.SevenDay is not null)
                    .MaxBy(s => s.Updated);
                fromClaude = newest is null ? null : new AccountLimits(newest.FiveHour, newest.SevenDay, newest.Updated);
            }
            var cached = ReadUsageCache(_usageCache);
            return cached is null || fromClaude?.Updated >= cached.Updated ? fromClaude : cached;
        }
    }

    /// <summary>
    /// Where the user's statusline.ps1 keeps the limits it fetches for itself when Claude's
    /// payload has none. Clayo only reads the file: the script calls the API, Clayo never does.
    /// </summary>
    public static string UsageCachePath => Path.Combine(
        Environment.GetEnvironmentVariable("TEMP") ?? Path.GetTempPath(), "claude", "statusline-usage-cache.json");

    private static AccountLimits? ReadUsageCache(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return ParseUsageCache(File.ReadAllText(path), File.GetLastWriteTimeUtc(path));
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>The script's cache: as rate_limits, but "utilization" for the percentage. Null when it holds no limits.</summary>
    public static AccountLimits? ParseUsageCache(string json, DateTime updated)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(json).RootElement; }
        catch (JsonException) { return null; }
        var h5 = ReadLimit(Obj(root, "five_hour"), "utilization");
        var d7 = ReadLimit(Obj(root, "seven_day"), "utilization");
        return h5 is null && d7 is null ? null : new AccountLimits(h5, d7, updated);
    }

    /// <summary>Reads what is there already, then follows changes.</summary>
    public void Start()
    {
        if (_watcher is not null) return;
        Directory.CreateDirectory(_dir);

        _watcher = new FileSystemWatcher(_dir, "*.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
        };
        // The relay renames a finished file into place, so a rename is the usual event.
        _watcher.Created += (_, e) => Load(e.FullPath);
        _watcher.Changed += (_, e) => Load(e.FullPath);
        _watcher.Renamed += (_, e) => Load(e.FullPath);
        _watcher.EnableRaisingEvents = true;

        foreach (var file in Directory.EnumerateFiles(_dir, "*.json")) Load(file);
    }

    /// <summary>Reads one file. Anything unreadable keeps the last good status.</summary>
    public void Load(string path)
    {
        string json;
        DateTime updated;
        try
        {
            // Share delete as well, or the relay's rename over this file fails while we read.
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            json = reader.ReadToEnd();
            updated = File.GetLastWriteTimeUtc(path);
        }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }

        if (Parse(json, updated, _fallbackEffort) is not { } status) return;

        lock (_lock)
        {
            if (_byId.TryGetValue(status.SessionId, out var had) && had == status) return;
            _byId[status.SessionId] = status;
        }
        Changed?.Invoke(status);
    }

    /// <summary>
    /// One status line payload. Null for anything that is not a whole JSON object with a
    /// session id, so a half-written or foreign file is ignored rather than shown.
    /// </summary>
    public static SessionStatus? Parse(string json, DateTime updated, Func<string> fallbackEffort)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(json).RootElement; }
        catch (JsonException) { return null; }
        if (root.ValueKind != JsonValueKind.Object) return null;

        if (Str(root, "session_id") is not { } id || !Guid.TryParse(id, out _)) return null;

        var window = Obj(root, "context_window");
        long size = Num(window, "context_window_size") is { } sz and > 0 ? (long)sz : 200_000;

        // The same sum the user's statusline.ps1 shows: what the next request sends.
        var usage = Obj(window, "current_usage");
        long used = (long)((Num(usage, "input_tokens") ?? 0)
                           + (Num(usage, "cache_creation_input_tokens") ?? 0)
                           + (Num(usage, "cache_read_input_tokens") ?? 0));

        var limits = Obj(root, "rate_limits");

        return new SessionStatus(
            id,
            Str(Obj(root, "model"), "display_name"),
            Str(root, "cwd"),
            used,
            size,
            (int)(used * 100 / size),
            Str(Obj(root, "effort"), "level") is { Length: > 0 } e ? e : fallbackEffort(),
            ReadLimit(Obj(limits, "five_hour"), "used_percentage"),
            ReadLimit(Obj(limits, "seven_day"), "used_percentage"),
            updated);
    }

    /// <summary>
    /// Claude Code 2.1 puts the effort in the payload. Older ones did not, and the user's
    /// script reads the variable, then settings.json, then assumes medium.
    /// </summary>
    public static string Effort(string? payload, string? env, string? settings) =>
        new[] { payload, env, settings }.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? "medium";

    private static string DefaultEffort()
    {
        string? fromSettings = null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(StatusRelay.ClaudeConfigDir, "settings.json")));
            fromSettings = Str(doc.RootElement, "effortLevel");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (JsonException) { }
        return Effort(null, Environment.GetEnvironmentVariable("CLAUDE_CODE_EFFORT_LEVEL"), fromSettings);
    }

    private static Limit? ReadLimit(JsonElement? el, string percentKey)
    {
        if (Num(el, percentKey) is not { } pct) return null;

        // Epoch seconds today; the user's script also takes an ISO time, so do we.
        DateTimeOffset? resets = null;
        if (el is { } o && o.TryGetProperty("resets_at", out var r))
        {
            if (r.ValueKind == JsonValueKind.Number && r.TryGetInt64(out var secs) && secs > 0)
                resets = DateTimeOffset.FromUnixTimeSeconds(secs);
            else if (r.ValueKind == JsonValueKind.String
                     && DateTimeOffset.TryParse(r.GetString(), CultureInfo.InvariantCulture,
                                                DateTimeStyles.AssumeUniversal, out var at))
                resets = at;
        }
        return new Limit((int)Math.Floor(pct), resets);
    }

    private static JsonElement? Obj(JsonElement? el, string name) =>
        el is { ValueKind: JsonValueKind.Object } o
        && o.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.Object
            ? v
            : null;

    private static string? Str(JsonElement? el, string name) =>
        el is { ValueKind: JsonValueKind.Object } o
        && o.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static double? Num(JsonElement? el, string name) =>
        el is { ValueKind: JsonValueKind.Object } o
        && o.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble()
            : null;

    public void Dispose()
    {
        _watcher?.Dispose();
        _watcher = null;
    }
}

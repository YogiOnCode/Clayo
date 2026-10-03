#:property TargetFramework=net8.0-windows
#:project ../src/Clayo/CcxShell.csproj
using CcxShell.Core;

// Captured from Claude Code 2.1.288 through the relay, on start-up before any prompt, with
// the user's paths swapped for C:\work. So no rate_limits yet and current_usage is null.
const string fresh = """
{"session_id":"3f136615-e5ba-45b2-9766-6cc4d88ef5d9","transcript_path":"C:\\Users\\me\\.claude\\projects\\C--work\\3f136615-e5ba-45b2-9766-6cc4d88ef5d9.jsonl","cwd":"C:\\work","effort":{"level":"medium"},"model":{"id":"claude-opus-5-5[1m]","display_name":"Opus 5.5 (1M context)"},"workspace":{"current_dir":"C:\\work","project_dir":"C:\\work","added_dirs":[]},"version":"2.1.288","output_style":{"name":"default"},"cost":{"total_cost_usd":0,"total_duration_ms":902,"total_api_duration_ms":0,"total_lines_added":0,"total_lines_removed":0},"context_window":{"total_input_tokens":0,"total_output_tokens":0,"context_window_size":1000000,"current_usage":null,"used_percentage":null,"remaining_percentage":null},"exceeds_200k_tokens":false,"fast_mode":false,"thinking":{"enabled":true}}
""";

// The same session after a reply: current_usage and rate_limits filled in, in the shape the
// user's statusline.ps1 reads them.
const string busy = """
{"session_id":"3f136615-e5ba-45b2-9766-6cc4d88ef5d9","cwd":"C:\\work","effort":{"level":"xhigh"},"model":{"id":"claude-opus-5-5[1m]","display_name":"Opus 5.5 (1M context)"},"context_window":{"context_window_size":1000000,"current_usage":{"input_tokens":12,"output_tokens":900,"cache_creation_input_tokens":40000,"cache_read_input_tokens":300000}},"rate_limits":{"five_hour":{"used_percentage":42.7,"resets_at":1790000000},"seven_day":{"used_percentage":9,"resets_at":"2026-10-08T10:00:00Z"}}}
""";

var at = new DateTime(2026, 10, 3, 1, 0, 0, DateTimeKind.Utc);
string Unused() => throw new InvalidOperationException("fallback read although the payload had an effort");

int fail = 0;
void Check<T>(string name, T got, T want)
{
    var ok = EqualityComparer<T>.Default.Equals(got, want);
    if (!ok) fail++;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}  got={got} want={want}");
}

{
    var s = StatusStore.Parse(fresh, at, Unused)!;
    Check("real payload: session id", s.SessionId, "3f136615-e5ba-45b2-9766-6cc4d88ef5d9");
    Check("real payload: model", s.Model, "Opus 5.5 (1M context)");
    Check("real payload: cwd", s.Cwd, @"C:\work");
    Check("real payload: context size", s.ContextSize, 1_000_000L);
    Check("real payload: no usage yet is 0 used", s.ContextUsed, 0L);
    Check("real payload: and 0%", s.ContextPercent, 0);
    Check("real payload: effort from the payload", s.Effort, "medium");
    Check("missing rate_limits: no 5h", s.FiveHour, null);
    Check("missing rate_limits: no 7d", s.SevenDay, null);
    Check("last update is the file's time", s.Updated, at);
}

{
    var s = StatusStore.Parse(busy, at, Unused)!;
    Check("context used is input + cache creation + cache read", s.ContextUsed, 340_012L);
    Check("context percent is floored", s.ContextPercent, 34);
    Check("5h percent is floored", s.FiveHour?.Percent, 42);
    Check("5h reset from epoch seconds", s.FiveHour?.ResetsAt, DateTimeOffset.FromUnixTimeSeconds(1790000000));
    Check("7d percent", s.SevenDay?.Percent, 9);
    Check("7d reset from an ISO time", s.SevenDay?.ResetsAt, new DateTimeOffset(2026, 10, 8, 10, 0, 0, TimeSpan.Zero));
}

// Effort: the payload's own, else CLAUDE_CODE_EFFORT_LEVEL, else effortLevel in settings.json, else medium.
Check("effort: payload first", StatusStore.Effort("high", "low", "max"), "high");
Check("effort: then the environment", StatusStore.Effort(null, "low", "max"), "low");
Check("effort: then settings.json", StatusStore.Effort(null, "", "max"), "max");
Check("effort: else medium", StatusStore.Effort(null, null, null), "medium");
Check("payload without an effort uses the fallback",
    StatusStore.Parse("""{"session_id":"a1b2c3d4-0000-0000-0000-000000000000"}""", at, () => "low")?.Effort, "low");

// Partial or bad JSON is ignored, never thrown.
Check("half a file is ignored", StatusStore.Parse(busy[..60], at, Unused), null);
Check("not JSON is ignored", StatusStore.Parse("Claude", at, Unused), null);
Check("an array is ignored", StatusStore.Parse("[1,2]", at, Unused), null);
Check("no session id is ignored", StatusStore.Parse("""{"cwd":"C:\\work"}""", at, Unused), null);
Check("a session id that is not a guid is ignored",
    StatusStore.Parse("""{"session_id":"..\\..\\evil"}""", at, Unused), null);

// The store over a real folder: one file per session, account limits from the newest that has them.
var dir = Path.Combine(Path.GetTempPath(), "clayo-check-status-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);
try
{
    string Write(string id, string json, DateTime when)
    {
        var path = Path.Combine(dir, id + ".json");
        File.WriteAllText(path, json);
        File.SetLastWriteTimeUtc(path, when);
        return path;
    }

    const string a = "aaaaaaaa-0000-0000-0000-000000000000";
    const string b = "bbbbbbbb-0000-0000-0000-000000000000";
    const string c = "cccccccc-0000-0000-0000-000000000000";
    Write(a, busy.Replace("3f136615-e5ba-45b2-9766-6cc4d88ef5d9", a), at);
    Write(b, busy.Replace("3f136615-e5ba-45b2-9766-6cc4d88ef5d9", b).Replace("42.7", "77"), at.AddMinutes(5));
    // Newest of all, but fresh: no limits yet, so it must not blank the account's.
    Write(c, fresh.Replace("3f136615-e5ba-45b2-9766-6cc4d88ef5d9", c), at.AddMinutes(9));
    Write("broken", busy[..80], at.AddMinutes(10));

    // A usage cache that is not there: the real one must not leak into these checks.
    using var store = new StatusStore(dir, () => "medium", Path.Combine(dir, "no-usage-cache.json"));
    store.Start();
    Check("each session's file is read", store.Get(a)?.ContextPercent, 34);
    Check("lookup ignores case", store.Get(b.ToUpperInvariant())?.FiveHour?.Percent, 77);
    Check("the newest file with limits wins", store.Limits?.FiveHour?.Percent, 77);
    Check("a bad file is skipped", store.Get("broken"), null);

    // A rewrite of an older session makes it the newest.
    store.Load(Write(a, busy.Replace("3f136615-e5ba-45b2-9766-6cc4d88ef5d9", a).Replace("42.7", "51"), at.AddMinutes(20)));
    Check("an updated file takes over the limits", store.Limits?.FiveHour?.Percent, 51);

    // A file cut short mid-write keeps what we had.
    store.Load(Write(b, "{\"session_id\":\"" + b + "\",\"cwd\":", at.AddMinutes(30)));
    Check("partial JSON keeps the last good status", store.Get(b)?.FiveHour?.Percent, 77);

    // The watcher picks up a new file the way the relay writes it: a temp file renamed into place.
    var seen = new ManualResetEventSlim();
    store.Changed += s => { if (s.SessionId == "dddddddd-0000-0000-0000-000000000000") seen.Set(); };
    var tmp = Path.Combine(dir, "x.tmp");
    File.WriteAllText(tmp, fresh.Replace("3f136615-e5ba-45b2-9766-6cc4d88ef5d9", "dddddddd-0000-0000-0000-000000000000"));
    File.Move(tmp, Path.Combine(dir, "dddddddd-0000-0000-0000-000000000000.json"));
    Check("the watcher sees a renamed-in file", seen.Wait(5000), true);
}
finally
{
    Directory.Delete(dir, recursive: true);
}

// The user's statusline.ps1 cache, for accounts whose Claude Code sends no rate_limits. A real
// file's shape, trimmed: utilization rather than used_percentage, ISO reset times.
const string cache = """
{
    "five_hour":  { "utilization":  2.0, "resets_at":  "2026-10-03T13:39:59.976874+00:00", "limit_dollars":  null },
    "seven_day":  { "utilization":  16.0, "resets_at":  "2026-10-07T01:59:59.976900+00:00" },
    "extra_usage": { "is_enabled": false }
}
""";
{
    var c = StatusStore.ParseUsageCache(cache, at)!;
    Check("cache: 5h percent", c.FiveHour?.Percent, 2);
    Check("cache: 5h reset", c.FiveHour?.ResetsAt, new DateTimeOffset(2026, 10, 3, 13, 39, 59, 976, TimeSpan.Zero).AddTicks(8740));
    Check("cache: 7d percent", c.SevenDay?.Percent, 16);
    Check("cache: updated is the file's time", c.Updated, at);
    Check("cache: no limits in it is none", StatusStore.ParseUsageCache("""{"extra_usage":{}}""", at), null);
    Check("cache: bad JSON is none", StatusStore.ParseUsageCache("{\"five_hour\":", at), null);
}

var dir2 = Path.Combine(Path.GetTempPath(), "clayo-check-cache-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir2);
try
{
    var cachePath = Path.Combine(dir2, "usage.json");
    using var store = new StatusStore(dir2, () => "medium", cachePath);
    store.Start();
    Check("no sessions and no cache is no limits", store.Limits, null);

    File.WriteAllText(cachePath, cache);
    File.SetLastWriteTimeUtc(cachePath, at);
    Check("the cache fills in when Claude sends none", store.Limits?.SevenDay?.Percent, 16);

    // Claude's own report, newer than the cache, wins; an older one does not.
    var s = Path.Combine(dir2, "aaaaaaaa-0000-0000-0000-000000000000.json");
    File.WriteAllText(s, busy.Replace("3f136615-e5ba-45b2-9766-6cc4d88ef5d9", "aaaaaaaa-0000-0000-0000-000000000000"));
    File.SetLastWriteTimeUtc(s, at.AddMinutes(1));
    store.Load(s);
    Check("a newer report from Claude wins", store.Limits?.SevenDay?.Percent, 9);
    File.SetLastWriteTimeUtc(cachePath, at.AddMinutes(2));
    Check("a newer cache wins", store.Limits?.SevenDay?.Percent, 16);
}
finally
{
    Directory.Delete(dir2, recursive: true);
}

// Git: the same numbers as the user's script, binary files (-) skipped.
Check("numstat added", GitInfo.ParseNumstat("3\t1\ta.cs\n10\t0\tb.cs\n-\t-\tlogo.png\n").Added, 13);
Check("numstat deleted", GitInfo.ParseNumstat("3\t1\ta.cs\n10\t0\tb.cs\n-\t-\tlogo.png\n").Deleted, 1);
Check("numstat empty", GitInfo.ParseNumstat(""), (0, 0));
Check("the temp folder is not a repo", GitInfo.IsRepo(Path.GetTempPath()), false);
Check("a folder outside any repo reads nothing", GitInfo.Read(Path.GetTempPath()), null);
// Run from checks/, inside this checkout.
Check("this checkout is a repo", GitInfo.IsRepo(Environment.CurrentDirectory), true);
Check("and has a branch", GitInfo.Read(Environment.CurrentDirectory)?.Branch is { Length: > 0 }, true);

Console.WriteLine(fail == 0 ? "\nall checks passed" : $"\n{fail} FAILED");
return fail;

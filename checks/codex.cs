#:property TargetFramework=net8.0-windows
#:project ../CcxShell.csproj
using CcxShell.Core;

// A fake CODEX_HOME in a temp folder. The lines are cut from real codex-cli 0.160.0
// transcripts (2026-10-02), with base_instructions and other bulk left out.
var home = Path.Combine(Path.GetTempPath(), "clayo-check-codex-" + Guid.NewGuid().ToString("N"));
var day = Path.Combine(home, "sessions", "2026", "10", "02");

int fail = 0;
void Check<T>(string name, T got, T want)
{
    var ok = EqualityComparer<T>.Default.Equals(got, want);
    if (!ok) fail++;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}  got={got} want={want}");
}

string Meta(string id, string cwd, string started, string? parent = null) =>
    "{\"timestamp\":\"" + started + "\",\"ordinal\":0,\"type\":\"session_meta\",\"payload\":{\"session_id\":\"" + id + "\",\"id\":\"" + id + "\","
    + (parent is null ? "" : "\"forked_from_id\":\"" + parent + "\",\"forked_from_ordinal_exclusive\":67,")
    + "\"timestamp\":\"" + started + "\",\"cwd\":\"" + cwd.Replace(@"\", @"\\")
    + "\",\"originator\":\"codex-tui\",\"cli_version\":\"0.160.0\",\"base_instructions\":{\"text\":\"You are Codex...\"}}}";
string User(string at, string text) =>
    "{\"timestamp\":\"" + at + "\",\"ordinal\":8,\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"" + text + "\"}]}}";
string Context(string at, string cwd) =>
    User(at, $"<environment_context>\\n  <cwd>{cwd.Replace(@"\", @"\\\\")}</cwd>\\n  <shell>powershell</shell>\\n</environment_context>");
string Event(string at, string type) =>
    "{\"timestamp\":\"" + at + "\",\"ordinal\":20,\"type\":\"event_msg\",\"payload\":{\"type\":\"" + type + "\"}}";
string Write(string name, params string[] lines)
{
    Directory.CreateDirectory(day);
    var path = Path.Combine(day, name);
    File.WriteAllText(path, string.Join("\n", lines) + "\n");
    return path;
}
DateTime Local(string iso) => DateTimeOffset.Parse(iso).LocalDateTime;

const string a = "01a0fd44-440a-7fb1-ba60-9ebb7a28ae46";
const string b = "01a0fd49-f4bd-7d51-a1f3-305c520b8ad1";
const string fork = "01a0fd5a-4ae7-7e12-9be4-5cd951206df2";
const string noIndex = "01a0fd5f-5ca5-7150-aac4-1f57a2afb465";

try
{
    var store = new CodexSessionStore(home);
    Check("no sessions folder: nothing", (store.RootExists, store.Scan().Count), (false, 0));

    // A plain session.
    Write($"rollout-2026-10-02T17-38-24-{a}.jsonl",
        Meta(a, @"C:\Yogi\Clayo", "2026-10-02T15:38:24.825Z"),
        Event("2026-10-02T15:38:54.300Z", "task_started"),
        Context("2026-10-02T15:38:54.310Z", @"C:\Yogi\Clayo"),
        User("2026-10-02T15:38:54.320Z", "hai are you ready"),
        Event("2026-10-02T15:40:01.565Z", "task_complete"));

    // Resumed later: the file grew, its time stayed old (docs/SETUP.md, "Recency").
    var resumed = Write($"rollout-2026-10-02T17-44-37-{b}.jsonl",
        Meta(b, @"C:\Users\you", "2026-10-02T15:44:37.570Z"),
        Context("2026-10-02T15:44:45.990Z", @"C:\Users\you"),
        User("2026-10-02T15:44:46.000Z", "checking what we can do"),
        Event("2026-10-02T15:44:49.000Z", "task_complete"),
        User("2026-10-02T16:11:40.000Z", "just testing resume, reply with ok"),
        Event("2026-10-02T16:11:45.384Z", "task_complete"));
    File.SetLastWriteTime(resumed, Local("2026-10-02T15:44:49.000Z"));

    // A fork: its own file and id, the parent named in session_meta, no copied history.
    Write($"rollout-2026-10-02T18-02-28-{fork}.jsonl",
        Meta(fork, @"C:\Yogi\Clayo", "2026-10-02T16:02:28.375Z", parent: a),
        User("2026-10-02T16:02:30.000Z", "so this is the fork?"),
        Event("2026-10-02T16:02:44.792Z", "task_complete"));

    // Not in session_index yet.
    Write($"rollout-2026-10-02T18-08-00-{noIndex}.jsonl",
        Meta(noIndex, @"C:\Temp\codex-approval", "2026-10-02T16:08:00.429Z"),
        User("2026-10-02T16:08:20.000Z", "Create a file called hello.txt here containing the word hi."),
        Event("2026-10-02T16:08:34.924Z", "turn_aborted"));

    // Not sessions.
    Write("other.jsonl", Meta("nope", @"C:\x", "2026-10-02T10:00:00Z"));
    Write("rollout-broken.jsonl", "{not json");
    Write("rollout-empty.jsonl");

    File.WriteAllText(Path.Combine(home, "session_index.jsonl"), string.Join("\n",
        $$"""{"id":"{{a}}","thread_name":"Confirm readiness","updated_at":"2026-10-02T15:38:57Z"}""",
        $$"""{"id":"{{b}}","thread_name":"Check options","updated_at":"2026-10-02T15:44:49Z"}""",
        $$"""{"id":"{{fork}}","thread_name":"Confirm readiness","updated_at":"2026-10-02T16:02:28Z"}""",
        $$"""{"id":"{{b}}","thread_name":"Check available options","updated_at":"2026-10-02T15:50:00Z"}""") + "\n");

    var rows = store.Scan().ToDictionary(s => s.SessionId);
    Check("rollout files only", rows.Count, 4);
    Check("newest first", store.Scan()[0].SessionId, b);

    var plain = rows[a];
    Check("plain: codex", plain.Agent, AgentKind.Codex);
    Check("plain: folder from session_meta", plain.ProjectDir, @"C:\Yogi\Clayo");
    Check("plain: title from session_index", plain.AiTitle, "Confirm readiness");
    Check("plain: preview skips Codex's own context", plain.Preview, "hai are you ready");
    Check("plain: last active is the last line", plain.LastActivity, Local("2026-10-02T15:40:01.565Z"));
    Check("plain: no parent", plain.ParentId, null);

    Check("resumed: last line beats the file time", rows[b].LastActivity, Local("2026-10-02T16:11:45.384Z"));
    Check("resumed: last prompt", rows[b].LastPrompt, "just testing resume, reply with ok");
    Check("resumed: a later rename wins", rows[b].AiTitle, "Check available options");

    Check("fork: parent from session_meta", rows[fork].ParentId, a);
    Check("fork: its own first prompt", rows[fork].Preview, "so this is the fork?");
    Check("fork: the parent's title, as Codex gives it", rows[fork].AiTitle, "Confirm readiness");

    Check("no index entry: no title", rows[noIndex].AiTitle, null);
    Check("no index entry: the prompt to fall back on", rows[noIndex].Preview, "Create a file called hello.txt here containing the word hi.");

    // Grows while Clayo watches: the same length means unchanged, a new length is read again.
    File.AppendAllText(resumed,
        User("2026-10-02T17:00:00.000Z", "one more") + "\n" + Event("2026-10-02T17:00:05.000Z", "task_complete") + "\n");
    File.SetLastWriteTime(resumed, Local("2026-10-02T15:44:49.000Z"));
    var again = store.Scan().First(s => s.SessionId == b);
    Check("grown: new last prompt", again.LastPrompt, "one more");
    Check("grown: new last active", again.LastActivity, Local("2026-10-02T17:00:05.000Z"));

    // A line half written: the one before it counts.
    File.AppendAllText(resumed, """{"timestamp":"2026-10-02T18:00:00.000Z","type":"event_ms""");
    Check("half-written last line ignored", store.Scan().First(s => s.SessionId == b).LastActivity, Local("2026-10-02T17:00:05.000Z"));

    // Binding a pane Clayo started to the transcript Codex then writes (docs/SETUP.md,
    // "Finding a new Codex session").
    var all = store.Scan();
    var none = new HashSet<string>();
    Check("started: from session_meta", rows[a].Started, Local("2026-10-02T15:38:24.825Z"));
    Check("new pane: the session started in its folder after it",
        CodexSessionStore.FindStarted(all, @"C:\Yogi\Clayo\", null, Local("2026-10-02T15:30:00Z"), none)?.SessionId, a);
    Check("new pane: not one started before it",
        CodexSessionStore.FindStarted(all, @"C:\Yogi\Clayo", null, Local("2026-10-02T15:39:00Z"), none)?.SessionId, null);
    Check("new pane: not one another pane has",
        CodexSessionStore.FindStarted(all, @"c:\yogi\clayo", null, Local("2026-10-02T15:30:00Z"), new HashSet<string> { a })?.SessionId, null);
    Check("new pane: not a fork in the same folder",
        CodexSessionStore.FindStarted(all, @"C:\Yogi\Clayo", null, Local("2026-10-02T16:00:00Z"), none)?.SessionId, null);
    Check("new pane: not another folder's",
        CodexSessionStore.FindStarted(all, @"C:\Other", null, Local("2026-10-02T15:30:00Z"), none)?.SessionId, null);
    Check("fork pane: the session naming its parent",
        CodexSessionStore.FindStarted(all, @"C:\Elsewhere", a, Local("2026-10-02T16:00:00Z"), none)?.SessionId, fork);
    Check("fork pane: nothing forked from another",
        CodexSessionStore.FindStarted(all, @"C:\Yogi\Clayo", b, Local("2026-10-02T16:00:00Z"), none)?.SessionId, null);

    // Status (step 7): the turn's state, and the strip's numbers from turn_context and token_count.
    Check("turn: complete is done", rows[a].Turn, TurnState.Done);
    Check("turn: aborted is stopped", rows[noIndex].Turn, TurnState.Stopped);
    Check("no token_count, no model: nothing for the strip", rows[a].Status, null);

    string TurnContext(string at, string model, string? effort) =>
        "{\"timestamp\":\"" + at + "\",\"type\":\"turn_context\",\"payload\":{\"model\":\"" + model
        + "\",\"collaboration_mode\":{\"mode\":\"default\",\"settings\":{\"model\":\"" + model + "\",\"reasoning_effort\":"
        + (effort is null ? "null" : "\"" + effort + "\"") + "}}}}";
    string TokenCount(string at, long total, long window, string limits) =>
        "{\"timestamp\":\"" + at + "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":"
        + (total - 268) + ",\"output_tokens\":268,\"total_tokens\":" + total + "},\"model_context_window\":" + window
        + "},\"rate_limits\":{\"limit_id\":\"codex\"," + limits + "}}}";
    string Window(int percent, int minutes, long resets) =>
        "{\"used_percent\":" + percent + ".5,\"window_minutes\":" + minutes + ",\"resets_at\":" + resets + "}";

    const string working = "01a0fd70-0000-7000-8000-000000000001";
    Write($"rollout-2026-10-02T19-00-00-{working}.jsonl",
        Meta(working, @"C:\Yogi\Clayo", "2026-10-02T17:00:00.000Z"),
        TurnContext("2026-10-02T17:00:01.000Z", "gpt-6-luna", null),
        Event("2026-10-02T17:00:02.000Z", "task_started"),
        User("2026-10-02T17:00:02.100Z", "write the tests"),
        TokenCount("2026-10-02T17:00:05.000Z", 46800, 258400,
            "\"primary\":" + Window(12, 300, 1790000000) + ",\"secondary\":" + Window(40, 10080, 1790500000)),
        TurnContext("2026-10-02T17:00:06.000Z", "gpt-6-luna", "high"),
        Event("2026-10-02T17:00:07.000Z", "task_complete"),
        Event("2026-10-02T17:00:08.000Z", "task_started"));
    const string monthly = "01a0fd70-0000-7000-8000-000000000002";
    Write($"rollout-2026-10-02T19-10-00-{monthly}.jsonl",
        Meta(monthly, @"C:\Yogi\Clayo", "2026-10-02T17:10:00.000Z"),
        TokenCount("2026-10-02T17:10:05.000Z", 1000, 258400, "\"primary\":" + Window(3, 43200, 1793547537) + ",\"secondary\":null"),
        Event("2026-10-02T17:10:06.000Z", "task_complete"));

    var now = store.Scan().ToDictionary(s => s.SessionId);
    var st = now[working].Status!;
    Check("turn: started after the last complete is working", now[working].Turn, TurnState.Working);
    Check("strip: model from the latest turn_context", st.Model, "gpt-6-luna");
    Check("strip: effort from the latest turn_context", st.Effort, "high");
    Check("strip: context is the last request's total", (st.ContextUsed, st.ContextSize, st.ContextPercent), (46800L, 258400L, 18));
    Check("strip: a 5-hour window, floored", st.FiveHour, new Limit(12, DateTimeOffset.FromUnixTimeSeconds(1790000000)));
    Check("strip: a 7-day window", st.SevenDay, new Limit(40, DateTimeOffset.FromUnixTimeSeconds(1790500000)));
    Check("strip: folder", st.Cwd, @"C:\Yogi\Clayo");
    var mo = now[monthly].Status!;
    Check("strip: a 30-day window fits neither", (mo.FiveHour, mo.SevenDay), ((Limit?)null, (Limit?)null));
    Check("strip: no turn_context, no effort", mo.Effort, "");
}
finally
{
    if (Directory.Exists(home)) Directory.Delete(home, recursive: true);
}

Console.WriteLine(fail == 0 ? "\nall checks passed" : $"\n{fail} FAILED");
return fail;

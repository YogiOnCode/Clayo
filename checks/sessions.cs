#:property TargetFramework=net10.0-windows
#:project ../src/Clayo/CcxShell.csproj
using CcxShell.Core;

// A fake ~/.claude/projects in a temp folder. Once the watcher runs, a scan reads only the
// transcripts it reported (issue #21), so these check that a new, a changed and a deleted
// transcript still reach the sidebar.
var root = Path.Combine(Path.GetTempPath(), "clayo-check-sessions-" + Guid.NewGuid().ToString("N"));
var project = Path.Combine(root, "C--x");

int fail = 0;
void Check<T>(string name, T got, T want)
{
    var ok = EqualityComparer<T>.Default.Equals(got, want);
    if (!ok) fail++;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}  got={got} want={want}");
}

string User(string id, string text) =>
    "{\"type\":\"user\",\"cwd\":\"C:\\\\x\",\"sessionId\":\"" + id + "\",\"message\":{\"role\":\"user\",\"content\":\"" + text + "\"}}\n";
string Path_(string id) => Path.Combine(project, id + ".jsonl");

// The watcher reports a moment after the write, so give it up to 5 s to show up in a scan.
IReadOnlyList<SessionInfo> ScanUntil(SessionStore store, Func<IReadOnlyList<SessionInfo>, bool> done)
{
    var rows = store.Scan();
    for (int i = 0; i < 50 && !done(rows); i++)
    {
        Thread.Sleep(100);
        rows = store.Scan();
    }
    return rows;
}

var store = new SessionStore(root);
try
{
    Directory.CreateDirectory(Path.Combine(project, "subagents"));
    File.WriteAllText(Path_("a"), User("a", "first"));
    File.WriteAllText(Path.Combine(project, "subagents", "agent-1.jsonl"), User("agent-1", "tool"));

    Check("before watching: every file is read", store.Scan().Count, 1);
    Check("subagent transcripts are not sessions", store.Scan().Any(s => s.SessionId == "agent-1"), false);

    store.StartWatching();
    Check("first scan while watching", store.Scan().Count, 1);

    File.WriteAllText(Path_("b"), User("b", "second"));
    Check("a new transcript appears", ScanUntil(store, r => r.Count == 2).Count, 2);

    File.AppendAllText(Path_("a"), "{\"type\":\"last-prompt\",\"lastPrompt\":\"later\"}\n");
    Check("a changed transcript is read again",
        ScanUntil(store, r => r.First(s => s.SessionId == "a").LastPrompt == "later").First(s => s.SessionId == "a").LastPrompt, "later");

    File.Delete(Path_("b"));
    Check("a deleted transcript goes", ScanUntil(store, r => r.Count == 1).Count, 1);

    File.WriteAllText(Path.Combine(project, "subagents", "agent-2.jsonl"), User("agent-2", "tool"));
    Thread.Sleep(1000);
    Check("a new subagent transcript still is not a session", store.Scan().Count, 1);
}
finally
{
    store.StopWatching();
    if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
}

Console.WriteLine(fail == 0 ? "\nall checks passed" : $"\n{fail} FAILED");
return fail;

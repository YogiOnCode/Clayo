#:property TargetFramework=net8.0-windows
#:project ../CcxShell.csproj
using CcxShell.Core;

// The commands a pane types, for new / resume / fork. Nothing is run; the relay settings
// path is fixed so the real one in %LOCALAPPDATA%\Clayo is never rewritten from here.
var claude = new ClaudeAgent(() => @"C:\Users\O'Neil\.local\bin\claude.exe", new NoSessions(), () => @"C:\s\relay.json");
var bare = new ClaudeAgent(() => "claude", new NoSessions(), () => null);
var picksOwnId = new PicksOwnId();

int fail = 0;
void Check<T>(string name, T got, T want)
{
    var ok = EqualityComparer<T>.Default.Equals(got, want);
    if (!ok) fail++;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}  got={got} want={want}");
}

const string exe = "& 'C:\\Users\\O''Neil\\.local\\bin\\claude.exe'";
const string relay = " --settings 'C:\\s\\relay.json'";

var plan = SessionLauncher.Plan(claude, LaunchMode.New, @"C:\work");
Check("new: picks an id", Guid.TryParse(plan.ExpectedSessionId, out _), true);
Check("new: passes it to claude", plan.AgentCommand, $"{exe} --session-id {plan.ExpectedSessionId}{relay}");
Check("new: in the folder", plan.WorkingDirectory, @"C:\work");
Check("new: a fresh id each time", SessionLauncher.Plan(claude, LaunchMode.New, @"C:\work").ExpectedSessionId != plan.ExpectedSessionId, true);

plan = SessionLauncher.Plan(claude, LaunchMode.Resume, @"C:\work", "abc");
Check("resume: keyed on the existing id", plan.ExpectedSessionId, "abc");
Check("resume: command", plan.AgentCommand, $"{exe} --resume abc{relay}");

plan = SessionLauncher.Plan(claude, LaunchMode.Fork, @"C:\work", "abc");
Check("fork: a new id for the child", Guid.TryParse(plan.ExpectedSessionId, out _), true);
Check("fork: command", plan.AgentCommand, $"{exe} --resume abc --fork-session --session-id {plan.ExpectedSessionId}{relay}");

Check("no relay settings: no --settings", SessionLauncher.Plan(bare, LaunchMode.Resume, @"C:\work", "abc").AgentCommand, "& 'claude' --resume abc");

// An agent that picks its own ids (Codex): Clayo expects none, and never the parent's.
Check("own ids, new: none expected", SessionLauncher.Plan(picksOwnId, LaunchMode.New, @"C:\work").ExpectedSessionId, null);
Check("own ids, new: no id passed", picksOwnId.LastNewId, null);
Check("own ids, fork: not the parent's id", SessionLauncher.Plan(picksOwnId, LaunchMode.Fork, @"C:\work", "abc").ExpectedSessionId, null);
Check("own ids, resume: the existing id", SessionLauncher.Plan(picksOwnId, LaunchMode.Resume, @"C:\work", "abc").ExpectedSessionId, "abc");

bool threw;
try { SessionLauncher.Plan(claude, LaunchMode.Fork, @"C:\work"); threw = false; }
catch (ArgumentException) { threw = true; }
Check("fork without a parent is refused", threw, true);

Console.WriteLine(fail == 0 ? "\nall checks passed" : $"\n{fail} FAILED");
return fail;

sealed class NoSessions : ISessionSource
{
    public event Action? Changed { add { } remove { } }
    public bool RootExists => false;
    public IReadOnlyList<SessionInfo> Scan() => [];
    public void StartWatching() { }
    public void StopWatching() { }
}

sealed class PicksOwnId : IAgent
{
    public string? LastNewId = "unset";
    public AgentKind Kind => AgentKind.Codex;
    public bool KnowsIdUpfront => false;
    public ISessionSource Sessions { get; } = new NoSessions();
    public string NewCommand(string? newId) { LastNewId = newId; return "new"; }
    public string ResumeCommand(string id) => $"resume {id}";
    public string ForkCommand(string parentId, string? newId) => $"fork {parentId}";
}

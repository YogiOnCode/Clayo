#:property TargetFramework=net8.0-windows
#:project ../src/Clayo/CcxShell.csproj
using CcxShell.Core;

// A fake machine in a temp folder: the environment points into it and the runner answers
// from a table, so nothing real is run, read or installed.
var root = Path.Combine(Path.GetTempPath(), "clayo-check-agents");
if (Directory.Exists(root)) Directory.Delete(root, recursive: true);

var vars = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
var answers = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
var detector = new AgentDetector(
    name => vars.GetValueOrDefault(name),
    (exe, args) => answers.GetValueOrDefault($"{exe}|{args}"));

int fail = 0;
void Check<T>(string name, T got, T want)
{
    var ok = EqualityComparer<T>.Default.Equals(got, want);
    if (!ok) fail++;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}  got={got} want={want}");
}

string P(params string[] parts) => Path.Combine([root, .. parts]);
string Touch(string path, string text = "")
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, text);
    return path;
}
void Fresh()
{
    if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    vars.Clear();
    answers.Clear();
    vars["USERPROFILE"] = P("home");
    vars["APPDATA"] = P("home", "AppData", "Roaming");
    vars["LOCALAPPDATA"] = P("home", "AppData", "Local");
    vars["ProgramFiles"] = P("pf");
    vars["PATH"] = P("bin1") + ";" + P("bin2");
}
void Works(string exe, string version, string? status = null, string? statusArgs = null)
{
    answers[$"{exe}|--version"] = version + "\n";
    if (status is not null) answers[$"{exe}|{statusArgs}"] = status;
}

// The real outputs, as printed on 2026-10-03 by Claude Code 2.1.288 and codex-cli 0.160.0.
const string claudeIn = """
    {
      "loggedIn": true,
      "authMethod": "claude.ai",
      "apiProvider": "firstParty"
    }
    """;
const string claudeOut = """{ "loggedIn": false }""";

try
{
    // Nothing installed.
    Fresh();
    var none = detector.Detect(AgentKind.Claude);
    Check("nothing installed: no exe", none.Exe, null);
    Check("nothing installed: not signed in", none.SignedIn, false);
    Check("nothing installed: no sessions", none.Sessions, 0);
    Check("default data folder is ~\\.claude", none.DataFolder, P("home", ".claude"));

    // Known place found when PATH has nothing (an install a moment ago, PATH not yet updated).
    Fresh();
    var native = Touch(P("home", ".local", "bin", "claude.exe"));
    Works(native, "2.1.288 (Claude Code)", claudeIn, "auth status");
    var c = detector.Detect(AgentKind.Claude);
    Check("known place found without PATH", c.Exe, native);
    Check("version is the first line", c.Version, "2.1.288 (Claude Code)");
    Check("claude signed in from JSON", c.SignedIn, true);

    // PATH beats the known place; a .cmd counts.
    var cmd = Touch(P("bin2", "claude.cmd"));
    Works(cmd, "2.1.288 (Claude Code)", claudeOut, "auth status");
    c = detector.Detect(AgentKind.Claude);
    Check("PATH beats a known place", c.Exe, cmd);
    Check("claude signed out from JSON", c.SignedIn, false);

    // A saved path beats PATH.
    var saved = Touch(P("custom", "claude.exe"));
    Works(saved, "2.1.200 (Claude Code)");
    Check("saved path beats PATH", detector.Detect(AgentKind.Claude, saved).Exe, saved);
    Check("a saved path that's gone falls back to PATH", detector.Detect(AgentKind.Claude, P("gone", "claude.exe")).Exe, cmd);

    // Found but doesn't run (or timed out: the runner returns null for both).
    answers.Remove($"{cmd}|--version");
    c = detector.Detect(AgentKind.Claude);
    Check("found but --version fails: not usable", c.Exe, null);
    Check("found but --version fails: no version", c.Version, null);

    // Codex through npm, signed in on stderr, data under CODEX_HOME, Node found.
    Fresh();
    var codex = Touch(P("home", "AppData", "Roaming", "npm", "codex.cmd"));
    Works(codex, "codex-cli 0.160.0", "Logged in using ChatGPT\r\n", "login status");
    var npm = Touch(P("pf", "nodejs", "npm.cmd"));
    vars["CODEX_HOME"] = P("codexhome");
    string Meta(string cwd) =>
        """{"timestamp":"2026-10-02T17:44:00Z","type":"session_meta","payload":{"id":"x","cwd":"""
        + "\"" + cwd.Replace(@"\", @"\\") + "\"}}\n";
    Touch(P("codexhome", "sessions", "2026", "10", "01", "rollout-a.jsonl"), Meta(@"C:\work\api"));
    Touch(P("codexhome", "sessions", "2026", "10", "02", "rollout-b.jsonl"), Meta(@"C:\Work\API\"));
    Touch(P("codexhome", "sessions", "2026", "10", "02", "rollout-c.jsonl"), Meta(@"C:\work\web"));
    Touch(P("codexhome", "sessions", "2026", "10", "02", "rollout-d.jsonl"), "not json\n");
    Touch(P("codexhome", "sessions", "2026", "10", "02", "other.jsonl"), Meta(@"C:\elsewhere"));
    var x = detector.Detect(AgentKind.Codex);
    Check("codex in %APPDATA%\\npm", x.Exe, codex);
    Check("codex version", x.Version, "codex-cli 0.160.0");
    Check("codex signed in", x.SignedIn, true);
    Check("CODEX_HOME is the data folder", x.DataFolder, P("codexhome"));
    Check("codex sessions: rollout files only", x.Sessions, 4);
    Check("codex projects: same folder in any case or trailing slash counts once", x.Projects, 2);
    Check("npm found in Program Files", x.Npm, npm);
    Check("codex signed out", AgentDetector.CodexSignedIn("Not logged in\n"), false);

    // Claude counts and CLAUDE_CONFIG_DIR.
    Fresh();
    vars["CLAUDE_CONFIG_DIR"] = P("cfg");
    Touch(P("cfg", "projects", "C--work-api", "1.jsonl"));
    Touch(P("cfg", "projects", "C--work-api", "2.jsonl"));
    Touch(P("cfg", "projects", "C--work-web", "3.jsonl"));
    Directory.CreateDirectory(P("cfg", "projects", "C--empty"));
    c = detector.Detect(AgentKind.Claude);
    Check("CLAUDE_CONFIG_DIR is the data folder", c.DataFolder, P("cfg"));
    Check("claude sessions", c.Sessions, 3);
    Check("claude projects: empty folders don't count", c.Projects, 2);
    Check("no npm for claude", c.Npm, null);

    // Odd outputs.
    Check("claude status with a line before the JSON", AgentDetector.ClaudeSignedIn("note\n" + claudeIn), true);
    Check("claude status not JSON", AgentDetector.ClaudeSignedIn("oops"), false);
    Check("no output at all", AgentDetector.ClaudeSignedIn(null), false);

    // The real runner: output, a failing exit, a timeout.
    Check("runner returns output", AgentDetector.Run("cmd.exe", "/c echo hi")?.Trim(), "hi");
    Check("runner: non-zero exit is null", AgentDetector.Run("cmd.exe", "/c exit 3"), null);
    var started = DateTime.UtcNow;
    Check("runner: too slow is null", AgentDetector.Run("cmd.exe", "/c ping -n 6 127.0.0.1 >nul", 500), null);
    Check("runner: gives up on time", (DateTime.UtcNow - started).TotalSeconds < 3, true);
    Check("runner: missing exe is null", AgentDetector.Run(P("nope.exe"), ""), null);
}
finally
{
    if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
}

Console.WriteLine(fail == 0 ? "\nall checks passed" : $"\n{fail} FAILED");
return fail;

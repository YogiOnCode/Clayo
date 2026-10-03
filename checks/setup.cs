#:property TargetFramework=net8.0-windows
#:project ../CcxShell.csproj
using CcxShell.Core;

// The setup window's states (design/setup/setup-prototype.html) from made-up detector results.
int fail = 0;
void Check<T>(string name, T got, T want)
{
    var ok = EqualityComparer<T>.Default.Equals(got, want);
    if (!ok) fail++;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}  got={got} want={want}");
}

var claudeReady = new AgentInfo(AgentKind.Claude, @"C:\c.exe", "2.1.287 (Claude Code)", true, "", 42, 7, null);
var codexReady = new AgentInfo(AgentKind.Codex, @"C:\x.cmd", "codex-cli 0.160.0", true, "", 9, 3, @"C:\npm.cmd");
var claudeMissing = new AgentInfo(AgentKind.Claude, null, null, false, "", 0, 0, null);
var codexMissing = codexReady with { Exe = null, Version = null, SignedIn = false, Sessions = 0, Projects = 0 };
var codexNoNode = codexMissing with { Npm = null };
var codexSignIn = codexReady with { SignedIn = false };
var claudeSignIn = claudeReady with { SignedIn = false };

// #checking
var v = SetupScreen.Checking();
Check("checking: title", v.Title, "Setting up Clayo");
Check("checking: both looking", (v.Claude.State, v.Codex.State), (AgentRowState.Looking, AgentRowState.Looking));
Check("checking: can't open", v.CanOpen, false);
Check("checking: options greyed", v.OptionsEnabled, false);

// #both
v = SetupScreen.From(claudeReady, codexReady, true, true);
Check("both: title", v.Title, "Clayo is ready");
Check("both: intro", v.Intro, "Found both agents. Their sessions share one sidebar.");
Check("both: claude meta", v.Claude.Meta, "v2.1.287 · 42 sessions in 7 projects");
Check("both: codex meta", v.Codex.Meta, "v0.160.0 · 9 sessions in 3 projects");
Check("both: picker shows", v.ShowPicker, true);
Check("both: can open", v.CanOpen, true);
Check("both: note", v.Note, "Change any of this later in Settings.");
Check("both unticked: can't open", SetupScreen.From(claudeReady, codexReady, false, false).CanOpen, false);
Check("one unticked: still opens", SetupScreen.From(claudeReady, codexReady, false, true).CanOpen, true);

// #claude, and its mirror
v = SetupScreen.From(claudeReady, codexMissing, true, true);
Check("claude only: intro", v.Intro, "Claude Code is all Clayo needs. Codex is optional.");
Check("claude only: codex optional", v.Codex.Meta, "Optional. Needs Node.js.");
Check("claude only: no picker", v.ShowPicker, false);
Check("claude only: note", v.Note, "Add Codex any time from Settings.");
Check("claude only, no node", SetupScreen.From(claudeReady, codexNoNode, true, true).Codex.Meta, "Optional. Node.js not found.");
Check("claude only, claude unticked: can't open", SetupScreen.From(claudeReady, codexMissing, false, true).CanOpen, false);
v = SetupScreen.From(claudeMissing, codexReady, true, true);
Check("codex only: intro", v.Intro, "Codex is all Clayo needs. Claude Code is optional.");
Check("codex only: claude optional", v.Claude.Meta, "Optional.");
Check("codex only: can open", v.CanOpen, true);

// #signin
v = SetupScreen.From(claudeMissing, codexSignIn, true, true);
Check("signin: title", v.Title, "Almost there");
Check("signin: intro", v.Intro, "Codex is installed but not signed in.");
Check("signin: state", v.Codex.State, AgentRowState.SignIn);
Check("signin: meta", v.Codex.Meta, "v0.160.0 · Sign in once, then Clayo can use it");
Check("signin: can't open", v.CanOpen, false);
Check("signin: note", v.Note, "Sign in, then Check again.");
Check("both signed out", SetupScreen.From(claudeSignIn, codexSignIn, true, true).Intro, "Both agents are installed but not signed in.");
v = SetupScreen.From(claudeReady, codexSignIn, true, true);
Check("one ready, one signed out: opens", v.CanOpen, true);
Check("one ready, one signed out: says so", v.Intro, "Codex is installed but not signed in. Clayo can start without it.");

// #none
v = SetupScreen.From(claudeMissing, codexNoNode, true, true);
Check("none: title", v.Title, "Install one agent to start");
Check("none: claude meta", v.Claude.Meta, "Anthropic");
Check("none: codex meta", v.Codex.Meta, "OpenAI · Node.js not found");
Check("none: with node", SetupScreen.From(claudeMissing, codexMissing, true, true).Codex.Meta, "OpenAI · needs Node.js");
Check("none: can't open", v.CanOpen, false);
Check("none: options greyed", v.OptionsEnabled, false);

// #installing and signing in: only that row changes, nothing can be done meanwhile.
v = SetupScreen.Installing(SetupScreen.From(claudeMissing, codexMissing, true, true), AgentKind.Claude);
Check("installing: title", v.Title, "Installing Claude Code");
Check("installing: claude row", v.Claude, new AgentRowView(AgentKind.Claude, AgentRowState.Installing, "Running the official installer…"));
Check("installing: codex row as it was", v.Codex.State, AgentRowState.Missing);
Check("installing: can't open", v.CanOpen, false);
Check("installing: options greyed", v.OptionsEnabled, false);
v = SetupScreen.Installing(SetupScreen.From(claudeReady, codexMissing, true, true), AgentKind.Codex);
Check("installing codex beside a ready claude: still can't open meanwhile", v.CanOpen, false);
Check("installing codex: the npm line", v.Codex.Meta, "Running npm install -g @openai/codex…");
v = SetupScreen.SigningIn(SetupScreen.From(claudeMissing, codexSignIn, true, true), AgentKind.Codex);
Check("signing in: title", v.Title, "Signing in to Codex");
Check("signing in: state", v.Codex.State, AgentRowState.SigningIn);

// What Install and Sign in run.
Check("install claude", AgentHelper.InstallCommand(claudeMissing), "irm https://claude.ai/install.ps1 | iex");
Check("install codex through its npm", AgentHelper.InstallCommand(codexMissing with { Npm = @"C:\Program Files\nodejs\npm.cmd" }),
    @"& 'C:\Program Files\nodejs\npm.cmd' install -g @openai/codex");
Check("no npm, no codex install", AgentHelper.InstallCommand(codexNoNode), null);
Check("sign in to claude", AgentHelper.SignInCommand(claudeSignIn), @"& 'C:\c.exe' auth login");
Check("sign in to codex", AgentHelper.SignInCommand(codexSignIn), @"& 'C:\x.cmd' login");
Check("nothing to sign in to", AgentHelper.SignInCommand(claudeMissing), null);
var window = AgentHelper.Window("x");
Check("a visible PowerShell window", (window.FileName, window.UseShellExecute, window.CreateNoWindow), ("powershell.exe", false, false));
Check("that waits before closing", window.ArgumentList[^1], "x; Read-Host 'Done. Press Enter to close'");

// Small things
Check("one session, one project", SetupScreen.From(claudeReady with { Sessions = 1, Projects = 1 }, codexMissing, true, true).Claude.Meta, "v2.1.287 · 1 session in 1 project");
Check("no sessions yet", SetupScreen.From(claudeReady with { Sessions = 0, Projects = 0 }, codexMissing, true, true).Claude.Meta, "v2.1.287 · No sessions yet");
Check("a version with no number stays as it is", SetupScreen.ShortVersion("dev build"), "dev build");

Console.WriteLine(fail == 0 ? "\nall checks passed" : $"\n{fail} FAILED");
return fail;

using System.Text.RegularExpressions;

namespace CcxShell.Core;

public enum AgentRowState { Looking, Ready, SignIn, Missing, Installing, SigningIn }

/// <summary>One agent's row: its pill and the grey line under its name.</summary>
public sealed record AgentRowView(AgentKind Kind, AgentRowState State, string Meta);

/// <summary>Everything the setup window shows that depends on what was found.</summary>
public sealed record SetupView(
    string Title,
    string Intro,
    AgentRowView Claude,
    AgentRowView Codex,
    bool ShowPicker,
    bool OptionsEnabled,
    string Note,
    bool CanOpen);

/// <summary>
/// The setup window's words and states (design/setup/setup-prototype.html), from what
/// AgentDetector found and the "Use" ticks. No WPF, so checks/setup.cs can drive it.
/// </summary>
public static partial class SetupScreen
{
    public static string Name(AgentKind kind) => kind == AgentKind.Claude ? "Claude Code" : "Codex";

    /// <summary>#checking: while the detector runs.</summary>
    public static SetupView Checking() => new(
        "Setting up Clayo",
        "Looking for Claude Code and Codex on this PC…",
        new(AgentKind.Claude, AgentRowState.Looking, "Checking PATH and usual install folders"),
        new(AgentKind.Codex, AgentRowState.Looking, "Checking PATH and usual install folders"),
        ShowPicker: false, OptionsEnabled: false, "Takes about a second.", CanOpen: false);

    /// <summary>
    /// #installing: Install's PowerShell window is open. Nothing else can be done meanwhile;
    /// the other row stays as it was.
    /// </summary>
    public static SetupView Installing(SetupView found, AgentKind kind) => Waiting(found, kind, new(kind, AgentRowState.Installing,
            kind == AgentKind.Claude ? "Running the official installer…" : "Running npm install -g @openai/codex…"))
        with
        {
            Title = $"Installing {Name(kind)}",
            Intro = "Follow along in the PowerShell window. Clayo checks again when it closes.",
            Note = "Then sign in once, and Clayo is ready."
        };

    /// <summary>The same while Sign in's window is open.</summary>
    public static SetupView SigningIn(SetupView found, AgentKind kind) =>
        Waiting(found, kind, new(kind, AgentRowState.SigningIn, "Finish in the PowerShell window"))
        with
        {
            Title = $"Signing in to {Name(kind)}",
            Intro = "Follow along in the PowerShell window. Clayo checks again when it closes.",
            Note = "Your browser may open to sign in."
        };

    private static SetupView Waiting(SetupView found, AgentKind kind, AgentRowView row) => found with
    {
        Claude = kind == AgentKind.Claude ? row : found.Claude,
        Codex = kind == AgentKind.Codex ? row : found.Codex,
        ShowPicker = false,
        OptionsEnabled = false,
        CanOpen = false
    };

    public static AgentRowState StateOf(AgentInfo a) =>
        a.Exe is null ? AgentRowState.Missing : a.SignedIn ? AgentRowState.Ready : AgentRowState.SignIn;

    public static SetupView From(AgentInfo claude, AgentInfo codex, bool useClaude, bool useCodex)
    {
        var cs = StateOf(claude);
        var xs = StateOf(codex);
        bool anyReady = cs == AgentRowState.Ready || xs == AgentRowState.Ready;
        bool canOpen = (cs == AgentRowState.Ready && useClaude) || (xs == AgentRowState.Ready && useCodex);

        string title, intro, note;
        if (cs == AgentRowState.Ready && xs == AgentRowState.Ready)
            (title, intro, note) = ("Clayo is ready", "Found both agents. Their sessions share one sidebar.", "Change any of this later in Settings.");
        else if (anyReady)
        {
            var (ready, other, otherState) = cs == AgentRowState.Ready ? (claude, codex, xs) : (codex, claude, cs);
            (title, intro, note) = otherState == AgentRowState.SignIn
                ? ("Clayo is ready", $"{Name(other.Kind)} is installed but not signed in. Clayo can start without it.", $"Sign in to {Name(other.Kind)} any time, then Check again.")
                : ("Clayo is ready", $"{Name(ready.Kind)} is all Clayo needs. {Name(other.Kind)} is optional.", $"Add {Name(other.Kind)} any time from Settings.");
        }
        else if (cs == AgentRowState.SignIn || xs == AgentRowState.SignIn)
            (title, intro, note) = ("Almost there",
                cs == xs ? "Both agents are installed but not signed in."
                         : $"{Name(cs == AgentRowState.SignIn ? AgentKind.Claude : AgentKind.Codex)} is installed but not signed in.",
                "Sign in, then Check again.");
        else
            (title, intro, note) = ("Install one agent to start", "Clayo runs your coding agent in its panes. Either one works.",
                "Install installs it for you, in a window you can watch.");

        return new SetupView(title, intro,
            Row(claude, cs, anyReady), Row(codex, xs, anyReady),
            ShowPicker: cs == AgentRowState.Ready && xs == AgentRowState.Ready,
            OptionsEnabled: canOpen, note, canOpen);
    }

    private static AgentRowView Row(AgentInfo a, AgentRowState state, bool anyReady) => new(a.Kind, state, state switch
    {
        AgentRowState.Ready => $"{ShortVersion(a.Version)} · {Count(a.Sessions, a.Projects)}",
        AgentRowState.SignIn => $"{ShortVersion(a.Version)} · Sign in once, then Clayo can use it",
        // Missing: optional when the other one works, else who makes it. Codex installs through npm.
        _ when a.Kind == AgentKind.Claude => anyReady ? "Optional." : "Anthropic",
        _ when anyReady => a.Npm is null ? "Optional. Node.js not found." : "Optional. Needs Node.js.",
        _ => a.Npm is null ? "OpenAI · Node.js not found" : "OpenAI · needs Node.js",
    });

    /// <summary>"2.1.288 (Claude Code)" and "codex-cli 0.160.0" both become "v2.1.288" style.</summary>
    public static string ShortVersion(string? version) =>
        version is not null && VersionNumber().Match(version) is { Success: true } m ? $"v{m.Value}" : version ?? "";

    private static string Count(int sessions, int projects) => sessions == 0
        ? "No sessions yet"
        : $"{sessions} {(sessions == 1 ? "session" : "sessions")} in {projects} {(projects == 1 ? "project" : "projects")}";

    [GeneratedRegex(@"\d+(\.\d+)+")]
    private static partial Regex VersionNumber();
}

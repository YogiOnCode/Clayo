using System.IO;

namespace CcxShell.Core;

public enum LaunchMode
{
    /// <summary>Fresh session in the folder.</summary>
    New,

    /// <summary>Continue an existing session. Never open the same id twice — transcripts interleave.</summary>
    Resume,

    /// <summary>Copy the conversation and diverge. The original is untouched.</summary>
    Fork
}

public sealed record LaunchPlan(
    string ShellCommandLine,
    string WorkingDirectory,
    string AgentCommand,
    string? ExpectedSessionId,
    AgentKind Agent);

public static class SessionLauncher
{
    /// <summary>
    /// We spawn a shell and type the claude command into it, rather than spawning claude
    /// directly. When claude exits you land on a prompt instead of the pane dying.
    /// </summary>
    private static string ResolveShell()
    {
        return FindOnPath("pwsh.exe") is { } pwsh
            ? $"\"{pwsh}\" -NoLogo"
            : "powershell.exe -NoLogo";
    }

    /// <summary>
    /// Claude Code marks its child processes with session-scoped variables. Panes inherit
    /// our environment (ConPty passes a null environment block), so if clayo was itself
    /// started from inside a Claude Code session those markers reach the claude we spawn.
    /// CLAUDE_CODE_CHILD_SESSION alone switches transcript saving off, which leaves the new
    /// session invisible to both the sidebar and --resume.
    ///
    /// ponytail: clearing them in our own process is the whole fix. Windows hands a child a
    /// copy of the current block, so there is no environment block to marshal. Configuration
    /// variables (ANTHROPIC_*, CLAUDE_EFFORT) are deliberately left alone.
    /// </summary>
    public static void ScrubInheritedSession()
    {
        foreach (var name in new[]
                 {
                     "CLAUDECODE",
                     "CLAUDE_CODE_CHILD_SESSION",
                     "CLAUDE_CODE_ENTRYPOINT",
                     "CLAUDE_CODE_SESSION_ID",
                     "CLAUDE_CODE_MESSAGING_SOCKET",
                     "CLAUDE_CODE_MESSAGING_TOKEN",
                     "CLAUDE_PID"
                 })
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    public static LaunchPlan Plan(IAgent agent, LaunchMode mode, string workingDirectory, string? sessionId = null)
    {
        if (mode != LaunchMode.New && string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException($"{mode} needs a session id.", nameof(sessionId));

        // Allocate the id up front when the agent lets us, rather than letting it pick one.
        // Without it a brand-new pane has nothing to key on until its transcript appears on
        // disk, so it cannot be renamed, tracked, or told apart from another new pane. For a
        // fork it is the whole trick: we can draw the branch in the sidebar immediately
        // instead of scraping stdout for the new id.
        var newId = agent.KnowsIdUpfront && mode != LaunchMode.Resume ? Guid.NewGuid().ToString() : null;

        var command = mode switch
        {
            LaunchMode.New => agent.NewCommand(newId),
            LaunchMode.Resume => agent.ResumeCommand(sessionId!),
            LaunchMode.Fork => agent.ForkCommand(sessionId!, newId),
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };

        // Resume allocates nothing — the id already exists, and it is the one the pane
        // has to be keyed on, or the sidebar row for it never learns it is open.
        return new LaunchPlan(ResolveShell(), workingDirectory, command,
            mode == LaunchMode.Resume ? sessionId : newId, agent.Kind);
    }

    /// <summary>The first PATH folder holding exe. AgentDetector passes its own PATH.</summary>
    internal static string? FindOnPath(string exe, string? path = null)
    {
        path ??= Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { /* malformed PATH entry */ }
        }

        return null;
    }
}

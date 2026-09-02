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
    string ClaudeCommand,
    string? ExpectedSessionId);

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

    public static LaunchPlan Plan(LaunchMode mode, string workingDirectory, string? sessionId = null)
    {
        string command;
        string? expectedId = null;

        switch (mode)
        {
            case LaunchMode.New:
                // Allocate the id up front rather than letting claude pick one. Without it a
                // brand-new pane has nothing to key on until its transcript appears on disk,
                // so it cannot be renamed, tracked, or told apart from another new pane.
                expectedId = Guid.NewGuid().ToString();
                command = $"claude --session-id {expectedId}";
                break;

            case LaunchMode.Resume:
                if (string.IsNullOrWhiteSpace(sessionId))
                    throw new ArgumentException("Resume needs a session id.", nameof(sessionId));
                command = $"claude --resume {sessionId}";
                break;

            case LaunchMode.Fork:
                if (string.IsNullOrWhiteSpace(sessionId))
                    throw new ArgumentException("Fork needs a parent session id.", nameof(sessionId));
                // Pre-allocating the child id is the whole trick: we can draw the branch in
                // the sidebar immediately instead of scraping stdout for the new id.
                expectedId = Guid.NewGuid().ToString();
                command = $"claude --resume {sessionId} --fork-session --session-id {expectedId}";
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }

        // Resume allocates nothing — the id already exists, and it is the one the pane
        // has to be keyed on, or the sidebar row for it never learns it is open.
        return new LaunchPlan(ResolveShell(), workingDirectory, command, expectedId ?? sessionId);
    }

    private static string? FindOnPath(string exe)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
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

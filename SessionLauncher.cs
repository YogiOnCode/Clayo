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
    public static string ResolveShell()
    {
        return FindOnPath("pwsh.exe") is { } pwsh
            ? $"\"{pwsh}\" -NoLogo"
            : "powershell.exe -NoLogo";
    }

    public static LaunchPlan Plan(LaunchMode mode, string workingDirectory, string? sessionId = null)
    {
        string command;
        string? expectedId = null;

        switch (mode)
        {
            case LaunchMode.New:
                command = "claude";
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

        return new LaunchPlan(ResolveShell(), workingDirectory, command, expectedId);
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

using System.Diagnostics;

namespace CcxShell.Core;

/// <summary>
/// What the setup window's Install and Sign in run (docs/SETUP.md D2): the agent's own
/// installer or login, in a PowerShell window the user can watch and answer. Never elevated,
/// never hidden. The window waits for Enter, so an error stays readable.
/// </summary>
public static class AgentHelper
{
    /// <summary>Null when it can't be installed from here: Codex without npm.</summary>
    public static string? InstallCommand(AgentInfo agent) => agent.Kind == AgentKind.Claude
        ? "irm https://claude.ai/install.ps1 | iex"
        : agent.Npm is { } npm ? $"& {Quote(npm)} install -g @openai/codex" : null;

    /// <summary>Null when the agent wasn't found.</summary>
    public static string? SignInCommand(AgentInfo agent) => agent.Exe is not { } exe ? null
        : agent.Kind == AgentKind.Claude ? $"& {Quote(exe)} auth login"
        : $"& {Quote(exe)} login";

    /// <summary>
    /// powershell.exe, which every Windows has, in its own console. ArgumentList quotes for us,
    /// so the command reaches -Command exactly as written.
    /// </summary>
    public static ProcessStartInfo Window(string command) => new("powershell.exe")
    {
        UseShellExecute = false,
        CreateNoWindow = false,
        ArgumentList =
        {
            "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command",
            $"{command}; Read-Host 'Done. Press Enter to close'"
        }
    };

    /// <summary>Runs it and returns when the window is closed.</summary>
    public static async Task RunAsync(string command)
    {
        using var process = Process.Start(Window(command));
        if (process is not null) await process.WaitForExitAsync();
    }

    /// <summary>A PowerShell single-quoted string.</summary>
    public static string Quote(string s) => $"'{s.Replace("'", "''")}'";
}

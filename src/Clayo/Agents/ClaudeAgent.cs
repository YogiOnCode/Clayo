namespace CcxShell.Core;

/// <summary>
/// Claude Code. exe is asked on every launch, so an install or a Locate… made while Clayo runs
/// is the one used. relaySettings gives the --settings file for Clayo's status line relay
/// (StatusRelay), or null for none; it is asked on every launch because it follows the
/// user's own status line.
/// </summary>
public sealed class ClaudeAgent(Func<string> exe, ISessionSource sessions, Func<string?> relaySettings) : IAgent
{
    /// <summary>
    /// The full path, so a PATH that differs between Explorer and the shell can't break a
    /// pane. Only located here, not run: setup is what checks that it works.
    /// </summary>
    public static ClaudeAgent ForThisUser(Func<string?> savedPath) => new(
        () => AgentDetector.ForThisMachine().Locate(AgentKind.Claude, savedPath()) ?? "claude",
        new SessionStore(),
        () => Environment.ProcessPath is { } self ? StatusRelay.WriteSettings(self) : null);

    public AgentKind Kind => AgentKind.Claude;

    public bool KnowsIdUpfront => true;

    public ISessionSource Sessions => sessions;

    public string NewCommand(string? newId) => Command($"--session-id {Id(newId)}");

    public string ResumeCommand(string id) => Command($"--resume {Id(id)}");

    public string ForkCommand(string parentId, string? newId) =>
        Command($"--resume {Id(parentId)} --fork-session --session-id {Id(newId)}");

    // An id is read from transcript contents, so it is quoted like any other text.
    private static string Id(string? id) => AgentHelper.Quote(id ?? "");

    /// <summary>Single quotes throughout, because the shell is PowerShell.</summary>
    private string Command(string args)
    {
        var command = $"& {AgentHelper.Quote(exe())} {args}";

        // Clayo's status line relay, for this pane only. The user's own settings.json is
        // never written.
        if (relaySettings() is { } settings) command += $" --settings {AgentHelper.Quote(settings)}";
        return command;
    }
}

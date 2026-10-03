namespace CcxShell.Core;

/// <summary>
/// What differs between Claude Code and Codex. The commands are typed into the pane's
/// PowerShell (see SessionLauncher), so they are PowerShell command lines.
/// </summary>
public interface IAgent
{
    AgentKind Kind { get; }

    /// <summary>
    /// Claude takes the id we pick, so a new pane's row exists before the first message.
    /// Codex picks its own; Clayo finds it afterwards (docs/SETUP.md, "Finding a new Codex
    /// session"). When false, the commands get a null newId.
    /// </summary>
    bool KnowsIdUpfront { get; }

    string NewCommand(string? newId);

    string ResumeCommand(string id);

    string ForkCommand(string parentId, string? newId);

    ISessionSource Sessions { get; }
}

/// <summary>An agent's transcripts, read only, as rows for the sidebar.</summary>
public interface ISessionSource
{
    /// <summary>Fired on a background thread after the transcripts settle.</summary>
    event Action? Changed;

    bool RootExists { get; }

    IReadOnlyList<SessionInfo> Scan();

    void StartWatching();

    void StopWatching();
}

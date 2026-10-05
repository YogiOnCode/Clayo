namespace CcxShell.Core;

/// <summary>
/// Codex. It picks its own session ids, so a new pane or a fork is bound to its transcript
/// once that appears (CodexSessionStore.FindStarted). exe is asked on every launch, as for
/// ClaudeAgent. No -C: the pane's shell already starts in the folder.
/// </summary>
public sealed class CodexAgent(Func<string> exe, ISessionSource sessions) : IAgent
{
    public static CodexAgent ForThisUser(Func<string?> savedPath) => new(
        () => AgentDetector.ForThisMachine().Locate(AgentKind.Codex, savedPath()) ?? "codex",
        CodexSessionStore.ForThisUser());

    public AgentKind Kind => AgentKind.Codex;

    public bool KnowsIdUpfront => false;

    public ISessionSource Sessions => sessions;

    public string NewCommand(string? newId) => Exe;

    // An id is read from transcript contents, so it is quoted like any other text.
    public string ResumeCommand(string id) => $"{Exe} resume {AgentHelper.Quote(id)}";

    public string ForkCommand(string parentId, string? newId) => $"{Exe} fork {AgentHelper.Quote(parentId)}";

    private string Exe => $"& {AgentHelper.Quote(exe())}";
}

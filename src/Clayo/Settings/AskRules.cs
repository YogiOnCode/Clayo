using System.IO;

namespace CcxShell.Core;

/// <summary>
/// One row of Settings › Security (docs/PERMISSIONS.md, "The list"): what Claude is given as
/// ask rules, and the command prefixes Codex is given as prompt rules.
/// </summary>
public sealed record AskRule(string Id, string Label, string Line, string[] Claude, string[][] Codex);

/// <summary>
/// One kind of action, a card on Settings › Security (docs/SECURITY.md): its rules under a
/// switch of its own. Icon is the card's Segoe Fluent glyph.
/// </summary>
public sealed record AskGroup(string Id, string Title, string Line, string Icon, AskRule[] Rules);

/// <summary>
/// Ask before publishing: the rules Clayo hands its sessions so they stop and ask before they
/// push, tag, release, merge or change the repo on GitHub. Clayo's own files only (D1).
/// </summary>
public static class AskRules
{
    public static readonly AskGroup[] Groups =
    [
        new("publishing", "Publishing", "Pushing, tags, releases, anything that changes GitHub.", "",
        [
            new("push", "Push code", "git push",
                ["git push*", "git -C * push*"], [["git", "push"]]),
            new("tags", "Tags", "git tag: making or moving a version tag",
                ["git tag*", "git -C * tag*"], [["git", "tag"]]),
            new("releases", "Releases", "gh release: drafting or publishing one",
                ["gh release*"], [["gh", "release"]]),
            new("merge", "Merge pull requests", "gh pr merge",
                ["gh pr merge*"], [["gh", "pr", "merge"]]),
            new("repo", "Repo settings", "Renaming, deleting, rulesets",
                ["gh repo edit*", "gh repo delete*", "gh repo rename*", "gh ruleset*"],
                [["gh", "repo", "edit"], ["gh", "repo", "delete"], ["gh", "repo", "rename"], ["gh", "ruleset"]]),
            new("secrets", "Secrets", "gh secret",
                ["gh secret*"], [["gh", "secret"]]),
            new("workflows", "Workflows", "gh workflow: running or turning one on or off",
                ["gh workflow*"], [["gh", "workflow"]]),
            // Codex matches prefixes only, so it can't tell a gh api read from a write (D6).
            new("api", "Other GitHub changes", "gh api calls that write",
                ["gh api *-X*", "gh api *--method*", "gh api *-f *", "gh api *-F *", "gh api *--field*",
                 "gh api *--raw-field*", "gh api *--input*"],
                [["gh", "api"]]),
        ]),
    ];

    public static readonly AskRule[] All = Groups.SelectMany(g => g.Rules).ToArray();

    /// <summary>The rules of every group that's on, minus the ones you unticked.</summary>
    public static IEnumerable<AskRule> Ticked(ClayoSettings s) =>
        Groups.Where(g => !s.IsGroupOff(g.Id)).SelectMany(g => g.Rules).Where(r => !s.IsAskOff(r.Id));

    /// <summary>Claude's permissions.ask: each pattern for Bash, and for PowerShell in panes that run it.</summary>
    public static List<string> ClaudeAsk(ClayoSettings s) =>
        Ticked(s).SelectMany(r => r.Claude).SelectMany(p => new[] { $"Bash({p})", $"PowerShell({p})" }).ToList();

    public const string CodexFileName = "clayo.rules";

    /// <summary>
    /// What clayo.rules holds, or null when it should not exist: Codex is off for this, the
    /// switch is off, or every rule is unticked (D9).
    /// </summary>
    public static string? CodexRules(ClayoSettings s)
    {
        if (!s.AlsoForCodex) return null;
        var lines = Ticked(s).SelectMany(r => r.Codex)
            .Select(p => $"prefix_rule(pattern=[{string.Join(", ", p.Select(w => $"\"{w}\""))}], decision=\"prompt\")")
            .ToList();
        if (lines.Count == 0) return null;
        // ASCII only: Windows PowerShell shows the file in the ANSI code page.
        return "# Written by Clayo: Ask before publishing. Turn it off in Clayo, Settings > Security.\n"
             + "# Clayo rewrites this file when those settings change, and deletes it when they're off.\n"
             + string.Join("\n", lines) + "\n";
    }

    public static string CodexHome =>
        Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } home
            ? home
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    /// <summary>
    /// Makes <c>codexHome\rules\clayo.rules</c> match the settings: written, rewritten or deleted.
    /// No other file there is touched, and a Codex home that doesn't exist is not created.
    /// Best effort: a file Codex can't be given only means its sessions don't ask.
    /// </summary>
    public static void SyncCodex(ClayoSettings s, string codexHome)
    {
        var path = Path.Combine(codexHome, "rules", CodexFileName);
        try
        {
            if (CodexRules(s) is not { } text)
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            if (!Directory.Exists(codexHome)) return;
            if (File.Exists(path) && File.ReadAllText(path) == text) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

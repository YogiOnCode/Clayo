using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CcxShell.Core;

/// <summary>One of the user's own rules: allow, ask or deny for Claude; allow, prompt or forbidden for Codex.</summary>
public sealed record OwnRule(string Decision, string Rule, string Note = "");

/// <summary>An agent's own rules, as Settings › Security lists them. File is what Open file opens; Problem replaces the list.</summary>
public sealed record OwnRules(string? File, IReadOnlyList<OwnRule> Rules, string? Problem = null);

/// <summary>
/// The rules already in the agents' own settings (docs/PERMISSIONS.md, D7, D8). Only ever read,
/// never the credential files, and a file that's missing or broken is one line, never an error.
/// </summary>
public static partial class OwnRulesReader
{
    /// <summary>permissions.allow, ask and deny from Claude's settings.json.</summary>
    public static OwnRules Claude(string settingsJson, ClayoSettings s)
    {
        if (!File.Exists(settingsJson)) return new(null, [], "No settings file yet.");
        var rules = new List<OwnRule>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(settingsJson));
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("permissions", out var p) && p.ValueKind == JsonValueKind.Object)
                foreach (var decision in new[] { "allow", "ask", "deny" })
                    if (p.TryGetProperty(decision, out var list) && list.ValueKind == JsonValueKind.Array)
                        foreach (var r in list.EnumerateArray())
                            if (r.ValueKind == JsonValueKind.String && r.GetString() is { Length: > 0 } rule)
                                rules.Add(new(decision, rule, decision == "allow" ? ClaudeNote(rule, s) : ""));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(settingsJson, [], "Couldn't read this file.");
        }
        return new(settingsJson, rules);
    }

    /// <summary>prefix_rule lines from every rules file in CODEX_HOME\rules but Clayo's own.</summary>
    public static OwnRules Codex(string codexHome, ClayoSettings s)
    {
        var dir = Path.Combine(codexHome, "rules");
        string[] files;
        try
        {
            files = Directory.Exists(dir)
                ? Directory.GetFiles(dir, "*.rules")
                    .Where(f => !Path.GetFileName(f).Equals(AskRules.CodexFileName, StringComparison.OrdinalIgnoreCase))
                    .Order(StringComparer.OrdinalIgnoreCase).ToArray()
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { files = []; }
        if (files.Length == 0) return new(null, [], "No rules files yet.");

        var rules = new List<OwnRule>();
        try
        {
            foreach (var file in files)
                foreach (Match m in PrefixRule().Matches(File.ReadAllText(file)))
                {
                    if (Pattern().Match(m.Groups[1].Value) is not { Success: true } pattern) continue;
                    // Codex's own default when a rule names no decision.
                    var decision = Decision().Match(m.Groups[1].Value) is { Success: true } d ? d.Groups[1].Value : "allow";
                    var words = Word().Matches(pattern.Groups[1].Value).Select(w => w.Groups[1].Value).ToArray();
                    var shown = "[" + string.Join(", ", words.Select(w => $"\"{w}\"")) + "]";
                    rules.Add(new(decision, shown, decision == "allow" ? CodexNote(words, s) : ""));
                }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new(files[0], [], "Couldn't read these files.");
        }
        return new(files[0], rules);
    }

    // Claude: ask wins over allow, so an allow of a ticked rule's pattern still asks (D8).
    private static string ClaudeNote(string rule, ClayoSettings s) =>
        AskRules.All.FirstOrDefault(r => r.Claude.Any(p => rule == $"Bash({p})" || rule == $"PowerShell({p})")) is { } hit
            ? Note(hit, AskRules.Ticked(s).Contains(hit))
            : "";

    // Codex: the strictest matching decision wins, so prompt beats allow, but only with Codex on.
    private static string CodexNote(string[] words, ClayoSettings s) =>
        AskRules.All.FirstOrDefault(r => r.Codex.Any(p => p.SequenceEqual(words))) is { } hit
            ? Note(hit, s.AlsoForCodex && AskRules.Ticked(s).Contains(hit))
            : "";

    private static string Note(AskRule rule, bool asks) => asks ? $"{rule.Label} still asks first" : $"{rule.Label} runs without asking";

    [GeneratedRegex(@"prefix_rule\s*\(([^)]*)\)", RegexOptions.Singleline)]
    private static partial Regex PrefixRule();

    [GeneratedRegex(@"pattern\s*=\s*\[([^\]]*)\]")]
    private static partial Regex Pattern();

    [GeneratedRegex(@"decision\s*=\s*""(\w+)""")]
    private static partial Regex Decision();

    [GeneratedRegex(@"""([^""]*)""")]
    private static partial Regex Word();
}

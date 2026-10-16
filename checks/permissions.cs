#:property TargetFramework=net10.0-windows
#:project ../src/Clayo/CcxShell.csproj
using System.Text.Json;
using System.Text.Json.Nodes;
using CcxShell.Core;

// Ask before publishing (docs/PERMISSIONS.md, P2): the rules each setting gives each agent.
// Never the real ~/.claude or ~/.codex: throwaway folders, removed at the end.
var dir = Path.Combine(Path.GetTempPath(), "clayo-check-permissions-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);

int fail = 0;
void Check<T>(string name, T got, T want)
{
    var ok = EqualityComparer<T>.Default.Equals(got, want);
    if (!ok) fail++;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}  got={got} want={want}");
}

var on = new ClayoSettings();
var off = on with { AskGroupsOff = "publishing" };
var noTags = on with { AskOff = "tags" };
var codex = on with { AlsoForCodex = true };

try
{
    // Defaults (D4): on, every rule ticked, Codex off.
    Check("every group on by default", on.AskGroupsOff, "");
    Check("nothing unticked by default", on.AskOff, "");
    Check("codex off by default", on.AlsoForCodex, false);
    Check("eight rules", AskRules.All.Length, 8);
    Check("ids are unique", AskRules.All.Select(r => r.Id).Distinct().Count(), 8);

    // Claude.
    var ask = AskRules.ClaudeAsk(on);
    Check("every pattern, for Bash and PowerShell", ask.Count, AskRules.All.Sum(r => r.Claude.Length) * 2);
    Check("push asks in Bash", ask.Contains("Bash(git push*)"), true);
    Check("push asks in PowerShell", ask.Contains("PowerShell(git push*)"), true);
    Check("push with -C asks", ask.Contains("Bash(git -C * push*)"), true);
    Check("gh api writes ask", ask.Contains("Bash(gh api *-X*)"), true);
    Check("switch off: no rules", AskRules.ClaudeAsk(off).Count, 0);

    var unticked = AskRules.ClaudeAsk(noTags);
    Check("unticking tags takes its patterns away", unticked.Any(p => p.Contains("git tag") || p.Contains("* tag*")), false);
    Check("and only its patterns", unticked.Count, ask.Count - 4);
    Check("push still asks", unticked.Contains("Bash(git push*)"), true);

    // Groups (docs/SECURITY.md, S1): one so far, Publishing, holding every rule.
    Check("one group", string.Join(",", AskRules.Groups.Select(g => g.Id)), "publishing");
    Check("All is every group's rules", AskRules.All.SequenceEqual(AskRules.Groups.SelectMany(g => g.Rules)), true);
    Check("group off keeps its ticks", (noTags with { AskGroupsOff = "publishing" }).AskOff, "tags");
    Check("group off with a rule unticked: no rules", AskRules.ClaudeAsk(noTags with { AskGroupsOff = "publishing" }).Count, 0);
    Check("back on, the unticked rule stays off", AskRules.ClaudeAsk(noTags).SequenceEqual(unticked), true);
    Check("an unknown group off is ignored", AskRules.ClaudeAsk(on with { AskGroupsOff = "nope" }).SequenceEqual(ask), true);

    // The --settings file.
    var theirs = new JsonObject { ["type"] = "command", ["command"] = "pwsh line.ps1", ["padding"] = 2 };
    var file = JsonNode.Parse(StatusRelay.SettingsJson(@"C:\Clayo\clayo.exe", theirs, ask))!;
    Check("the file still has its statusLine relay",
        file["statusLine"]?["command"]?.GetValue<string>().StartsWith("'C:/Clayo/clayo.exe' --statusline --pass"), true);
    Check("and the rest of their statusLine", file["statusLine"]?["padding"]?.GetValue<int>(), 2);
    Check("ask rules in permissions.ask", file["permissions"]?["ask"]?.AsArray().Count, ask.Count);
    Check("nothing but ask", file["permissions"]?.AsObject().Count, 1);
    Check("their statusLine object is not changed", theirs["command"]?.GetValue<string>(), "pwsh line.ps1");
    var bare = JsonNode.Parse(StatusRelay.SettingsJson(@"C:\Clayo\clayo.exe", null, AskRules.ClaudeAsk(off)))!;
    Check("switch off: no permissions key", bare["permissions"] is null, true);
    Check("switch off: statusLine still there", bare["statusLine"]?["command"]?.GetValue<string>(), "'C:/Clayo/clayo.exe' --statusline");

    // Settings file: an old one without the new keys reads as all on (D4).
    var path = Path.Combine(dir, "settings.json");
    File.WriteAllText(path, """{"statusHeader":false,"useCodex":true}""");
    var old = ClayoSettings.Load(path);
    Check("old file: every group on", old.AskGroupsOff, "");
    Check("old file: all ticked", old.AskOff, "");
    Check("old file: codex off", old.AlsoForCodex, false);

    var saved = on with { AskGroupsOff = "publishing", AskOff = "push,tags", AlsoForCodex = true };
    saved.Save(path);
    Check("saved settings load back", ClayoSettings.Load(path), saved);
    Check("askOff is an array in the file",
        JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("askOff").GetArrayLength(), 2);
    Check("askGroupsOff is an array in the file",
        JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("askGroupsOff").GetArrayLength(), 1);

    File.WriteAllText(path, """{"askOff":["tags",3,"",null,"push","tags","a,b"]}""");
    Check("odd askOff entries are skipped", ClayoSettings.Load(path).AskOff, "push,tags");
    File.WriteAllText(path, """{"askOff":"tags"}""");
    Check("askOff that is not an array is ignored", ClayoSettings.Load(path).AskOff, "");
    File.WriteAllText(path, """{"askGroupsOff":["nope",3]}""");
    Check("an unknown group in the file is ignored", AskRules.ClaudeAsk(ClayoSettings.Load(path)).SequenceEqual(ask), true);

    // Codex (D1, D9): only with Also for Codex on.
    Check("codex off: no file", AskRules.CodexRules(on), null);
    var rules = AskRules.CodexRules(codex)!;
    var lines = rules.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    Check("starts with a comment saying Clayo wrote it", lines[0].StartsWith("# Written by Clayo"), true);
    Check("a prompt rule per prefix", lines.Count(l => l.StartsWith("prefix_rule(")), AskRules.All.Sum(r => r.Codex.Length));
    Check("push as Codex reads it", lines.Contains("""prefix_rule(pattern=["git", "push"], decision="prompt")"""), true);
    Check("every gh api call (D6)", lines.Contains("""prefix_rule(pattern=["gh", "api"], decision="prompt")"""), true);
    Check("codex, switch off: no file", AskRules.CodexRules(codex with { AskGroupsOff = "publishing" }), null);
    Check("codex, tags unticked", AskRules.CodexRules(codex with { AskOff = "tags" })!.Contains("\"tag\""), false);
    Check("codex, all unticked: no file",
        AskRules.CodexRules(codex with { AskOff = string.Join(",", AskRules.All.Select(r => r.Id)) }), null);

    // The file itself, in a throwaway CODEX_HOME holding a rules file of the user's.
    var home = Path.Combine(dir, "codex");
    var clayoRules = Path.Combine(home, "rules", AskRules.CodexFileName);
    var mine = Path.Combine(home, "rules", "default.rules");
    Directory.CreateDirectory(Path.GetDirectoryName(mine)!);
    File.WriteAllText(mine, "prefix_rule(pattern=[\"git\", \"reset\"], decision=\"prompt\")\n");

    AskRules.SyncCodex(on, home);
    Check("codex off: nothing written", File.Exists(clayoRules), false);
    AskRules.SyncCodex(codex, home);
    Check("codex on: clayo.rules written", File.ReadAllText(clayoRules), rules);
    AskRules.SyncCodex(codex with { AskOff = "tags" }, home);
    Check("a tick rewrites it", File.ReadAllText(clayoRules).Contains("\"tag\""), false);
    AskRules.SyncCodex(codex with { AskGroupsOff = "publishing" }, home);
    Check("switch off deletes it", File.Exists(clayoRules), false);
    AskRules.SyncCodex(codex, home);
    AskRules.SyncCodex(on, home);
    Check("codex off deletes it", File.Exists(clayoRules), false);
    Check("the user's own rules file is untouched",
        File.ReadAllText(mine), "prefix_rule(pattern=[\"git\", \"reset\"], decision=\"prompt\")\n");
    Check("nothing else in rules", Directory.GetFiles(Path.Combine(home, "rules")).Length, 1);

    var none = Path.Combine(dir, "no-codex");
    AskRules.SyncCodex(codex, none);
    Check("a Codex home that doesn't exist is not created", Directory.Exists(none), false);

    // Your own rules (P3, D7, D8): read only, in throwaway files.
    var claudeFile = Path.Combine(dir, "claude-settings.json");
    Check("claude: a missing file is one line", OwnRulesReader.Claude(claudeFile, on).Problem, "No settings file yet.");
    File.WriteAllText(claudeFile, """{"permissions":{"allow":["Bash(npm test*)","Bash(git push*)"],"ask":["Bash(git commit*)"],"deny":["Read(./.env)"]},"model":"x"}""");
    var own = OwnRulesReader.Claude(claudeFile, on);
    Check("claude: allow, ask and deny read", string.Join(" ", own.Rules.Select(r => r.Decision)), "allow allow ask deny");
    Check("claude: the rule as written", own.Rules[3].Rule, "Read(./.env)");
    Check("claude: no note on an unrelated rule", own.Rules[0].Note, "");
    Check("claude: allow of a ticked rule still asks", own.Rules[1].Note, "Push code still asks first");
    Check("claude: unticked, it runs without asking",
        OwnRulesReader.Claude(claudeFile, noTags with { AskOff = "push" }).Rules[1].Note, "Push code runs without asking");
    Check("claude: switch off, it runs without asking", OwnRulesReader.Claude(claudeFile, off).Rules[1].Note, "Push code runs without asking");
    Check("claude: Open file is the file", own.File, claudeFile);
    File.WriteAllText(claudeFile, """{"permissions":{"allow":["Bash(""");
    Check("claude: a broken file is one line", OwnRulesReader.Claude(claudeFile, on).Problem, "Couldn't read this file.");
    File.WriteAllText(claudeFile, """{"permissions":{"allow":"Bash(x)"}}""");
    Check("claude: a list that isn't one is skipped", OwnRulesReader.Claude(claudeFile, on).Rules.Count, 0);

    var cxHome = Path.Combine(dir, "codex-own");
    Check("codex: no rules folder is one line", OwnRulesReader.Codex(cxHome, codex).Problem, "No rules files yet.");
    Directory.CreateDirectory(Path.Combine(cxHome, "rules"));
    File.WriteAllText(Path.Combine(cxHome, "rules", "default.rules"),
        "# mine\nprefix_rule(pattern=[\"git\", \"reset\"], decision=\"prompt\")\nprefix_rule(\n  pattern = [\"git\",\"push\"],\n  decision = \"allow\",\n)\nprefix_rule(pattern=[\"npm\", \"test\"])\n");
    AskRules.SyncCodex(codex, cxHome);
    var cx = OwnRulesReader.Codex(cxHome, codex);
    Check("codex: Clayo's own file is not listed", cx.Rules.Count, 3);
    Check("codex: decisions read", string.Join(" ", cx.Rules.Select(r => r.Decision)), "prompt allow allow");
    Check("codex: a rule over several lines", cx.Rules[1].Rule, """["git", "push"]""");
    Check("codex: no decision is Codex's allow", cx.Rules[2].Rule, """["npm", "test"]""");
    Check("codex: allow of a ticked rule still asks", cx.Rules[1].Note, "Push code still asks first");
    Check("codex: with Codex off it runs without asking", OwnRulesReader.Codex(cxHome, on).Rules[1].Note, "Push code runs without asking");
    Check("codex: Open file is the user's file", Path.GetFileName(cx.File), "default.rules");
}
finally
{
    Directory.Delete(dir, recursive: true);
}

Console.WriteLine(fail == 0 ? "\nall checks passed" : $"\n{fail} FAILED");
return fail;

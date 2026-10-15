#:property TargetFramework=net8.0-windows
#:project ../src/Clayo/CcxShell.csproj
using CcxShell.Core;

// The doc and image chips: which docs a transcript says were written, when a prompt was sent,
// and which pasted images are still waiting. Temp folders only.
int fail = 0;
void Check<T>(string name, T got, T want)
{
    var ok = EqualityComparer<T>.Default.Equals(got, want);
    if (!ok) fail++;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}  got={got} want={want}");
}

string Esc(string s) => $"\"{System.Text.Json.JsonEncodedText.Encode(s)}\"";
string Ts(DateTime t) => t.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
string Write(string path, DateTime at) =>
    $"{{\"type\":\"assistant\",\"timestamp\":\"{Ts(at)}\",\"message\":{{\"role\":\"assistant\",\"content\":[{{\"type\":\"tool_use\",\"name\":\"Write\",\"input\":{{\"file_path\":{Esc(path)},\"content\":\"x\"}}}}]}}}}";
string Prompt(DateTime at) =>
    $"{{\"type\":\"user\",\"timestamp\":\"{Ts(at)}\",\"message\":{{\"role\":\"user\",\"content\":\"see docs/a.md\"}}}}";
string ToolResult(DateTime at) =>
    $"{{\"type\":\"user\",\"timestamp\":\"{Ts(at)}\",\"message\":{{\"role\":\"user\",\"content\":[{{\"type\":\"tool_result\",\"content\":\"ok\"}}]}}}}";
string Patch(string path, DateTime at) =>
    $"{{\"timestamp\":\"{Ts(at)}\",\"type\":\"response_item\",\"payload\":{{\"type\":\"custom_tool_call\",\"name\":\"apply_patch\",\"input\":{Esc($"*** Begin Patch\n*** Add File: {path}\n+# hi\n*** End Patch\n")}}}}}";

var dir = Directory.CreateTempSubdirectory("clayo-panefiles").FullName;
try
{
    var folder = Path.Combine(dir, "proj");
    Directory.CreateDirectory(folder);
    var opened = DateTime.UtcNow;
    var file = Path.Combine(dir, "t.jsonl");

    // The docs the transcript names, as they are on disk.
    string Doc(params string[] parts)
    {
        var path = Path.Combine([folder, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "# doc");
        return path;
    }

    // Claude
    File.WriteAllText(file, Write(Path.Combine(folder, "GONE.md"), opened.AddMinutes(-6)) + "\n");
    var tail = new TranscriptTail(folder, opened, codex: false);
    Check("a doc that was deleted since: ignored", tail.Read(file), false);

    File.AppendAllText(file, Write(Doc("OLD.md"), opened.AddMinutes(-5)) + "\n" + Prompt(opened.AddMinutes(-4)) + "\n");
    Check("a doc from before the pane opened (resumed): seen", tail.Read(file) ? tail.Doc : null, Path.Combine(folder, "OLD.md"));
    Check("  but a prompt from before then is not", tail.PromptAt, DateTime.MinValue);
    Doc("docs", "PLAN.md");
    Doc("HALF.md");
    Doc("notes", "README.md");

    File.AppendAllText(file, Write(Path.Combine(folder, "docs", "PLAN.md"), opened.AddSeconds(1)) + "\n");
    Check("a doc written since: seen", tail.Read(file), true);
    Check("  as its full path", tail.Doc, Path.Combine(folder, "docs", "PLAN.md"));
    Check("nothing new: nothing to say", tail.Read(file), false);

    File.AppendAllText(file, Write(Path.Combine(dir, "elsewhere.md"), opened.AddSeconds(2)) + "\n");
    Check("a doc outside the folder: ignored", tail.Read(file), false);
    File.AppendAllText(file, Write(Path.Combine(folder, "code.cs"), opened.AddSeconds(2)) + "\n");
    Check("a file that is not markdown: ignored", tail.Read(file), false);

    File.AppendAllText(file, Write(Path.Combine(folder, "HALF.md"), opened.AddSeconds(3)));
    Check("a line still being written: waits", tail.Read(file), false);
    File.AppendAllText(file, "\n");
    Check("  and is read once whole", tail.Read(file) ? tail.Doc : null, Path.Combine(folder, "HALF.md"));

    Check("no prompt yet", tail.PromptAt, DateTime.MinValue);
    var sent = opened.AddSeconds(4);
    File.AppendAllText(file, Prompt(sent) + "\n" + ToolResult(sent.AddSeconds(5)) + "\n");
    tail.Read(file);
    Check("a prompt you sent is its time, a tool result is not", tail.PromptAt, DateTime.Parse(Ts(sent), null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime());
    File.AppendAllText(file, Prompt(sent.AddSeconds(6)).Replace("{\"type\":\"user\",", "{\"type\":\"user\",\"isMeta\":true,") + "\n");
    tail.Read(file);
    Check("  nor one of Claude's meta lines", tail.PromptAt, DateTime.Parse(Ts(sent), null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime());

    // Codex
    var cfile = Path.Combine(dir, "c.jsonl");
    File.WriteAllText(cfile, Patch("notes/README.md", opened.AddSeconds(1)) + "\n");
    var ctail = new TranscriptTail(folder, opened, codex: true);
    Check("codex: a patch adding a doc, by relative path", ctail.Read(cfile) ? ctail.Doc : null, Path.Combine(folder, "notes", "README.md"));
    File.AppendAllText(cfile, Patch("../escape.md", opened.AddSeconds(2)) + "\n");
    Check("codex: a path leaving the folder: ignored", ctail.Read(cfile), false);
    // Escaped backslashes in the JSON: "\notes" must not read as the patch's line break.
    File.AppendAllText(cfile, Patch(Path.Combine(folder, "notes", "README.md"), opened.AddSeconds(2)) + "\n");
    Check("codex: a patch by full Windows path", ctail.Read(cfile) ? ctail.Doc : null, Path.Combine(folder, "notes", "README.md"));
    // Code mode (0.162): the patch sits in a JS string in exec's input, its line breaks escaped
    // twice; the FileChange that follows names the file plainly.
    Doc("docs", "scratch", "codex-test.md");
    File.AppendAllText(cfile, $"{{\"timestamp\":\"{Ts(opened.AddSeconds(3))}\",\"type\":\"response_item\",\"payload\":{{\"type\":\"custom_tool_call\",\"name\":\"exec\",\"input\":{Esc($"const r = await tools.apply_patch({Esc("*** Begin Patch\n*** Add File: docs/scratch/codex-test.md\n+# hi\n*** End Patch\n")});")}}}}}\n");
    File.AppendAllText(cfile, $"{{\"timestamp\":\"{Ts(opened.AddSeconds(3))}\",\"type\":\"event_msg\",\"payload\":{{\"type\":\"item_completed\",\"item\":{{\"type\":\"FileChange\",\"changes\":{{{Esc(Path.Combine(folder, "docs", "scratch", "codex-test.md"))}:{{\"type\":\"add\",\"content\":\"# hi\\n\"}}}}}}}}}}\n");
    Check("codex: a patch run from exec (code mode)", ctail.Read(cfile) ? ctail.Doc : null, Path.Combine(folder, "docs", "scratch", "codex-test.md"));

    string CodexUser(string text, DateTime at) =>
        $"{{\"timestamp\":\"{Ts(at)}\",\"type\":\"response_item\",\"payload\":{{\"type\":\"message\",\"role\":\"user\",\"content\":[{{\"type\":\"input_text\",\"text\":{Esc(text)}}}]}}}}";
    File.AppendAllText(cfile, CodexUser("<environment_context>cwd</environment_context>", opened.AddSeconds(3)) + "\n");
    ctail.Read(cfile);
    Check("codex: its own context is not a prompt", ctail.PromptAt, DateTime.MinValue);
    var csent = opened.AddSeconds(4);
    File.AppendAllText(cfile, CodexUser("what is in this screenshot?", csent) + "\n");
    ctail.Read(cfile);
    Check("codex: a prompt you sent is its time", ctail.PromptAt, DateTime.Parse(Ts(csent), null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime());

    // Pasted images
    var root = Path.Combine(dir, "claude");
    var images = Path.Combine(root, "C--proj", "sess-1", "images");
    Check("no paste yet: no folder", PastedImages.FindDir(root, "sess-1"), null);
    Directory.CreateDirectory(images);
    Check("found under its project", PastedImages.FindDir(root, "sess-1"), images);

    var since = DateTime.UtcNow;
    foreach (var (name, age) in new[] { ("1.png", -60), ("10.png", 5), ("2.webp", 5), ("notes.txt", 5) })
    {
        var f = Path.Combine(images, name);
        File.WriteAllText(f, "");
        File.SetLastWriteTimeUtc(f, since.AddSeconds(age));
    }
    Check("only images since then, in tag order", string.Join(",", PastedImages.Since(images, since).Select(Path.GetFileName)), "2.webp,10.png");
    Check("a folder that is gone: none", PastedImages.Since(Path.Combine(dir, "none"), since).Count, 0);

    // Codex: loose clipboard files in %TEMP%, among everything else there.
    var temp = Path.Combine(dir, "temp");
    Directory.CreateDirectory(temp);
    foreach (var (name, age) in new[] { ("codex-clipboard-b.png", 8), ("codex-clipboard-a.png", 4), ("codex-clipboard-old.png", -60), ("other.png", 5) })
    {
        var f = Path.Combine(temp, name);
        File.WriteAllText(f, "");
        File.SetLastWriteTimeUtc(f, since.AddSeconds(age));
    }
    Check("codex: only its clipboard files since then, oldest first",
        string.Join(",", PastedImages.Since(temp, since, PastedImages.CodexPattern).Select(Path.GetFileName)),
        "codex-clipboard-a.png,codex-clipboard-b.png");
}
finally { Directory.Delete(dir, recursive: true); }

Console.WriteLine(fail == 0 ? "\nall checks passed" : $"\n{fail} FAILED");
return fail;

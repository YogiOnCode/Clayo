using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CcxShell.Core;

/// <summary>
/// Clayo's Claude panes start with a status line of their own (see SessionLauncher):
///
///     "clayo.exe" --statusline --pass | {
///     their own statusLine command
///     }
///
/// Claude Code pipes the session's JSON to it on every refresh. We keep a copy for Clayo and
/// pass the same bytes on to the user's command, which the shell starts alongside us, so the
/// line inside the pane looks as it always has and our start-up overlaps theirs instead of
/// adding to it. With no command of theirs it is only `clayo.exe --statusline`, which prints
/// nothing. The user's settings.json is only ever read.
/// </summary>
public static class StatusRelay
{
    public const string Arg = "--statusline";
    public const string PassArg = "--pass";

    private static string ClayoDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clayo");

    public static string StatusDir => Path.Combine(ClayoDir, "status");

    public static string ClaudeConfigDir =>
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    /// <summary>
    /// The file panes pass to --settings. A dev build has its own, so its panes relay through
    /// the dev exe and the installed Clayo's through the installed one.
    /// </summary>
#if DEBUG
    private static string SettingsPath => Path.Combine(ClayoDir, "statusline.dev.json");
#else
    private static string SettingsPath => Path.Combine(ClayoDir, "statusline.json");
#endif

    /// <summary>
    /// Writes the --settings file for a pane about to start and returns its path, or null if
    /// it could not be written. Read fresh each time, so a pane follows the user's current
    /// statusLine. The rest of their statusLine (padding and the like) is copied as it is.
    /// </summary>
    public static string? WriteSettings(string exePath)
    {
        try
        {
            var statusLine = UserStatusLine()?.DeepClone() as JsonObject ?? new JsonObject();
            var theirs = statusLine["command"] is JsonValue v && v.TryGetValue(out string? c) && c.Length > 0 ? c : null;

            // Claude Code runs status lines through Git Bash, where a backslash is an escape.
            // The braces give their whole command our output, even a `cd x && ./line.sh`; the
            // newlines keep a trailing # comment of theirs from eating the closing brace.
            var relay = $"\"{exePath.Replace('\\', '/')}\" {Arg}";
            statusLine["type"] = "command";
            statusLine["command"] = theirs is null ? relay : $"{relay} {PassArg} | {{\n{theirs}\n}}";

            var json = new JsonObject { ["statusLine"] = statusLine }.ToJsonString();
            Directory.CreateDirectory(ClayoDir);
            if (!File.Exists(SettingsPath) || File.ReadAllText(SettingsPath) != json)
                File.WriteAllText(SettingsPath, json);
            return SettingsPath;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// `clayo --statusline`. Runs before any WPF or single-instance work: it is on the path of
    /// every status refresh. The user's command is waiting on our output, so that goes first.
    /// </summary>
    public static int Run(bool pass)
    {
        var input = new MemoryStream();
        using (var stdin = Console.OpenStandardInput()) stdin.CopyTo(input);
        var bytes = input.ToArray();

        if (pass)
        {
            try
            {
                using var stdout = Console.OpenStandardOutput();
                stdout.Write(bytes);
            }
            catch (IOException) { /* their command did not read it; ours still saves */ }
        }

        Save(bytes);
        return 0;
    }

    /// <summary>
    /// Written whole and renamed into place, so a reader never sees half a file. Anything odd
    /// is dropped: this must never break the user's line.
    /// </summary>
    private static void Save(byte[] json)
    {
        string? id;
        try { id = JsonDocument.Parse(json).RootElement.GetProperty("session_id").GetString(); }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { return; }

        // The id becomes a file name.
        if (!Guid.TryParse(id, out var guid)) return;

        var path = Path.Combine(StatusDir, guid + ".json");
        var tmp = Path.Combine(StatusDir, $"{guid}.{Environment.ProcessId}.tmp");
        try
        {
            Directory.CreateDirectory(StatusDir);
            File.WriteAllBytes(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static JsonObject? UserStatusLine()
    {
        try
        {
            var settings = JsonNode.Parse(File.ReadAllText(Path.Combine(ClaudeConfigDir, "settings.json")));
            return settings?["statusLine"] as JsonObject;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return null; }
    }
}

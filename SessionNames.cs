using System.IO;
using System.Text.Json;

namespace CcxShell.Core;

/// <summary>
/// Names you typed yourself, keyed by session id. Claude Code generates a decent title for
/// every session, but it describes what the conversation was about — not what you were trying
/// to do, and not which of four similar branches this one is. Overriding it is cheap.
///
/// Kept in %LOCALAPPDATA%\Clayo because ~/.claude/projects belongs to Claude Code and we
/// never write there.
/// </summary>
public sealed class SessionNames
{
    private readonly string _path;
    private Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);

    public SessionNames()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clayo");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "names.json");
        Load();
    }

    public string? Get(string sessionId) =>
        _names.TryGetValue(sessionId, out var n) && n.Length > 0 ? n : null;

    /// <summary>Blank or whitespace clears the override and falls back to the generated title.</summary>
    public void Set(string sessionId, string? name)
    {
        name = name?.Trim();

        if (string.IsNullOrEmpty(name)) _names.Remove(sessionId);
        else _names[sessionId] = name;

        Save();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var json = File.ReadAllText(_path);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (parsed is not null) _names = new(parsed, StringComparer.OrdinalIgnoreCase);
        }
        catch (IOException) { }
        catch (JsonException) { /* corrupt file — start clean rather than refuse to launch */ }
        catch (UnauthorizedAccessException) { }
    }

    private void Save()
    {
        try
        {
            // Write beside the target then swap, so a crash mid-write can't leave a
            // truncated file that loses every name.
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_names,
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

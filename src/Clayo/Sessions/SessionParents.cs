using System.IO;
using System.Text.Json;

namespace CcxShell.Core;

/// <summary>
/// Which session each branch came from, keyed by the child's id.
///
/// This has to be stored because it cannot be recovered: `--fork-session` copies the parent's
/// messages into the child and rewrites `sessionId` on every copied line, so a forked
/// transcript holds no reference to its origin. Nothing on disk knows the two are related.
/// A link is therefore only ever written at the moment we fork, which means branches made
/// outside Clayo, or before this existed, read as ordinary top-level sessions.
///
/// Same home as the names file: %LOCALAPPDATA%\Clayo, never ~/.claude/projects.
/// </summary>
public sealed class SessionParents
{
    private readonly string _path;
    private Dictionary<string, string> _parents = new(StringComparer.OrdinalIgnoreCase);

    public SessionParents()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clayo");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "parents.json");
        Load();
    }

    public string? Get(string sessionId) =>
        _parents.TryGetValue(sessionId, out var p) && p.Length > 0 ? p : null;

    public void Set(string childId, string parentId)
    {
        if (string.IsNullOrWhiteSpace(childId) || string.IsNullOrWhiteSpace(parentId)) return;

        // A session cannot branch from itself, and allowing it would put a cycle in the
        // chain walk that draws the indents.
        if (childId.Equals(parentId, StringComparison.OrdinalIgnoreCase)) return;

        _parents[childId] = parentId;
        Save();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var json = File.ReadAllText(_path);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (parsed is not null) _parents = new(parsed, StringComparer.OrdinalIgnoreCase);
        }
        catch (IOException) { }
        catch (JsonException) { /* corrupt file — start clean rather than refuse to launch */ }
        catch (UnauthorizedAccessException) { }
    }

    private void Save()
    {
        try
        {
            // Write beside the target then swap, so a crash mid-write cannot leave a
            // truncated file that loses every link.
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_parents,
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

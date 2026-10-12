using System.IO;

namespace CcxShell.Core;

/// <summary>
/// Claude Code keeps each image pasted into a session as
/// %TEMP%\claude\&lt;project&gt;\&lt;session&gt;\images\&lt;n&gt;.png, n being its [Image #n] tag, and
/// a pasted image path (Ctrl+Alt+S) is copied in there too. CLAUDE_CODE_TMPDIR moves the
/// %TEMP% part. Codex keeps no folder per session: a pasted image is a loose
/// %TEMP%\codex-clipboard-*.png (the name read from codex.exe 0.160.0), and a pasted path is
/// used where it is. Clayo only reads these; the agents own them and clean them up.
/// </summary>
public static class PastedImages
{
    private static readonly string[] Kinds = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"];

    public const string CodexPattern = "codex-clipboard-*";

    public static bool IsImage(string path) => Kinds.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static string Root => Path.Combine(
        Environment.GetEnvironmentVariable("CLAUDE_CODE_TMPDIR") is { Length: > 0 } dir ? dir : Path.GetTempPath(),
        "claude");

    /// <summary>The session's images folder, or null until its first paste makes one.</summary>
    public static string? FindDir(string root, string sessionId)
    {
        try
        {
            foreach (var project in Directory.EnumerateDirectories(root))
            {
                var dir = Path.Combine(project, sessionId, "images");
                if (Directory.Exists(dir)) return dir;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>The images written after <paramref name="sinceUtc"/>, in tag order, else oldest first.</summary>
    public static List<string> Since(string dir, DateTime sinceUtc, string pattern = "*")
    {
        try
        {
            return Directory.EnumerateFiles(dir, pattern)
                .Where(f => IsImage(f)
                            && File.GetLastWriteTimeUtc(f) > sinceUtc)
                .OrderBy(f => int.TryParse(Path.GetFileNameWithoutExtension(f), out var n) ? n : int.MaxValue)
                .ThenBy(File.GetLastWriteTimeUtc)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }
}

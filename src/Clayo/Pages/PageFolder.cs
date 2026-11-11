using System.IO;

namespace CcxShell.Core;

/// <summary>The .clayo folder a project's pages live in. No WPF.</summary>
public static class PageFolder
{
    private const string Line = ".clayo/";

    /// <summary>
    /// Keeps a project's pages out of git without touching its .gitignore: adds .clayo/ to the
    /// repo's own .git/info/exclude, which is never committed. Does nothing outside a git repo,
    /// or when the line is already there.
    /// </summary>
    public static void HideFromGit(string projectFolder)
    {
        try
        {
            // The session's folder may sit below the repo's root.
            var dir = new DirectoryInfo(projectFolder);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git"))) dir = dir.Parent;
            if (dir is null) return;

            var exclude = Path.Combine(dir.FullName, ".git", "info", "exclude");
            var text = File.Exists(exclude) ? File.ReadAllText(exclude) : "";
            if (text.Split('\n').Any(l => l.Trim() == Line)) return;

            Directory.CreateDirectory(Path.GetDirectoryName(exclude)!);
            File.AppendAllText(exclude, (text.Length == 0 || text.EndsWith('\n') ? "" : "\n") + Line + "\n");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

using System.IO;

namespace CcxShell.Core;

/// <summary>The .clayo folder a project's pages live in. No WPF.</summary>
public static class PageFolder
{
    private const string Line = ".clayo/";

    /// <summary>
    /// Keeps a project's pages out of git without touching its .gitignore: adds .clayo/ (or
    /// line) to the repo's own .git/info/exclude, which is never committed. Does nothing
    /// outside a git repo, or when the line is already there.
    /// </summary>
    public static void HideFromGit(string projectFolder, string line = Line)
    {
        try
        {
            // The session's folder may sit below the repo's root.
            var dir = new DirectoryInfo(projectFolder);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git"))) dir = dir.Parent;
            if (dir is null) return;

            var exclude = Path.Combine(dir.FullName, ".git", "info", "exclude");
            var text = File.Exists(exclude) ? File.ReadAllText(exclude) : "";
            if (text.Split('\n').Any(l => l.Trim() == line)) return;

            Directory.CreateDirectory(Path.GetDirectoryName(exclude)!);
            File.AppendAllText(exclude, (text.Length == 0 || text.EndsWith('\n') ? "" : "\n") + line + "\n");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Puts what an agent needs to make a page into the project's .clayo folder: the guide, the
    /// templates, and beside the pages the stylesheet, script and libraries they link, so a page
    /// also opens straight in a browser. A file is copied when it is missing or Clayo's is
    /// newer. The stylesheet is Clayo's with the theme after it (PageTheme), written again when
    /// either changes, so the browser shows what the viewer does. The pages themselves are
    /// never touched.
    /// </summary>
    public static void Setup(string projectFolder, string? assets = null, string themeCss = "")
    {
        assets ??= Path.Combine(AppContext.BaseDirectory, "Assets", "pages");
        var clayo = Path.Combine(projectFolder, ".clayo");
        try
        {
            foreach (var file in Directory.EnumerateFiles(assets, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetRelativePath(assets, file);
                if (name.StartsWith("themes" + Path.DirectorySeparatorChar)) continue;   // Clayo's to apply
                var to = Path.Combine(clayo, name.Contains(Path.DirectorySeparatorChar) || name == "page-guide.md" ? name : Path.Combine("pages", name));
                if (name == "clayo-page.css")
                {
                    var sheet = File.ReadAllText(file);
                    if (themeCss.Length > 0) sheet += "\n/* The theme picked in Clayo's settings. */\n" + themeCss;
                    if (File.Exists(to) && File.ReadAllText(to) == sheet) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    File.WriteAllText(to, sheet);
                    continue;
                }
                if (File.Exists(to) && File.GetLastWriteTimeUtc(to) >= File.GetLastWriteTimeUtc(file)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(file, to, overwrite: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        HideFromGit(projectFolder);
    }

    /// <summary>
    /// Writes Clayo's /page for Claude into the project (.claude/commands/page.md, kept out of
    /// git), or brings it up to date. True when it was there already, so the session has it; a
    /// /page of the user's own is never touched, and gives false.
    /// </summary>
    public static bool ClaudeCommand(string projectFolder, string text = PagePrompt.ClaudeCommand)
    {
        var file = Path.Combine(projectFolder, ".claude", "commands", "page.md");
        try
        {
            if (File.Exists(file))
            {
                var old = File.ReadAllText(file);
                if (!old.Contains(PagePrompt.ClaudeMarker)) return false;
                if (old != text) File.WriteAllText(file, text);
                return true;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, text);
            HideFromGit(projectFolder, ".claude/commands/page.md");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return false;
    }
}

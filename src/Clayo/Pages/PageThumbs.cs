using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace CcxShell.Core;

/// <summary>
/// The Pages view's pictures: one PNG per page in %LOCALAPPDATA%\Clayo\thumbs, named from the
/// page's path, made again when the page changes (PageExport, Thumb). No WPF.
/// </summary>
public static class PageThumbs
{
    public const int Width = 400;

    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clayo", "thumbs");

    public static string PathFor(string page, string? dir = null) =>
        Path.Combine(dir ?? Dir, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(page.ToLowerInvariant()))) + ".png");

    /// <summary>Whether the page's picture is there and no older than the page.</summary>
    public static bool IsFresh(string page, string? dir = null)
    {
        var thumb = PathFor(page, dir);
        return File.Exists(thumb) && File.GetLastWriteTimeUtc(thumb) >= File.GetLastWriteTimeUtc(page);
    }

    /// <summary>Deletes the pictures of pages that are no longer in the index.</summary>
    public static void Prune(IEnumerable<string> pages, string? dir = null)
    {
        dir ??= Dir;
        if (!Directory.Exists(dir)) return;
        var keep = pages.Select(p => PathFor(p, dir)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(dir, "*.png"))
        {
            if (keep.Contains(file)) continue;
            try { File.Delete(file); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}

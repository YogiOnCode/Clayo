using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CcxShell.Core;

/// <summary>
/// A screenshot on its way to a session (docs/SCREENSHOT.md): the clipboard's image, written
/// as a PNG the agent can be handed by path. Kept a week, like the status files.
/// </summary>
public static class Screenshots
{
    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clayo", "shots");

    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(7);

    public static bool OnClipboard()
    {
        try { return Clipboard.ContainsImage(); }
        catch (COMException) { return false; }   // another app holds the clipboard
    }

    /// <summary>The clipboard's image as a new PNG in <see cref="Dir"/>, or null if there is none.</summary>
    public static string? SaveFromClipboard()
    {
        // Whoever just copied may still hold the clipboard open for a moment.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (!Clipboard.ContainsImage() || Clipboard.GetImage() is not { } image) return null;
                Directory.CreateDirectory(Dir);
                var path = NewPath(Dir, DateTime.Now);
                // Without the alpha channel: a copied bitmap often carries an alpha of 0 that
                // means nothing, which would save as a fully transparent picture.
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(image, PixelFormats.Bgr32, null, 0)));
                using (var file = File.Create(path)) encoder.Save(file);
                return path;
            }
            catch (COMException) when (attempt < 5) { Thread.Sleep(50); }
            catch (Exception e) when (e is COMException or IOException or UnauthorizedAccessException) { return null; }
        }
    }

    /// <summary>yyyy-MM-dd_HH-mm-ss.png, with -2, -3 when that second already has one.</summary>
    public static string NewPath(string dir, DateTime now)
    {
        var stem = Path.Combine(dir, now.ToString("yyyy-MM-dd_HH-mm-ss"));
        var path = stem + ".png";
        for (int n = 2; File.Exists(path); n++) path = $"{stem}-{n}.png";
        return path;
    }

    /// <summary>Deletes the screenshots written before <paramref name="before"/>.</summary>
    public static void Prune(string dir, DateTime before)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.EnumerateFiles(dir, "*.png"))
        {
            try { if (File.GetLastWriteTimeUtc(file) < before) File.Delete(file); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

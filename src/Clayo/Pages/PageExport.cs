using System.IO;
using Microsoft.Web.WebView2.Core;

namespace CcxShell.Core;

public enum PageExportKind { Pdf, Png }

/// <summary>
/// A page as a PDF or PNG, made off-screen the way the check shows it: a page on A4 or as one
/// tall image, a deck one slide per 16:9 PDF page or one PNG per slide. Needs a thread with a
/// message loop (the app's own, or PageCheck.OnOwnThread from a console).
/// </summary>
public static class PageExport
{
    private const string PageHost = "ccx.page";
    private const int Width = 1280, SlideHeight = 720, TallestPng = 12000;

    // Says when charts and diagrams are drawn (clayo-page.js), or shortly after a page without
    // that script has loaded.
    private const string Ready = """
        document.addEventListener("clayo-page:ready", function () { setTimeout(function () { chrome.webview.postMessage("ready"); }, 150); });
        addEventListener("load", function () {
          setTimeout(function () { chrome.webview.postMessage("ready"); }, document.querySelector('script[src*="clayo-page.js"]') ? 5000 : 800);
        });
        """;

    /// <summary>
    /// Writes the export and returns its files. A PDF, or the PNG of a page, is the one file
    /// `to`; the PNGs of a deck go in a folder named as `to` without its extension.
    /// </summary>
    public static async Task<IReadOnlyList<string>> Run(string file, string to, PageExportKind kind)
    {
        // Printed and captured as a document of its own, not in the viewer's frame: a frame
        // prints as one clipped box. It keeps the page's seal, loads from a folder holding
        // nothing else, may go nowhere, and no one listens to it but for "ready".
        var temp = Directory.CreateTempSubdirectory("clayo-export").FullName;
        File.WriteAllText(Path.Combine(temp, "page.html"), PageHtml.Seal(File.ReadAllText(file)));

        var userData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CcxShell", "WebView2-check");
        Directory.CreateDirectory(userData);
        var env = await CoreWebView2Environment.CreateAsync(null, userData);
        var controller = await env.CreateCoreWebView2ControllerAsync(new IntPtr(-3));   // HWND_MESSAGE: no window
        try
        {
            var core = controller.CoreWebView2;
            core.SetVirtualHostNameToFolderMapping(
                "ccx.assets", Path.Combine(AppContext.BaseDirectory, "Assets"), CoreWebView2HostResourceAccessKind.DenyCors);
            core.SetVirtualHostNameToFolderMapping(PageHost, temp, CoreWebView2HostResourceAccessKind.DenyCors);
            var start = $"https://{PageHost}/page.html";
            core.NavigationStarting += (_, a) =>
            {
                if (!a.Uri.StartsWith(start, StringComparison.OrdinalIgnoreCase)) a.Cancel = true;
            };
            core.FrameNavigationStarting += (_, a) =>
            {
                if (!a.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) a.Cancel = true;
            };
            core.NewWindowRequested += (_, a) => a.Handled = true;
            core.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Dark;   // as on screen

            var ready = new TaskCompletionSource();
            core.WebMessageReceived += (_, _) => ready.TrySetResult();
            await core.AddScriptToExecuteOnDocumentCreatedAsync(Ready);

            // PNGs at twice the size, so they stay sharp when zoomed or shown on a big screen.
            int scale = kind == PageExportKind.Png ? 2 : 1;
            controller.ShouldDetectMonitorScaleChanges = false;
            controller.RasterizationScale = scale;
            controller.Bounds = new System.Drawing.Rectangle(0, 0, Width * scale, SlideHeight * scale);
            controller.IsVisible = true;
            core.Navigate(start);
            await Task.WhenAny(ready.Task, Task.Delay(TimeSpan.FromSeconds(10)));

            int slides = await Number(core, "document.querySelectorAll('.deck .slide').length");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(to))!);

            if (kind == PageExportKind.Pdf)
            {
                var print = env.CreatePrintSettings();
                print.ShouldPrintBackgrounds = true;
                print.ShouldPrintHeaderAndFooter = false;
                // The paper as the stylesheet's @page rules have it: a slide, or A4.
                (print.PageWidth, print.PageHeight) = slides > 0 ? (Width / 96.0, SlideHeight / 96.0) : (8.27, 11.69);
                double margin = slides > 0 ? 0 : 14 / 25.4;
                print.MarginTop = print.MarginBottom = print.MarginLeft = print.MarginRight = margin;
                if (!await core.PrintToPdfAsync(to, print)) throw new IOException($"couldn't write {to}");
                return [to];
            }

            // A picture has no use for the slide counter or a scrollbar.
            await core.ExecuteScriptAsync(
                "var s = document.createElement('style'); s.textContent = '.deck-nav { display: none !important } ::-webkit-scrollbar { display: none }'; document.head.appendChild(s);");

            if (slides == 0)
            {
                int tall = Math.Clamp(await Number(core, "document.documentElement.scrollHeight"), SlideHeight, TallestPng);
                controller.Bounds = new System.Drawing.Rectangle(0, 0, Width * scale, tall * scale);
                await Task.Delay(300);
                await Capture(core, to);
                return [to];
            }

            var folder = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(to))!, Path.GetFileNameWithoutExtension(to));
            Directory.CreateDirectory(folder);
            var files = new List<string>();
            for (int i = 1; i <= slides; i++)
            {
                await core.ExecuteScriptAsync($"location.hash = '#{i}'");
                await Task.Delay(200);
                files.Add(Path.Combine(folder, $"slide-{i:00}.png"));
                await Capture(core, files[^1]);
            }
            return files;
        }
        finally
        {
            controller.Close();
            try { Directory.Delete(temp, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    private static async Task<int> Number(CoreWebView2 core, string script) =>
        int.TryParse(await core.ExecuteScriptAsync(script), out var n) ? n : 0;

    private static async Task Capture(CoreWebView2 core, string to)
    {
        using var png = File.Create(to);
        await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, png);
    }
}

using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace CcxShell.Core;

/// <summary>
/// `clayo-page file` (or `clayo --page-check file`): shows a page off-screen exactly as the viewer would (the same
/// seal, the same sandboxed frame), at a desktop and a phone width, and says what went wrong:
/// script errors, requests the policy blocked, content wider than the screen. An agent runs it
/// once on the page it wrote. Needs no Clayo window.
/// </summary>
public static class PageCheck
{
    public const string Arg = "--page-check";

    /// <summary>The console copy of clayo.exe that is this command by its name alone.</summary>
    public const string Tool = "clayo-page";

    private static readonly (int Width, int Height)[] Sizes = [(1280, 800), (390, 844)];
    private static readonly TimeSpan GiveUp = TimeSpan.FromSeconds(15);
    private const int TallestShot = 4000;

    // The frame the viewer uses, in a page of nothing else. What the probe below finds comes
    // out as a message, since nothing can look into the frame from outside.
    private const string Host = """
        <!doctype html><meta charset="utf-8">
        <style>html, body { margin: 0; height: 100%; background: #14171c; } iframe { display: block; width: 100%; height: 100%; border: 0; }</style>
        <iframe id="f" sandbox="allow-scripts"></iframe>
        <script>
        const f = document.getElementById("f");
        chrome.webview.addEventListener("message", e => {
          const m = JSON.parse(e.data);
          if (m.t === "page") f.srcdoc = m.d;
          if (m.t === "height") f.style.height = m.d + "px";
        });
        addEventListener("message", e => {
          if (e.source === f.contentWindow && e.data && e.data.t === "check") chrome.webview.postMessage(JSON.stringify(e.data));
        });
        </script>
        """;

    // Runs inside the page, first. Gathers what broke, then measures once the page says it is
    // ready (clayo-page.js) or, for a page without that script, shortly after it loads.
    private const string Probe = """
        <script>(function () {
          var errors = [], blocked = [], sent = false;
          addEventListener("error", function (e) {
            var t = e.target;
            errors.push(t && t !== window && (t.src || t.href) ? "couldn't load " + (t.src || t.href) : String(e.message || "error") + (e.lineno ? " (line " + e.lineno + ")" : ""));
          }, true);
          addEventListener("unhandledrejection", function (e) { errors.push(String(e.reason && e.reason.message || e.reason)); });
          var ce = console.error;
          console.error = function () { errors.push([].join.call(arguments, " ")); ce.apply(console, arguments); };
          document.addEventListener("securitypolicyviolation", function (e) {
            blocked.push("blocked: " + (e.blockedURI || "inline") + " (" + e.violatedDirective + ")");
          });
          function name(el) { return el.tagName.toLowerCase() + (el.classList.length ? "." + el.classList[0] : ""); }
          function measure() {
            if (sent) return;
            sent = true;
            var w = document.documentElement.clientWidth, wide = null, tall = [];
            var slides = document.querySelectorAll(".deck .slide");
            if (!slides.length && document.documentElement.scrollWidth > w + 1) {
              var most = 0;
              document.querySelectorAll("body *").forEach(function (el) {
                var r = el.getBoundingClientRect();
                if (r.right > w + 1 && r.width > most && !el.closest(".table-wrap, pre")) { most = r.width; wide = name(el) + " is " + Math.round(r.width) + "px wide"; }
              });
              wide = wide || "the page is " + document.documentElement.scrollWidth + "px wide";
            }
            slides.forEach(function (s, i) {
              if (s.scrollHeight > s.clientHeight + 2 || s.scrollWidth > s.clientWidth + 2) tall.push(i + 1);
            });
            parent.postMessage({ t: "check", errors: errors, blocked: blocked, wide: wide, tall: tall,
              slides: slides.length, height: document.documentElement.scrollHeight }, "*");
          }
          document.addEventListener("clayo-page:ready", function () { setTimeout(measure, 150); });
          addEventListener("load", function () {
            setTimeout(measure, document.querySelector('script[src*="clayo-page.js"]') ? 5000 : 800);
          });
        })();</script>
        """;

    /// <summary>The command: prints the report and returns the exit code.</summary>
    public static int Run(string[] args)
    {
        string? file = null, outDir = null;
        for (int i = 0; i < args.Length; i++)
            if (args[i] == "--out" && i + 1 < args.Length) outDir = args[++i];
            else file ??= args[i];

        if (file is null || !File.Exists(file))
        {
            Console.WriteLine(file is null ? "usage: clayo-page <file.html> [--out <dir>]" : $"no such file: {file}");
            return PageReport.CouldNotRun;
        }
        file = Path.GetFullPath(file);
        outDir = Path.GetFullPath(outDir ?? Path.Combine(Path.GetDirectoryName(file)!, ".check"));

        var (result, failure) = Check(file, outDir);
        if (result is null)
        {
            // The usual reason: a sandboxed shell (Codex's) can't start the off-screen browser.
            Console.WriteLine($"{Path.GetFileName(file)}: couldn't check it: {failure}. Skip the check, or run it outside the sandbox.");
            return PageReport.CouldNotRun;
        }
        var (text, exit) = PageReport.Format(result);
        Console.WriteLine(text);
        return exit;
    }

    /// <summary>Checks one page. A null result comes with why it could not be checked.</summary>
    public static (PageCheckResult? Result, string? Failure) Check(string file, string outDir)
    {
        (PageCheckResult?, string?) done = (null, $"gave up after {GiveUp.TotalSeconds:0} s");
        // WebView2 needs a thread with a message loop; the caller's may be neither.
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var work = Render(file, outDir);
            var timer = new DispatcherTimer { Interval = GiveUp };
            timer.Tick += (_, _) => dispatcher.InvokeShutdown();
            timer.Start();
            work.ContinueWith(t =>
            {
                done = t.IsFaulted ? (null, t.Exception!.InnerException?.Message ?? "it failed") : (t.Result, null);
                dispatcher.InvokeShutdown();
            }, TaskScheduler.FromCurrentSynchronizationContext());
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
        return done;
    }

    private static async Task<PageCheckResult> Render(string file, string outDir)
    {
        var html = File.ReadAllText(file);
        var page = PageHtml.Seal(html);
        // Straight after the policy, so it is running before anything of the page's.
        int at = page.IndexOf('>', page.IndexOf("Content-Security-Policy", StringComparison.Ordinal)) + 1;
        // On one line, so the line numbers in the page's own errors stay the file's.
        page = page[..at] + Probe.ReplaceLineEndings(" ") + page[at..];

        // Its own user-data folder, shared by every check so the start stays fast.
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
            core.FrameNavigationStarting += (_, a) =>
            {
                if (!a.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) a.Cancel = true;
            };
            core.NewWindowRequested += (_, a) => a.Handled = true;
            core.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Dark;

            TaskCompletionSource<JsonElement>? report = null;
            core.WebMessageReceived += (_, a) =>
            {
                try
                {
                    using var doc = JsonDocument.Parse(a.TryGetWebMessageAsString());
                    report?.TrySetResult(doc.RootElement.Clone());
                }
                catch (Exception e) when (e is JsonException or ArgumentException) { }
            };

            // One CSS pixel per pixel, whatever this PC's display scaling: 390 means 390.
            controller.ShouldDetectMonitorScaleChanges = false;
            controller.RasterizationScale = 1;
            controller.Bounds = new System.Drawing.Rectangle(0, 0, Sizes[0].Width, Sizes[0].Height);
            controller.IsVisible = true;
            var loaded = new TaskCompletionSource();
            core.NavigationCompleted += (_, _) => loaded.TrySetResult();
            core.NavigateToString(Host);
            await loaded.Task;

            var name = Path.GetFileNameWithoutExtension(file);
            var errors = new List<string>();
            var blocked = new List<string>();
            var overflow = new List<string>();
            var shots = new List<string>();
            int slides = 0;
            Directory.CreateDirectory(outDir);

            foreach (var (width, height) in Sizes)
            {
                controller.Bounds = new System.Drawing.Rectangle(0, 0, width, height);
                core.PostWebMessageAsString(JsonSerializer.Serialize(new { t = "height", d = height }));
                report = new TaskCompletionSource<JsonElement>();
                core.PostWebMessageAsString(JsonSerializer.Serialize(new { t = "page", d = page }));
                var found = await report.Task;

                foreach (var e in Strings(found, "errors")) if (!errors.Contains(e)) errors.Add(e);
                foreach (var b in Strings(found, "blocked")) if (!blocked.Contains(b)) blocked.Add(b);
                if (found.TryGetProperty("wide", out var wide) && wide.GetString() is { } w)
                    overflow.Add($"overflow at {width}px: {w}");
                slides = found.GetProperty("slides").GetInt32();
                if (width == Sizes[0].Width)
                    foreach (var n in found.GetProperty("tall").EnumerateArray())
                        overflow.Add($"slide {n.GetInt32()}: its content doesn't fit the slide");

                // A page is captured whole; a deck is its first slide, at the screen's size.
                if (slides == 0)
                {
                    int tall = Math.Clamp(found.GetProperty("height").GetInt32(), height, TallestShot);
                    controller.Bounds = new System.Drawing.Rectangle(0, 0, width, tall);
                    core.PostWebMessageAsString(JsonSerializer.Serialize(new { t = "height", d = tall }));
                    await Task.Delay(250);
                }
                var shot = Path.Combine(outDir, $"{name}-{width}.png");
                using (var png = File.Create(shot))
                    await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, png);
                shots.Add(Path.GetRelativePath(Environment.CurrentDirectory, shot).Replace('\\', '/'));
            }
            return new PageCheckResult(Path.GetFileName(file), new FileInfo(file).Length, slides, errors, blocked, overflow, shots);
        }
        finally { controller.Close(); }
    }

    private static IEnumerable<string> Strings(JsonElement found, string name) =>
        found.GetProperty(name).EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0);
}

using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace CcxShell.UI;

/// <summary>
/// A deck full screen, over Clayo, until Esc. The page sits in the same sealed frame as in the
/// viewer; its own script moves between slides with the arrow keys and Space.
/// </summary>
public sealed class PresentWindow : Window
{
    // The viewer's frame in a page of nothing else, focused so the keys reach the deck.
    private const string Host = """
        <!doctype html><meta charset="utf-8">
        <style>html, body { margin: 0; height: 100%; background: #000; } iframe { display: block; width: 100%; height: 100%; border: 0; }</style>
        <iframe id="f" sandbox="allow-scripts"></iframe>
        <script>
        const f = document.getElementById("f");
        f.onload = () => f.focus();
        chrome.webview.addEventListener("message", e => { f.srcdoc = e.data; });
        </script>
        """;

    private readonly WebView2 _web = new();

    /// <param name="page">The page as the viewer shows it (PageHtml.Seal).</param>
    public PresentWindow(string page)
    {
        Title = "Clayo";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Background = Brushes.Black;
        Content = _web;

        // Esc is always an accelerator key, which WebView2 hands on wherever the focus is inside it.
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Closed += (_, _) => _web.Dispose();
        Loaded += async (_, _) =>
        {
            try
            {
                var userData = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CcxShell", "WebView2");
                await _web.EnsureCoreWebView2Async(await CoreWebView2Environment.CreateAsync(null, userData));
                var core = _web.CoreWebView2;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.SetVirtualHostNameToFolderMapping(
                    "ccx.assets", Path.Combine(AppContext.BaseDirectory, "Assets"), CoreWebView2HostResourceAccessKind.DenyCors);
                core.FrameNavigationStarting += (_, a) =>
                {
                    if (!a.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) a.Cancel = true;
                };
                core.NewWindowRequested += (_, a) => a.Handled = true;
                core.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Dark;
                core.NavigationCompleted += (_, _) =>
                {
                    core.PostWebMessageAsString(page);
                    _web.Focus();
                };
                core.NavigateToString(Host);
            }
            catch (Exception) when (!IsLoaded) { /* closed while WebView2 was still starting */ }
        };
    }

    /// <summary>Shows it over the screen the owner is on.</summary>
    public void ShowOver(Window owner)
    {
        Owner = owner;
        WindowStartupLocation = WindowStartupLocation.Manual;
        (Left, Top) = (owner.Left, owner.Top);
        Show();
        WindowState = WindowState.Maximized;
        Activate();
    }
}

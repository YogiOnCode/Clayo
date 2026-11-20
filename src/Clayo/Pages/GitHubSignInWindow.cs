using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using CcxShell.Core;

namespace CcxShell.UI;

/// <summary>
/// Signing in to the Clayo Pages GitHub App (GitHubSignIn): shows the code, copies it, opens
/// GitHub's page for it, and waits for the user to approve there. DialogResult is true when they did.
/// </summary>
public sealed class GitHubSignInWindow : Window
{
    private readonly TextBox _code = new()
    {
        IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
        FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 30, FontWeight = FontWeights.SemiBold,
        HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 14), Text = "…"
    };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) };
    private readonly Button _open = new() { Content = "Copy code and open GitHub", IsEnabled = false, Padding = new Thickness(14, 6, 14, 6) };
    private readonly CancellationTokenSource _stop = new();
    private DeviceCode? _device;

    public GitHubSignInWindow(Window? owner)
    {
        Title = "Sign in to GitHub";
        Owner = owner;
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        SizeToContent = SizeToContent.Height;
        Width = 440;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = owner is null;
        Background = Brush("SurfaceBg", "#1B1F26");
        var text = Brush("TextPrimary", "#C8CDD6");
        _code.Foreground = text;
        _status.Foreground = Brush("MutedAA", "#8C94A1");
        if (TryFindResource("AccentButton") is Style accent) _open.Style = accent;

        var cancel = new Button { Content = "Cancel", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(14, 6, 14, 6), IsCancel = true };
        if (TryFindResource("GhostButton") is Style ghost) cancel.Style = ghost;
        _open.Click += (_, _) => OpenGitHub();

        Content = new StackPanel
        {
            Margin = new Thickness(24, 20, 24, 20),
            Children =
            {
                new TextBlock
                {
                    Foreground = text, TextWrapping = TextWrapping.Wrap,
                    Text = "Sharing a page puts it in a secret gist on your GitHub. Sign in once to the Clayo Pages app: it can reach your gists and nothing else. Enter this code on GitHub:"
                },
                _code,
                _status,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { _open, cancel } }
            }
        };

        SourceInitialized += (_, _) => MainWindow.DarkCaption(new WindowInteropHelper(this).Handle);
        Loaded += async (_, _) => await Run();
        Closed += (_, _) => _stop.Cancel();
    }

    private Brush Brush(string key, string fallback) =>
        TryFindResource(key) as Brush ?? new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallback));

    private void OpenGitHub()
    {
        if (_device is null) return;
        try { Clipboard.SetText(_device.User); }
        catch (COMException) { }
        Process.Start(new ProcessStartInfo(_device.Uri) { UseShellExecute = true })?.Dispose();
        _status.Text = "Code copied. Paste it on GitHub and approve Clayo Pages; this window closes by itself.";
    }

    private async Task Run()
    {
        try
        {
            _status.Text = "Asking GitHub for a code…";
            var device = _device = await GitHubSignIn.Start();
            _code.Text = device.User;
            _open.IsEnabled = true;
            _open.Focus();
            _status.Text = "Press the button: it copies the code and opens GitHub.";

            var interval = device.Interval;
            while (!_stop.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(interval), _stop.Token);
                if (DateTime.UtcNow > device.Until) { Fail("The code ran out. Close this and share again for a new one."); return; }
                var poll = await GitHubSignIn.Poll(device with { Interval = interval });
                if (poll.Token is { } token)
                {
                    // Approved on GitHub, so kept even if this window was closed meanwhile.
                    GitHubSignIn.Save(token);
                    if (!_stop.IsCancellationRequested) DialogResult = true;
                    return;
                }
                if (_stop.IsCancellationRequested) return;
                if (poll.Error is { } error)
                {
                    Fail(error == "access_denied" ? "You declined on GitHub. Nothing was shared."
                        : error is "expired_token" or "token_expired" ? "The code ran out. Close this and share again for a new one."
                        : $"GitHub said: {error}. Nothing was shared.");
                    return;
                }
                interval = poll.Interval;
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (GitHubException e) when (!_stop.IsCancellationRequested)
        {
            Fail($"GitHub said: {e.Message}. Nothing was shared.");
        }
        catch (Exception e) when (!_stop.IsCancellationRequested && e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException
            or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            Fail(e switch
            {
                HttpRequestException or TaskCanceledException => "Couldn't reach GitHub. Check the connection and share again.",
                System.Text.Json.JsonException => "GitHub's answer wasn't what Clayo expected. Share again later.",
                _ => "Signed in, but Clayo couldn't keep the sign-in on this PC. Share again."
            });
        }
    }

    private void Fail(string text)
    {
        _status.Text = text;
        _status.Foreground = Brush("Failed", "#D2605F");
        _open.IsEnabled = false;
    }
}

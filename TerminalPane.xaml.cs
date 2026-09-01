using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using CcxShell.Core;
using Microsoft.Web.WebView2.Core;

namespace CcxShell.UI;

public enum PaneStatus
{
    Starting,
    Working,
    Idle,
    Exited
}

public partial class TerminalPane : UserControl
{
    private const string VirtualHost = "ccx.assets";

    private readonly PtyProcess _pty = new();
    private readonly LaunchPlan _plan;
    private bool _ready;
    private bool _typedCommand;
    private readonly List<byte[]> _pending = new();

    private DateTime _lastOutput = DateTime.UtcNow;
    private readonly Queue<DateTime> _recent = new();
    private readonly System.Windows.Threading.DispatcherTimer _idleTimer;
    private PaneStatus _status = PaneStatus.Starting;

    /// <summary>Session id once known. For a fork this is set before the child exists.</summary>
    public string? SessionId { get; }

    public string WorkingDirectory => _plan.WorkingDirectory;

    public PaneStatus Status
    {
        get => _status;
        private set
        {
            if (_status == value) return;
            _status = value;
            StatusChanged?.Invoke(this, value);
        }
    }

    public event EventHandler<PaneStatus>? StatusChanged;

    public TerminalPane(LaunchPlan plan, string? sessionId)
    {
        InitializeComponent();
        _plan = plan;
        SessionId = plan.ExpectedSessionId ?? sessionId;

        // Working vs waiting-for-you, from the shape of the output stream alone.
        //
        // "Any output means working" is wrong: measured over a 30 s idle session, Claude
        // Code still emits the odd redraw — one chunk after 8 s of silence — which flipped
        // the light to "running" for two seconds at a time while nothing was happening.
        //
        // So the two transitions use different tests. Going quiet is enough to call it
        // waiting. Claiming it is working needs *sustained* output, which a lone redraw
        // cannot fake. Between the two the previous state stands, so the light is steady
        // rather than strobing.
        _idleTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };
        _idleTimer.Tick += (_, __) =>
        {
            if (Status == PaneStatus.Exited) return;

            var now = DateTime.UtcNow;
            var quietFor = (now - _lastOutput).TotalMilliseconds;

            int burst;
            lock (_recent)
                burst = _recent.Count(t => (now - t).TotalMilliseconds < 900);

            if (quietFor > 1500) Status = PaneStatus.Idle;
            else if (burst >= 3) Status = PaneStatus.Working;
        };

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        // One shared user-data folder keeps startup fast across panes.
        var userData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CcxShell", "WebView2");
        Directory.CreateDirectory(userData);

        var env = await CoreWebView2Environment.CreateAsync(null, userData);
        await Web.EnsureCoreWebView2Async(env);

        var core = Web.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.AreDevToolsEnabled = true;   // F12 while you're building this
        core.Settings.IsStatusBarEnabled = false;

        var assets = Path.Combine(AppContext.BaseDirectory, "Assets");
        core.SetVirtualHostNameToFolderMapping(
            VirtualHost, assets, CoreWebView2HostResourceAccessKind.Allow);

        core.WebMessageReceived += OnWebMessage;
        core.Navigate($"https://{VirtualHost}/terminal.html");
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonElement msg;
        try { msg = JsonDocument.Parse(e.WebMessageAsJson).RootElement; }
        catch (JsonException) { return; }

        // WebMessageAsJson wraps a posted string as a JSON string literal.
        if (msg.ValueKind == JsonValueKind.String)
        {
            try { msg = JsonDocument.Parse(msg.GetString() ?? "{}").RootElement; }
            catch (JsonException) { return; }
        }

        var type = msg.TryGetProperty("t", out var t) ? t.GetString() : null;

        switch (type)
        {
            case "ready":
                _ready = true;
                StartPty(
                    (short)(msg.TryGetProperty("cols", out var c) ? c.GetInt32() : 80),
                    (short)(msg.TryGetProperty("rows", out var r) ? r.GetInt32() : 24));
                FlushPending();
                break;

            case "i":
                if (msg.TryGetProperty("d", out var d) && d.GetString() is { } b64)
                    _pty.Write(Convert.FromBase64String(b64));
                break;

            case "r":
                _pty.Resize(
                    (short)(msg.TryGetProperty("cols", out var rc) ? rc.GetInt32() : 80),
                    (short)(msg.TryGetProperty("rows", out var rr) ? rr.GetInt32() : 24));
                break;
        }
    }

    private void StartPty(short cols, short rows)
    {
        _pty.OutputReceived += bytes =>
        {
            var now = DateTime.UtcNow;
            _lastOutput = now;
            lock (_recent)
            {
                _recent.Enqueue(now);
                while (_recent.Count > 12) _recent.Dequeue();
            }
            Dispatcher.BeginInvoke(() => Push(bytes));
        };

        _pty.Exited += () => Dispatcher.BeginInvoke(() =>
        {
            Status = PaneStatus.Exited;
            _idleTimer.Stop();
            Post(new { t = "notice", d = "[shell exited]" });
        });

        try
        {
            _pty.Start(_plan.ShellCommandLine, _plan.WorkingDirectory, cols, rows);
        }
        catch (Exception ex)
        {
            Status = PaneStatus.Exited;
            Post(new { t = "notice", d = $"Couldn't start the shell: {ex.Message}" });
            return;
        }

        _idleTimer.Start();

        // Let the prompt paint before typing, or PowerShell eats the first characters.
        var delay = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };
        delay.Tick += (s, _) =>
        {
            delay.Stop();
            if (_typedCommand) return;
            _typedCommand = true;
            _pty.Write(_plan.ClaudeCommand + "\r");
        };
        delay.Start();
    }

    private void Push(byte[] bytes)
    {
        if (!_ready)
        {
            _pending.Add(bytes);
            return;
        }
        Post(new { t = "o", d = Convert.ToBase64String(bytes) });
    }

    private void FlushPending()
    {
        foreach (var chunk in _pending) Push(chunk);
        _pending.Clear();
    }

    private void Post(object payload)
    {
        var core = Web.CoreWebView2;
        if (core is null) return;
        try { core.PostWebMessageAsString(JsonSerializer.Serialize(payload)); }
        catch (InvalidOperationException) { /* navigating */ }
    }

    public void FocusTerminal()
    {
        Web.Focus();
        Post(new { t = "focus" });
    }

    public void Close()
    {
        _idleTimer.Stop();
        _pty.Dispose();
    }
}

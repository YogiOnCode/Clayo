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

    /// <summary>Claude asked something and is blocked until you answer.</summary>
    NeedsInput,

    /// <summary>Quiet with nothing outstanding — the turn is over.</summary>
    Done,

    /// <summary>Claude Code printed an API failure.</summary>
    Error,

    /// <summary>The shell itself closed.</summary>
    Exited
}

public partial class TerminalPane : UserControl
{
    private const string VirtualHost = "ccx.assets";

    private readonly PtyProcess _pty = new();
    private readonly LaunchPlan _plan;
    private bool _typedCommand;

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

    public TerminalPane(LaunchPlan plan)
    {
        InitializeComponent();
        _plan = plan;
        SessionId = plan.ExpectedSessionId;

        // Working vs waiting-for-you, from the shape of the output stream alone.
        //
        // "Any output means working" is wrong: measured over a 30 s idle session, Claude
        // Code still emits the odd redraw — one chunk after 8 s of silence — which flipped
        // the light to "running" for two seconds at a time while nothing was happening.
        //
        // So the two transitions use different tests. Going quiet is enough to call the
        // turn over. Claiming it is working needs *sustained* output, which a lone redraw
        // cannot fake. Between the two the previous state stands, so the light is steady
        // rather than strobing.
        //
        // Silence cannot tell "finished" from "waiting on you", so those two do not come
        // from timing at all — see Scan.
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

            // Sustained output means work resumed, which clears a question or an error.
            if (burst >= 3) Status = PaneStatus.Working;
            // ponytail: a question or an error outlives the silence behind it. Without this,
            // going quiet would immediately repaint both of them as "done".
            else if (Status is PaneStatus.NeedsInput or PaneStatus.Error) return;
            else if (quietFor > 1500) Status = PaneStatus.Done;
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
                StartPty(
                    (short)(msg.TryGetProperty("cols", out var c) ? c.GetInt32() : 80),
                    (short)(msg.TryGetProperty("rows", out var r) ? r.GetInt32() : 24));
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
            var seen = Scan(bytes, ref _carry);
            Dispatcher.BeginInvoke(() =>
            {
                if (seen is { } s) Status = s;
                Post(new { t = "o", d = Convert.ToBase64String(bytes) });
            });
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

    // ponytail: two ASCII literals, not a parser. Verified against the transcripts and
    // the CLI binary on this machine: every API failure Claude Code prints begins
    // "API Error:", and every permission prompt asks "Do you want to ...". ASCII bytes
    // never occur inside a multi-byte UTF-8 sequence, so the raw stream can be scanned
    // without decoding it.
    private static readonly byte[] ErrorMark = "API Error:"u8.ToArray();
    private static readonly byte[] AskMark = "Do you want to"u8.ToArray();

    private byte[] _carry = [];

    /// <summary>
    /// The status a chunk implies, or null if it says nothing new. <paramref name="carry"/>
    /// holds the tail of the previous chunk so a marker straddling two pipe reads still
    /// matches. Called from the pty read thread only.
    /// </summary>
    public static PaneStatus? Scan(byte[] chunk, ref byte[] carry)
    {
        byte[] buf = carry.Length == 0 ? chunk : [.. carry, .. chunk];
        int keep = Math.Max(ErrorMark.Length, AskMark.Length) - 1;
        carry = buf.Length <= keep ? buf : buf[^keep..];

        var span = buf.AsSpan();
        if (span.IndexOf(ErrorMark) >= 0) return PaneStatus.Error;
        if (span.IndexOf(AskMark) >= 0) return PaneStatus.NeedsInput;
        return null;
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

using System.IO;
using System.Text;
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
    private DateTime _typedAt = DateTime.MaxValue;
    private bool _closed;

    private DateTime _lastOutput = DateTime.UtcNow;
    private DateTime _resizedAt = DateTime.MinValue;
    private (short, short) _ptySize;
    private readonly Queue<DateTime> _recent = new();
    private readonly System.Windows.Threading.DispatcherTimer _idleTimer;
    private PaneStatus _status = PaneStatus.Starting;

    // Output waiting to cross the bridge. Written on the pty read thread, drained on the UI thread.
    private readonly object _outLock = new();
    private readonly MemoryStream _out = new();
    private bool _flushQueued;
    private readonly System.Windows.Threading.DispatcherTimer _flushTimer;

    /// <summary>
    /// Session id once known. For a Claude fork this is set before the child exists; a new
    /// Codex pane or fork gets it from MainWindow once its transcript appears.
    /// </summary>
    public string? SessionId { get; set; }

    public AgentKind Agent => _plan.Agent;

    public string WorkingDirectory => _plan.WorkingDirectory;

    /// <summary>
    /// Text to type into Claude's input once, without Enter, so you finish the prompt and
    /// send it yourself. Typed on the pane's first quiet after the claude command (see
    /// TypePrefill).
    /// </summary>
    public string? Prefill { get; set; }

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
            if (_turnKnown)
            {
                if ((now - _lastOutput).TotalMilliseconds > 1500) TypePrefill();
                return;
            }
            var quietFor = (now - _lastOutput).TotalMilliseconds;

            int burst;
            lock (_recent)
                burst = _recent.Count(t => (now - t).TotalMilliseconds < 900);

            // Sustained output means work resumed, which clears a question or an error.
            if (burst >= 3) Status = PaneStatus.Working;
            // ponytail: a question or an error outlives the silence behind it. Without this,
            // going quiet would immediately repaint both of them as "done".
            else if (Status is PaneStatus.NeedsInput or PaneStatus.Error) return;
            else if (quietFor > 1500)
            {
                Status = PaneStatus.Done;
                TypePrefill();
            }
        };

        _flushTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(8)
        };
        _flushTimer.Tick += (_, __) => FlushOutput();

        Loaded += OnLoaded;
    }

    // Only our own page may drive the pane: a message from it is typed into the shell.
    private const string Origin = $"https://{VirtualHost}/";

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        // An exception here would end Clayo and every session in it (App only logs them), so a
        // pane that cannot load says so and the rest carry on.
        try { await LoadWeb(); }
        catch (Exception ex) when (!_closed)
        {
            LoadError.Text = $"Couldn't load the terminal: {ex.Message}";
            LoadError.Visibility = Visibility.Visible;
        }
        catch when (_closed) { /* closed while WebView2 was still starting */ }
    }

    private async Task LoadWeb()
    {
        // One shared user-data folder keeps startup fast across panes.
        var userData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CcxShell", "WebView2");
        Directory.CreateDirectory(userData);

        // A file or link dropped on the pane would otherwise navigate away to it.
        Web.AllowExternalDrop = false;

        var env = await CoreWebView2Environment.CreateAsync(null, userData);
        if (_closed) return;
        await Web.EnsureCoreWebView2Async(env);
        if (_closed) return;

        var core = Web.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
#if DEBUG
        core.Settings.AreDevToolsEnabled = true;   // F12 while you're building this
#else
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;   // F5, Ctrl+P and the like
#endif

        var assets = Path.Combine(AppContext.BaseDirectory, "Assets");
        core.SetVirtualHostNameToFolderMapping(
            VirtualHost, assets, CoreWebView2HostResourceAccessKind.DenyCors);

        core.NavigationStarting += (_, args) =>
        {
            if (!args.Uri.StartsWith(Origin, StringComparison.OrdinalIgnoreCase)) args.Cancel = true;
        };
        // No windows inside Clayo. Links in the output come over as "link" messages instead.
        core.NewWindowRequested += (_, args) => args.Handled = true;
        // A crashed renderer leaves a blank pane; the shell behind it is still running.
        core.ProcessFailed += (_, args) =>
        {
            if (!_closed && args.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited)
                core.Reload();
        };

        core.WebMessageReceived += OnWebMessage;
        core.Navigate($"{Origin}terminal.html");
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!e.Source.StartsWith(Origin, StringComparison.OrdinalIgnoreCase)) return;

        JsonElement msg;
        try
        {
            using var outer = JsonDocument.Parse(e.WebMessageAsJson);
            // WebMessageAsJson wraps a posted string as a JSON string literal.
            if (outer.RootElement.ValueKind != JsonValueKind.String) msg = outer.RootElement.Clone();
            else
            {
                using var inner = JsonDocument.Parse(outer.RootElement.GetString() ?? "{}");
                msg = inner.RootElement.Clone();
            }
        }
        catch (JsonException) { return; }

        var type = msg.TryGetProperty("t", out var t) ? t.GetString() : null;

        switch (type)
        {
            case "ready":
                // A reloaded page reports ready again; the shell it belongs to is already running.
                if (_started)
                {
                    _pty.Resize(_ptySize.Item1, _ptySize.Item2);   // repaints the screen
                    break;
                }
                StartPty(
                    (short)(msg.TryGetProperty("cols", out var c) ? c.GetInt32() : 80),
                    (short)(msg.TryGetProperty("rows", out var r) ? r.GetInt32() : 24));
                break;

            case "i":
                if (msg.TryGetProperty("d", out var d) && d.GetString() is { } b64)
                    _pty.Write(Convert.FromBase64String(b64));
                break;

            case "r":
                // A minimized window still gets the odd layout pass (a title change is enough),
                // measured against a client area that is not the real one: seen as 124 -> 105
                // cols with nothing on screen. Passed on, Claude Code repaints at the wrong width,
                // then again on restore. The fit after restoring reports the true size, which the
                // pty usually still has, and even a same-size resize makes ConPTY repaint.
                var size = ((short)(msg.TryGetProperty("cols", out var rc) ? rc.GetInt32() : 80),
                            (short)(msg.TryGetProperty("rows", out var rr) ? rr.GetInt32() : 24));
                if (Window.GetWindow(this)?.WindowState == WindowState.Minimized || size == _ptySize) break;
                _ptySize = size;
                _resizedAt = DateTime.UtcNow;
                _pty.Resize(size.Item1, size.Item2);
                break;

            case "paste":
                PasteFilesFromClipboard();
                break;

            case "link":
                // Web links only: a file: or other scheme from the output would run something.
                if (msg.TryGetProperty("d", out var l) && Uri.TryCreate(l.GetString(), UriKind.Absolute, out var uri)
                    && uri.Scheme is "http" or "https")
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
                break;
        }
    }

    /// <summary>
    /// Windows Terminal pastes the paths when the clipboard holds files rather than text,
    /// and Claude Code reads a path it is given in a prompt. The browser cannot do this:
    /// a File on the clipboard carries a name and no path, so its own paste inserts
    /// nothing at all. Runs on every Ctrl+V and does nothing unless the clipboard is
    /// files with no text, which leaves an ordinary text paste to the browser as before.
    /// </summary>
    private void PasteFilesFromClipboard()
    {
        if (Clipboard.ContainsText() || !Clipboard.ContainsFileDropList()) return;
        InsertPaths(Clipboard.GetFileDropList().Cast<string>());
    }

    /// <summary>
    /// Types paths at the prompt, space separated and quoted when one contains a space,
    /// with a trailing space so you can carry on typing. Deliberately no Enter: the
    /// prompt stays yours to send, so you can say what to do with the files first.
    /// </summary>
    public void InsertPaths(IEnumerable<string> paths)
    {
        var text = string.Join(" ", paths.Select(p => p.Contains(' ') ? $"\"{p}\"" : p));
        if (text.Length == 0) return;
        _pty.Write(Encoding.UTF8.GetBytes(text + " "));
    }

    /// <summary>
    /// Text as a terminal paste. Claude Code and Codex both turn a pasted image path into an
    /// attachment, where the same path typed stays text (docs/SCREENSHOT.md, P0).
    /// </summary>
    public static string Pasted(string text) => $"\x1b[200~{text}\x1b[201~";

    /// <summary>Pastes into the prompt, without Enter: what to ask about it is yours to add.</summary>
    public void Paste(string text) => _pty.Write(Encoding.UTF8.GetBytes(Pasted(text)));

    private bool _started;

    private void StartPty(short cols, short rows)
    {
        _started = true;
        _pty.OutputReceived += bytes =>
        {
            var now = DateTime.UtcNow;
            _lastOutput = now;
            // A resize makes Claude Code repaint the whole screen at once. Counted, that reads
            // as sustained output and flips a finished pane to Working and back, which raises
            // a Done for a turn that never happened. It still counts as output for going quiet.
            if ((now - _resizedAt).TotalMilliseconds > 500)
                lock (_recent)
                {
                    _recent.Enqueue(now);
                    while (_recent.Count > 12) _recent.Dequeue();
                }
            var seen = Scan(bytes, ref _carry, codex: Agent == AgentKind.Codex);
            if (seen is { } s) Dispatcher.BeginInvoke(() =>
            {
                if (s == PaneStatus.NeedsInput) _askedAt = DateTime.Now;
                Status = s;
            });

            // ConPTY hands over one redraw in several reads. Posted one by one, xterm can
            // paint between them and show a half-drawn screen, which is the flicker. So
            // output is held for about a frame and crosses the bridge as one message.
            bool schedule;
            lock (_outLock)
            {
                _out.Write(bytes);
                schedule = !_flushQueued;
                _flushQueued = true;
            }
            if (schedule) Dispatcher.BeginInvoke(() => _flushTimer.Start());
        };

        _pty.Exited += () => Dispatcher.BeginInvoke(() =>
        {
            FlushOutput();
            Status = PaneStatus.Exited;
            _idleTimer.Stop();
            Post(new { t = "notice", d = "[shell exited]" });
        });

        try
        {
            _pty.Start(_plan.ShellCommandLine, _plan.WorkingDirectory, cols, rows);
            _ptySize = (cols, rows);
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
            _typedAt = DateTime.UtcNow;
            _pty.Write(_plan.AgentCommand + "\r");
        };
        delay.Start();
    }

    /// <summary>
    /// Claude has drawn its input box once the output after the typed command goes quiet: the
    /// same 1.5 s of silence that marks a turn over. The output must be newer than the command,
    /// or a pane that was silent while WebView2 loaded would type into PowerShell first. A
    /// claude slow enough to load in silence for 1.5 s gets the text a little early, in the
    /// console's input buffer, which it reads as typeahead once it starts.
    /// </summary>
    private void TypePrefill()
    {
        if (Prefill is not { } text || _lastOutput <= _typedAt) return;
        Prefill = null;
        _pty.Write(Encoding.UTF8.GetBytes(text));
    }

    // ponytail: two ASCII literals, not a parser. Verified against the transcripts and
    // the CLI binary on this machine: every API failure Claude Code prints begins
    // "API Error:", and every permission prompt asks "Do you want to ...". ASCII bytes
    // never occur inside a multi-byte UTF-8 sequence, so the raw stream can be scanned
    // without decoding it.
    private static readonly byte[] ErrorMark = "API Error:"u8.ToArray();
    private static readonly byte[] AskMark = "Do you want to"u8.ToArray();

    // Codex asks before a command, an edit, input to a terminal or new permissions, all
    // "Would you like to ..." (read from codex.exe 0.160.0). Network access is the one
    // "Do you want to", which AskMark already matches. Codex panes only: Claude's own replies
    // say "Would you like to" too often.
    private static readonly byte[] CodexAskMark = "Would you like to"u8.ToArray();

    private byte[] _carry = [];

    /// <summary>
    /// The status a chunk implies, or null if it says nothing new. <paramref name="carry"/>
    /// holds the tail of the previous chunk so a marker straddling two pipe reads still
    /// matches. Called from the pty read thread only.
    /// </summary>
    public static PaneStatus? Scan(byte[] chunk, ref byte[] carry, bool codex = false)
    {
        byte[] buf = carry.Length == 0 ? chunk : [.. carry, .. chunk];
        int keep = Math.Max(ErrorMark.Length, Math.Max(AskMark.Length, CodexAskMark.Length)) - 1;
        carry = buf.Length <= keep ? buf : buf[^keep..];

        var span = buf.AsSpan();
        if (span.IndexOf(ErrorMark) >= 0) return PaneStatus.Error;
        if (span.IndexOf(AskMark) >= 0) return PaneStatus.NeedsInput;
        if (codex && span.IndexOf(CodexAskMark) >= 0) return PaneStatus.NeedsInput;
        return null;
    }

    // A Codex pane's transcript has reported a turn, so its status is exact from here on (ShowTurn).
    private bool _turnKnown;

    // When a question last appeared on screen, local time, as the transcript's timestamps are.
    private DateTime _askedAt;

    /// <summary>
    /// A Codex pane's status from its transcript (docs/SETUP.md, "Codex status from the
    /// transcript"): working and done are exact there. MainWindow calls it whenever the
    /// transcript changes; at is its last line's time.
    /// </summary>
    public void ShowTurn(TurnState turn, DateTime at)
    {
        _turnKnown = true;
        if (FromTurn(Status, turn, at, _askedAt) is { } next) Status = next;
    }

    /// <summary>
    /// Null leaves the status as it is. A question on screen is not in the transcript, and the
    /// tool call it asks about is written just before it appears, so only a line newer than
    /// the question means it was answered. A stopped turn reads as done: nothing is running.
    /// </summary>
    public static PaneStatus? FromTurn(PaneStatus current, TurnState turn, DateTime at, DateTime askedAt)
    {
        if (current == PaneStatus.Exited) return null;
        if (current == PaneStatus.NeedsInput && at <= askedAt) return null;
        return turn == TurnState.Working ? PaneStatus.Working : PaneStatus.Done;
    }

    private void FlushOutput()
    {
        _flushTimer.Stop();
        byte[] bytes;
        lock (_outLock)
        {
            bytes = _out.ToArray();
            _out.SetLength(0);
            _flushQueued = false;
        }
        if (bytes.Length > 0) Post(new { t = "o", d = Convert.ToBase64String(bytes) });
    }

    private void Post(object payload)
    {
        if (_closed) return;
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
        if (_closed) return;
        _closed = true;
        _idleTimer.Stop();
        _flushTimer.Stop();
        _pty.Dispose();

        // Dropping the pane out of the visual tree does not end its WebView2. The
        // renderer survives until clayo exits: measured with four panes removed and
        // garbage collected, 560 MB of msedgewebview2 was still resident, and fell
        // to 0 the moment Dispose was called. Nothing else reclaims it.
        Web.Dispose();
    }
}

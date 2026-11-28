namespace CcxShell.Core;

/// <summary>
/// The speech model's download as Settings › Voice shows it. It runs on with Settings closed,
/// so it lives with the window, not the page. Made and called on the UI thread; Changed is
/// raised there (post is how: Dispatcher.BeginInvoke).
/// </summary>
public sealed class VoiceSetup(string dir, Action<Action> post)
{
    private CancellationTokenSource? _cancel;
    private int _percent = -1;

    public string Dir => dir;

    public bool Ready => VoiceModel.IsReady(dir);

    public bool Downloading => _cancel is not null;

    /// <summary>Bytes on disk so far, while downloading.</summary>
    public long Done { get; private set; }

    public long Total { get; } = VoiceModel.TotalBytes(VoiceModel.Files);

    /// <summary>Why the last download stopped, until the next one starts. Null after a cancel.</summary>
    public string? Error { get; private set; }

    /// <summary>The state above changed: once per percent while downloading.</summary>
    public event Action? Changed;

    public async void Start()
    {
        if (Downloading || Ready) return;
        var cancel = _cancel = new CancellationTokenSource();
        Error = null;
        Done = 0;
        _percent = -1;
        Changed?.Invoke();
        try
        {
            // Off the UI thread: hashing 640 MB there would freeze the window.
            await Task.Run(() => VoiceModel.DownloadAsync(dir, VoiceModel.FromWeb, new Counter(this, cancel), cancel.Token));
        }
        catch (OperationCanceledException) { }
        catch (VoiceModelException e) { Error = e.Message; }
        finally
        {
            _cancel = null;
            cancel.Dispose();
            Changed?.Invoke();
        }
    }

    /// <summary>Stops the download. What arrived stays, and the next Start carries on from it.</summary>
    public void Cancel() => _cancel?.Cancel();

    /// <summary>Deletes the model. The caller lets it out of memory first (VoiceInput.Unload).</summary>
    public void Remove()
    {
        if (Downloading) return;
        VoiceModel.Remove(dir);
        Error = null;
        Changed?.Invoke();
    }

    // Called on the download's thread for every block read: only a new percent goes to the UI.
    private sealed class Counter(VoiceSetup setup, CancellationTokenSource run) : IProgress<long>
    {
        public void Report(long done)
        {
            int percent = (int)(done * 100 / Math.Max(setup.Total, 1));
            if (Interlocked.Exchange(ref setup._percent, percent) == percent) return;
            setup.Post(() =>
            {
                if (!ReferenceEquals(setup._cancel, run)) return;
                setup.Done = done;
                setup.Changed?.Invoke();
            });
        }
    }

    private void Post(Action action) => post(action);
}

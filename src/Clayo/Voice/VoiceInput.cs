using System.Diagnostics;

namespace CcxShell.Core;

/// <summary>
/// Dictation as the app uses it: does what VoiceFlow decides with the microphone and the model.
/// Made and called on the UI thread, and its events are raised there (post is how:
/// Dispatcher.BeginInvoke). The window types the text; the mic button and the island show the state.
/// </summary>
public sealed class VoiceInput : IDisposable
{
    private readonly VoiceFlow _flow = new();
    private readonly VoiceRecorder _recorder = new();
    private readonly VoiceTranscriber _transcriber;
    private readonly Action<Action> _post;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Timer _limit;

    public VoiceInput(string modelDir, Action<Action> post)
    {
        _post = post;
        _transcriber = new VoiceTranscriber(modelDir);
        _limit = new Timer(_ => _post(() => Act(_flow.Tick(_clock.ElapsedMilliseconds))));
    }

    public VoiceState State => _flow.State;

    /// <summary>The session being dictated to (the pane), while not idle.</summary>
    public object? Target => _flow.Target;

    /// <summary>Listening started or ended, or the text is being made.</summary>
    public event Action? Changed;

    /// <summary>A take became text, for this target. Never empty.</summary>
    public event Action<object, string>? Heard;

    /// <summary>Something to tell the user in a few words: no microphone, silence, a model that won't load.</summary>
    public event Action<string>? Problem;

    /// <summary>The mic button or the hotkey: start listening for this target, or stop and transcribe.</summary>
    public void Toggle(object target) => Act(_flow.Toggle(target, _clock.ElapsedMilliseconds));

    /// <summary>Esc: stop listening and type nothing.</summary>
    public void Cancel() => Act(_flow.Cancel());

    /// <summary>Lets the model out of memory: its files are about to be removed.</summary>
    public void Unload() => _transcriber.Unload();

    private void Act(VoiceStep step)
    {
        switch (step)
        {
            case VoiceStep.Start:
                try { _recorder.Start(); }
                catch (Exception)
                {
                    _flow.Done();
                    Changed?.Invoke();
                    Problem?.Invoke(VoiceRecorder.HasMicrophone ? "Couldn't open the microphone" : "No microphone found");
                    return;
                }
                // Loaded while you speak, so the first take waits no longer than the others.
                _transcriber.Warm();
                _limit.Change(1000, 1000);
                Changed?.Invoke();
                break;

            case VoiceStep.Stop:
                _limit.Change(Timeout.Infinite, Timeout.Infinite);
                var target = _flow.Target!;
                var (samples, peak) = _recorder.Stop();
                Changed?.Invoke();
                _ = Transcribe(target, samples, peak);
                break;

            case VoiceStep.Drop:
                _limit.Change(Timeout.Infinite, Timeout.Infinite);
                _recorder.Stop();
                Changed?.Invoke();
                break;
        }
    }

    private async Task Transcribe(object target, float[] samples, float peak)
    {
        string? text = null;
        string? problem = null;
        if (peak < VoiceFlow.SilentPeak) problem = "Heard nothing: is the microphone on?";
        else
        {
            try
            {
                text = VoiceFlow.Clean(await _transcriber.Transcribe(samples));
                if (text.Length == 0) problem = "Didn't catch that";
            }
            catch (Exception e)
            {
                problem = $"Voice couldn't start: {e.Message}";
            }
        }

        // Back on the UI thread: the await above resumes there, as this was called from it.
        // What it led to before Changed, so the island can show it where the listening pill was.
        _flow.Done();
        if (problem is not null) Problem?.Invoke(problem);
        else Heard?.Invoke(target, text!);
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _limit.Dispose();
        _recorder.Dispose();
        _transcriber.Dispose();
    }
}

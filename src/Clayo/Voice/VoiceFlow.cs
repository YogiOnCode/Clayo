using System.Text;

namespace CcxShell.Core;

public enum VoiceState { Idle, Listening, Transcribing }

/// <summary>What the caller does after a Toggle, Cancel or Tick: the flow only decides.</summary>
public enum VoiceStep
{
    None,

    /// <summary>Open the microphone.</summary>
    Start,

    /// <summary>Close the microphone and turn what it heard into text.</summary>
    Stop,

    /// <summary>Close the microphone and throw away what it heard.</summary>
    Drop
}

/// <summary>
/// Dictation's turns: the mic button or Ctrl+Alt+M starts listening, the same again stops and
/// transcribes, Esc throws it away. Kept free of WPF, the microphone and the model so
/// checks/voice.cs can drive it with a made-up clock; VoiceInput does what it says.
/// </summary>
public sealed class VoiceFlow
{
    /// <summary>A microphone left open stops by itself after this.</summary>
    public const long MaxMs = 120_000;

    /// <summary>
    /// Loudest sample, 0..1, under which a take is silence: a muted or wrong microphone. The
    /// model makes words up out of silence, so such a take is never transcribed.
    /// </summary>
    public const float SilentPeak = 0.02f;

    private long _since;

    public VoiceState State { get; private set; }

    /// <summary>Whatever identifies the session the text is for (the pane). Null while idle.</summary>
    public object? Target { get; private set; }

    /// <summary>
    /// The mic button or the hotkey. While a take is being transcribed a press does nothing:
    /// its text is about to be typed, and a second take would arrive out of order.
    /// </summary>
    public VoiceStep Toggle(object target, long nowMs)
    {
        switch (State)
        {
            case VoiceState.Idle:
                State = VoiceState.Listening;
                Target = target;
                _since = nowMs;
                return VoiceStep.Start;
            case VoiceState.Listening:
                State = VoiceState.Transcribing;
                return VoiceStep.Stop;
            default:
                return VoiceStep.None;
        }
    }

    /// <summary>Esc. Only a take still being spoken can be thrown away.</summary>
    public VoiceStep Cancel()
    {
        if (State != VoiceState.Listening) return VoiceStep.None;
        Reset();
        return VoiceStep.Drop;
    }

    /// <summary>Called now and then while listening: stops a take that reached the limit.</summary>
    public VoiceStep Tick(long nowMs) =>
        State == VoiceState.Listening && nowMs - _since >= MaxMs ? Toggle(Target!, nowMs) : VoiceStep.None;

    /// <summary>The text is ready or could not be made, or the microphone failed. Idle again.</summary>
    public void Done() => Reset();

    private void Reset()
    {
        State = VoiceState.Idle;
        Target = null;
    }

    /// <summary>
    /// The model's text as it is typed at a prompt: one line, single spaces, and nothing a
    /// terminal would act on (a control character typed there is a key press). Empty when
    /// nothing was said.
    /// </summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var clean = new StringBuilder(text.Length);
        bool space = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c)) { space = true; continue; }
            if (space && clean.Length > 0) clean.Append(' ');
            space = false;
            clean.Append(c);
        }
        return clean.ToString();
    }

    /// <summary>The first words of what was typed, for the island: whole words up to max characters.</summary>
    public static string Preview(string text, int max = 44)
    {
        if (text.Length <= max) return text;
        int cut = text.LastIndexOf(' ', max);
        return text[..(cut > max / 2 ? cut : max)] + "…";
    }
}

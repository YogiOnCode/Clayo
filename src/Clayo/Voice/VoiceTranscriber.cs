using System.IO;
using SherpaOnnx;

namespace CcxShell.Core;

/// <summary>
/// Speech to text on this PC, with nothing sent anywhere: Parakeet TDT 0.6B v3 (int8) through
/// sherpa-onnx, on the CPU. The model is large in memory, so it is loaded at the first take and
/// let go again once voice has not been used for a while.
/// </summary>
public sealed class VoiceTranscriber : IDisposable
{
    /// <summary>Unused this long, the model leaves memory. The next take loads it again.</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(10);

    private readonly string _dir;
    private readonly object _gate = new();
    private readonly Timer _idle;
    private OfflineRecognizer? _recognizer;
    private DateTime _usedAt;
    private bool _disposed;

    public VoiceTranscriber(string dir)
    {
        _dir = dir;
        _idle = new Timer(_ => LetGo(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public bool Loaded { get { lock (_gate) return _recognizer is not null; } }

    /// <summary>
    /// Loads the model ahead of the first take, while you are still speaking. Failures wait
    /// for Transcribe to report them.
    /// </summary>
    public void Warm() => Task.Run(() =>
    {
        try { lock (_gate) Ready(); }
        catch (Exception) { }
    });

    /// <summary>The text of a take, on a background thread. One at a time.</summary>
    public Task<string> Transcribe(float[] samples) => Task.Run(() =>
    {
        lock (_gate)
        {
            var text = Decode(Ready(), samples);
            _usedAt = DateTime.UtcNow;
            return text;
        }
    });

    private OfflineRecognizer Ready()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_recognizer is { } loaded) return loaded;

        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = VoiceRecorder.Rate;
        config.ModelConfig.Transducer.Encoder = Path.Combine(_dir, "encoder.int8.onnx");
        config.ModelConfig.Transducer.Decoder = Path.Combine(_dir, "decoder.int8.onnx");
        config.ModelConfig.Transducer.Joiner = Path.Combine(_dir, "joiner.int8.onnx");
        config.ModelConfig.Tokens = Path.Combine(_dir, "tokens.txt");
        config.ModelConfig.ModelType = "nemo_transducer";
        config.ModelConfig.NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.Debug = 0;
        config.DecodingMethod = "greedy_search";

        var recognizer = new OfflineRecognizer(config);
        // The first decode sets up buffers inside onnxruntime; done here it is not felt later.
        Decode(recognizer, new float[VoiceRecorder.Rate]);
        _usedAt = DateTime.UtcNow;
        return _recognizer = recognizer;
    }

    private static string Decode(OfflineRecognizer recognizer, float[] samples)
    {
        using var stream = recognizer.CreateStream();
        stream.AcceptWaveform(VoiceRecorder.Rate, samples);
        recognizer.Decode(stream);
        return stream.Result.Text;
    }

    private void LetGo()
    {
        // Not while a take is being decoded: the timer would only wait behind it.
        if (!Monitor.TryEnter(_gate)) return;
        try
        {
            if (_recognizer is null || DateTime.UtcNow - _usedAt < KeepFor) return;
            _recognizer.Dispose();
            _recognizer = null;
        }
        finally { Monitor.Exit(_gate); }
    }

    /// <summary>Lets the model go now: its files are about to be removed.</summary>
    public void Unload()
    {
        lock (_gate)
        {
            _recognizer?.Dispose();
            _recognizer = null;
        }
    }

    public void Dispose()
    {
        _idle.Dispose();
        lock (_gate)
        {
            _disposed = true;
            _recognizer?.Dispose();
            _recognizer = null;
        }
    }
}

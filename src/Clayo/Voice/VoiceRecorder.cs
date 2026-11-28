using NAudio.Wave;

namespace CcxShell.Core;

/// <summary>
/// The default microphone as 16 kHz mono samples, which is what the model takes; Windows
/// converts from the microphone's own format. Held in memory only: what you say is never
/// written to disk.
/// </summary>
public sealed class VoiceRecorder : IDisposable
{
    public const int Rate = 16000;

    private readonly object _lock = new();
    private List<float> _samples = [];
    private WaveIn? _mic;
    private float _peak;

    public static bool HasMicrophone
    {
        get
        {
            try { return WaveIn.DeviceCount > 0; }
            catch (Exception) { return false; }
        }
    }

    /// <summary>Opens the microphone. Throws when there is none or Windows refuses it.</summary>
    public void Start()
    {
        Stop();
        lock (_lock)
        {
            _samples = new List<float>(Rate * 30);
            _peak = 0;
        }
        var mic = new WaveIn { DeviceNumber = -1, WaveFormat = new WaveFormat(Rate, 16, 1), BufferMilliseconds = 50 };
        mic.DataAvailable += (_, e) =>
        {
            lock (_lock)
                for (int i = 0; i + 1 < e.BytesRecorded; i += 2)
                {
                    float sample = BitConverter.ToInt16(e.Buffer, i) / 32768f;
                    _samples.Add(sample);
                    float loud = Math.Abs(sample);
                    if (loud > _peak) _peak = loud;
                }
        };
        try { mic.StartRecording(); }
        catch
        {
            mic.Dispose();
            throw;
        }
        _mic = mic;
    }

    /// <summary>Closes the microphone and hands over what it heard, with the loudest sample (0..1).</summary>
    public (float[] Samples, float Peak) Stop()
    {
        if (_mic is { } mic)
        {
            _mic = null;
            try { mic.StopRecording(); }
            catch (Exception) { /* already gone */ }
            mic.Dispose();
        }
        lock (_lock)
        {
            var take = (_samples.ToArray(), _peak);
            _samples = [];
            _peak = 0;
            return take;
        }
    }

    public void Dispose() => Stop();
}

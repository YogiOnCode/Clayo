#:property TargetFramework=net10.0-windows
#:project ../../src/Clayo/CcxShell.csproj
using System.Diagnostics;
using CcxShell.Core;

// Is the speech model still where Settings › Voice downloads it from, and still the bytes
// Clayo expects? The real download (VoiceModel.DownloadAsync from VoiceModel.Source), hashes
// checked, into a temp folder, then the model loaded once. 640 MB over the network, so not one
// of the checks in checks\ that run on every pull request: .github/workflows/voice-model.yml
// runs it once a week, and whenever the source or the hashes change.
int fail = 0;
void Check<T>(string name, T got, T want)
{
    var ok = EqualityComparer<T>.Default.Equals(got, want);
    if (!ok) fail++;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}  got={got} want={want}");
}

var dir = Directory.CreateTempSubdirectory("clayo-voice-model").FullName;
try
{
    Console.WriteLine($"From {VoiceModel.Source}");
    var clock = Stopwatch.StartNew();
    string error = "";
    try { await VoiceModel.DownloadAsync(dir, VoiceModel.FromWeb, null, CancellationToken.None); }
    catch (VoiceModelException e) { error = e.Message; }
    Check($"every file downloads and matches its hash ({clock.Elapsed.TotalSeconds:0} s)", error, "");
    Check("the model is ready", VoiceModel.IsReady(dir), true);

    if (fail == 0)
    {
        // A second of silence: the files are a model sherpa-onnx can load and run.
        using var transcriber = new VoiceTranscriber(dir);
        string heard;
        try { heard = VoiceFlow.Clean(await transcriber.Transcribe(new float[VoiceRecorder.Rate])); }
        catch (Exception e) { heard = $"{e.GetType().Name}: {e.Message}"; }
        Check("it loads, and hears nothing in silence", heard, "");
    }
}
finally
{
    try { Directory.Delete(dir, recursive: true); }
    catch (IOException) { }
}

Console.WriteLine(fail == 0 ? "\nall checks passed" : $"\n{fail} FAILED");
return fail;

#:property TargetFramework=net10.0-windows
#:project ../src/Clayo/CcxShell.csproj
using System.Security.Cryptography;
using CcxShell.Core;

// Voice input: the turns of a dictation (VoiceFlow) with a made-up clock, the text as it is
// typed, and the model download (VoiceModel) with made-up files in a temp folder. No
// microphone, no model, no network.
int fail = 0;
void Check<T>(string name, T got, T want)
{
    var ok = EqualityComparer<T>.Default.Equals(got, want);
    if (!ok) fail++;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}  got={got} want={want}");
}

// ---- the turns
var pane = new object();
var other = new object();

var flow = new VoiceFlow();
Check("idle to begin with", (flow.State, flow.Target), (VoiceState.Idle, (object?)null));
Check("a press starts listening", flow.Toggle(pane, 0), VoiceStep.Start);
Check("  for that session", (flow.State, flow.Target), (VoiceState.Listening, (object?)pane));
Check("a second press stops and transcribes", flow.Toggle(pane, 5000), VoiceStep.Stop);
Check("  still that session's", (flow.State, flow.Target), (VoiceState.Transcribing, (object?)pane));
Check("a press while transcribing does nothing", flow.Toggle(other, 5100), VoiceStep.None);
Check("  and the take stays the first session's", flow.Target, (object?)pane);
Check("Esc while transcribing does nothing", flow.Cancel(), VoiceStep.None);
flow.Done();
Check("idle once the text is typed", (flow.State, flow.Target), (VoiceState.Idle, (object?)null));

flow = new VoiceFlow();
flow.Toggle(pane, 0);
Check("the press from another pane stops the take too", flow.Toggle(other, 4000), VoiceStep.Stop);
Check("  and the text goes where it was started", flow.Target, (object?)pane);

flow = new VoiceFlow();
flow.Toggle(pane, 0);
Check("Esc while listening throws the take away", flow.Cancel(), VoiceStep.Drop);
Check("  idle again", flow.State, VoiceState.Idle);
Check("Esc while idle does nothing", flow.Cancel(), VoiceStep.None);

flow = new VoiceFlow();
flow.Toggle(pane, 1000);
Check("before the limit: still listening", flow.Tick(1000 + VoiceFlow.MaxMs - 1), VoiceStep.None);
Check("at the limit: stops by itself", flow.Tick(1000 + VoiceFlow.MaxMs), VoiceStep.Stop);
Check("  and is transcribed", flow.State, VoiceState.Transcribing);
Check("a tick while transcribing does nothing", flow.Tick(1000 + 2 * VoiceFlow.MaxMs), VoiceStep.None);
flow.Done();
Check("a tick while idle does nothing", flow.Tick(long.MaxValue), VoiceStep.None);

// ---- the text
Check("trimmed", VoiceFlow.Clean("  Fix the login test.  "), "Fix the login test.");
Check("one line", VoiceFlow.Clean("first line\r\nsecond\tline"), "first line second line");
Check("nothing a terminal would act on", VoiceFlow.Clean("run\u001b[2Jthis\u0003 now\r"), "run [2Jthis now");
Check("nothing said", VoiceFlow.Clean(" \n "), "");
Check("null is nothing said", VoiceFlow.Clean(null), "");
Check("other languages pass", VoiceFlow.Clean("Grüße aus Köln"), "Grüße aus Köln");
Check("a short text previews whole", VoiceFlow.Preview("Fix the login test."), "Fix the login test.");
Check("a long one is cut at a word", VoiceFlow.Preview("please rename the settings page and move the voice switch to the top", 30), "please rename the settings…");
Check("one long word is cut where it must be", VoiceFlow.Preview(new string('a', 60), 10), new string('a', 10) + "…");

// ---- the model
var dir = Directory.CreateTempSubdirectory("clayo-voice").FullName;
try
{
    var bytes = new Dictionary<string, byte[]>
    {
        ["tokens.txt"] = Bytes(1, 300),
        ["encoder.onnx"] = Bytes(2, 400_000),
    };
    var files = bytes.Select(b => new VoiceFile(b.Key, b.Value.Length, Convert.ToHexString(SHA256.HashData(b.Value)))).ToList();
    var asked = new List<(string Name, long From)>();
    bool ranges = true;
    VoiceModel.Opener open = (file, from, _) =>
    {
        asked.Add((file.Name, from));
        long start = ranges ? from : 0;
        return Task.FromResult(((Stream)new MemoryStream(bytes[file.Name][(int)start..]), start));
    };
    var none = CancellationToken.None;
    string At(string name) => Path.Combine(dir, name);

    Check("the real model is four files", VoiceModel.Files.Count, 4);
    Check("  670 MB (640 as Explorer counts it)", VoiceModel.TotalBytes(VoiceModel.Files) / 1_000_000, 670L);
    Check("an empty folder is not ready", VoiceModel.IsReady(dir, files), false);
    Check("a folder that isn't there is not ready", VoiceModel.IsReady(At("none"), files), false);

    long last = 0;
    await VoiceModel.DownloadAsync(dir, open, new Sync(v => last = v), none, files);
    Check("downloaded: ready", VoiceModel.IsReady(dir, files), true);
    Check("  the files as sent", File.ReadAllBytes(At("encoder.onnx")).SequenceEqual(bytes["encoder.onnx"]), true);
    Check("  no .part left", Directory.GetFiles(dir, "*.part").Length, 0);
    Check("  progress ends at the total", last, VoiceModel.TotalBytes(files));

    asked.Clear();
    await VoiceModel.DownloadAsync(dir, open, null, none, files);
    Check("already there: nothing is fetched", asked.Count, 0);

    // A download that stopped part way is carried on from where it was.
    File.Delete(At("encoder.onnx"));
    File.WriteAllBytes(At("encoder.onnx.part"), bytes["encoder.onnx"][..100_000]);
    Check("a .part alone is not ready", VoiceModel.IsReady(dir, files), false);
    asked.Clear();
    last = 0;
    var firstSeen = -1L;
    await VoiceModel.DownloadAsync(dir, open, new Sync(v => { if (firstSeen < 0) firstSeen = v; last = v; }), none, files);
    Check("a .part is carried on from its end", asked.SequenceEqual([("encoder.onnx", 100_000L)]), true);
    Check("  to the whole file", File.ReadAllBytes(At("encoder.onnx")).SequenceEqual(bytes["encoder.onnx"]), true);
    Check("  progress starts past what was here", firstSeen > 300 + 100_000, true);

    // A server that ignores the range sends the file from its start.
    File.Delete(At("encoder.onnx"));
    File.WriteAllBytes(At("encoder.onnx.part"), bytes["encoder.onnx"][..100_000]);
    ranges = false;
    await VoiceModel.DownloadAsync(dir, open, null, none, files);
    Check("the whole file sent anyway: still right", File.ReadAllBytes(At("encoder.onnx")).SequenceEqual(bytes["encoder.onnx"]), true);
    ranges = true;

    // A .part that is not this file's start (wrong bytes) fails the hash and is thrown away.
    File.Delete(At("encoder.onnx"));
    File.WriteAllBytes(At("encoder.onnx.part"), new byte[100_000]);
    Check("a wrong .part: the download says so", await Throws(() => VoiceModel.DownloadAsync(dir, open, null, none, files)), "encoder.onnx did not arrive whole. Try the download again.");
    Check("  it is deleted", File.Exists(At("encoder.onnx.part")), false);
    Check("  nothing took its real name", File.Exists(At("encoder.onnx")), false);
    await VoiceModel.DownloadAsync(dir, open, null, none, files);
    Check("  and the next try gets it", VoiceModel.IsReady(dir, files), true);

    // A file of the right name and the wrong size is fetched again.
    File.WriteAllBytes(At("tokens.txt"), [1, 2, 3]);
    Check("a short file is not ready", VoiceModel.IsReady(dir, files), false);
    await VoiceModel.DownloadAsync(dir, open, null, none, files);
    Check("  fetched again", File.ReadAllBytes(At("tokens.txt")).SequenceEqual(bytes["tokens.txt"]), true);

    // A bigger .part than the file is begun again.
    File.Delete(At("tokens.txt"));
    File.WriteAllBytes(At("tokens.txt.part"), new byte[5000]);
    asked.Clear();
    await VoiceModel.DownloadAsync(dir, open, null, none, files);
    Check("a .part longer than the file starts over", asked.SequenceEqual([("tokens.txt", 0L)]), true);

    // A server that keeps sending past the file's size is cut off, not written to disk.
    File.Delete(At("tokens.txt"));
    VoiceModel.Opener endless = (file, from, _) => Task.FromResult(((Stream)new MemoryStream(new byte[5_000_000]), 0L));
    Check("more than the file: refused", await Throws(() => VoiceModel.DownloadAsync(dir, endless, null, none, files)), "tokens.txt did not arrive whole. Try the download again.");
    Check("  and nothing kept", Directory.GetFiles(dir, "tokens.txt*").Length, 0);
    await VoiceModel.DownloadAsync(dir, open, null, none, files);

    // The connection drops, or you cancel: what arrived stays for the next try.
    File.Delete(At("encoder.onnx"));
    VoiceModel.Opener drops = (file, from, _) =>
        Task.FromResult(((Stream)new Breaks(bytes[file.Name], 150_000), 0L));
    Check("a dropped connection: the download says so",
        (await Throws(() => VoiceModel.DownloadAsync(dir, drops, null, none, files))).StartsWith("The download stopped: "), true);
    Check("  what arrived is kept", new FileInfo(At("encoder.onnx.part")).Length > 0, true);
    await VoiceModel.DownloadAsync(dir, open, null, none, files);
    Check("  and the next try finishes it", VoiceModel.IsReady(dir, files), true);

    File.Delete(At("encoder.onnx"));
    using (var stop = new CancellationTokenSource())
    {
        VoiceModel.Opener slow = (file, from, _) =>
            Task.FromResult(((Stream)new Breaks(bytes[file.Name], 150_000, stop.Cancel), 0L));
        bool cancelled = false;
        try { await VoiceModel.DownloadAsync(dir, slow, null, stop.Token, files); }
        catch (OperationCanceledException) { cancelled = true; }
        Check("Cancel ends it as a cancel, not an error", cancelled, true);
        Check("  not ready", VoiceModel.IsReady(dir, files), false);
    }

    // A server that goes silent mid-file.
    var quiet = VoiceModel.QuietFor;
    VoiceModel.QuietFor = TimeSpan.FromMilliseconds(200);
    VoiceModel.Opener hangs = (file, from, _) => Task.FromResult(((Stream)new Hangs(), 0L));
    Check("a silent connection: given up on",
        await Throws(() => VoiceModel.DownloadAsync(dir, hangs, null, none, files)),
        "The download stopped: the connection went quiet. Try again to carry on from here.");
    VoiceModel.QuietFor = quiet;

    await VoiceModel.DownloadAsync(dir, open, null, none, files);
    File.WriteAllText(At("notes.txt"), "");
    File.WriteAllBytes(At("tokens.txt.part"), [1]);
    VoiceModel.Remove(dir, files);
    Check("Remove deletes the model's files", (File.Exists(At("tokens.txt")), File.Exists(At("encoder.onnx"))), (false, false));
    Check("  a half-downloaded one too", File.Exists(At("tokens.txt.part")), false);
    Check("  and nothing else", File.Exists(At("notes.txt")), true);
    Check("  a folder that isn't there: nothing to do", Try(() => VoiceModel.Remove(At("none"), files)), true);
}
finally { Directory.Delete(dir, recursive: true); }

Console.WriteLine(fail == 0 ? "\nall checks passed" : $"\n{fail} FAILED");
return fail;

static byte[] Bytes(int seed, int length)
{
    var bytes = new byte[length];
    new Random(seed).NextBytes(bytes);
    return bytes;
}

static bool Try(Action a) { try { a(); return true; } catch { return false; } }

static async Task<string> Throws(Func<Task> run)
{
    try { await run(); return "(no error)"; }
    catch (VoiceModelException e) { return e.Message; }
}

// Progress<T> posts to the thread pool, so its last value can arrive after the check reads it.
sealed class Sync(Action<long> to) : IProgress<long>
{
    public void Report(long value) => to(value);
}

// Sends the first `until` bytes, then fails like a lost connection (or calls `then` first).
sealed class Breaks(byte[] data, int until, Action? then = null) : MemoryStream(data)
{
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancel = default)
    {
        if (Position >= until)
        {
            then?.Invoke();
            cancel.ThrowIfCancellationRequested();
            throw new IOException("connection lost");
        }
        return await base.ReadAsync(buffer[..(int)Math.Min(buffer.Length, until - Position)], cancel);
    }
}

// Never sends a byte.
sealed class Hangs : MemoryStream
{
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancel = default)
    {
        await Task.Delay(Timeout.Infinite, cancel);
        return 0;
    }
}

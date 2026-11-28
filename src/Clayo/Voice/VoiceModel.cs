using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace CcxShell.Core;

/// <summary>One file of the speech model: its size and SHA-256 as published.</summary>
public sealed record VoiceFile(string Name, long Size, string Sha256);

/// <summary>A model file could not be fetched or was not the file expected. The message is for the user.</summary>
public sealed class VoiceModelException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The speech model on disk: Parakeet TDT 0.6B v3 (int8), 641 MB, in
/// %LOCALAPPDATA%\Clayo\models. Too big for the release zip, so Settings › Voice downloads it
/// once, file by file, each checked against the hash below before it gets its real name. No WPF.
/// </summary>
public static class VoiceModel
{
    public const string Name = "parakeet-tdt-0.6b-v3-int8";

    /// <summary>
    /// The plain files, not the release's .tar.bz2: .NET reads no bzip2. At one commit of the
    /// repository rather than its main branch, so what is fetched cannot change under the
    /// hashes below.
    /// </summary>
    public const string Source = "https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8/resolve/ed5d745623472f97cfc2e6c2916ee248798fc5a8/";

    public static readonly IReadOnlyList<VoiceFile> Files =
    [
        new("tokens.txt", 93_939, "d58544679ea4bc6ac563d1f545eb7d474bd6cfa467f0a6e2c1dc1c7d37e3c35d"),
        new("joiner.int8.onnx", 6_355_277, "cbd02f3696e9944970111fb439d9c3fd365a80374565f13f2b9c7007d253fc98"),
        new("decoder.int8.onnx", 11_845_274, "0222a86147299d4dee90edf95b6d13827edb42ad8b7c9cf0b2582f030937c0b5"),
        new("encoder.int8.onnx", 652_282_294, "1afbe69c1ffdabee94755f42e4daf1d93549131a3da11356bda84fcef0f2c479"),
    ];

    public static string DefaultDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clayo", "models", Name);

    /// <summary>A download that receives nothing for this long has stalled.</summary>
    public static TimeSpan QuietFor { get; set; } = TimeSpan.FromSeconds(45);

    public static long TotalBytes(IEnumerable<VoiceFile> files) => files.Sum(f => f.Size);

    /// <summary>
    /// Every file is there at its full size. The hash was checked when it was downloaded;
    /// reading 641 MB again at every start would cost seconds for nothing.
    /// </summary>
    public static bool IsReady(string dir, IEnumerable<VoiceFile>? files = null) =>
        (files ?? Files).All(f => Have(dir, f));

    private static bool Have(string dir, VoiceFile f)
    {
        try { return new FileInfo(Path.Combine(dir, f.Name)) is { Exists: true } info && info.Length == f.Size; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Opens a file of the model from <paramref name="from"/> bytes in. Returns where the body
    /// really starts: 0 when the server sent the whole file anyway.
    /// </summary>
    public delegate Task<(Stream Body, long From)> Opener(VoiceFile file, long from, CancellationToken cancel);

    /// <summary>
    /// Fetches the files still missing. Each goes to name.part and is renamed once its hash
    /// matches, so a file with its real name is always whole; a .part left by a cancel or a lost
    /// connection is carried on from. progress gets the bytes on disk so far, of all files.
    /// </summary>
    public static async Task DownloadAsync(string dir, Opener open, IProgress<long>? progress, CancellationToken cancel,
        IReadOnlyList<VoiceFile>? files = null)
    {
        files ??= Files;
        try
        {
            Directory.CreateDirectory(dir);
            long done = files.Where(f => Have(dir, f)).Sum(f => f.Size);
            foreach (var file in files)
            {
                if (Have(dir, file)) continue;
                var path = Path.Combine(dir, file.Name);
                var part = path + ".part";

                // Longer than the file is not this file's start: begin again.
                long have = File.Exists(part) ? new FileInfo(part).Length : 0;
                if (have > file.Size) have = 0;

                if (have < file.Size)
                {
                    // A connection that goes silent never ends a read by itself.
                    using var quiet = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                    try
                    {
                        quiet.CancelAfter(QuietFor);
                        var (body, from) = await open(file, have, quiet.Token);
                        await using (body)
                        await using (var to = new FileStream(part, from == 0 ? FileMode.Create : FileMode.Open, FileAccess.Write))
                        {
                            to.Seek(from, SeekOrigin.Begin);
                            to.SetLength(from);
                            var buffer = new byte[128 * 1024];
                            long at = from;
                            int read;
                            while ((read = await body.ReadAsync(buffer, quiet.Token)) > 0)
                            {
                                quiet.CancelAfter(QuietFor);
                                at += read;
                                // Not this file: stop before it fills the disk.
                                if (at > file.Size) break;
                                await to.WriteAsync(buffer.AsMemory(0, read), cancel);
                                progress?.Report(done + at);
                            }
                        }
                    }
                    catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
                    {
                        throw new VoiceModelException("The download stopped: the connection went quiet. Try again to carry on from here.");
                    }
                }

                if (!await Matches(part, file, cancel))
                {
                    File.Delete(part);
                    throw new VoiceModelException($"{file.Name} did not arrive whole. Try the download again.");
                }
                File.Move(part, path, overwrite: true);
                done += file.Size;
                progress?.Report(done);
            }
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            throw new VoiceModelException($"The download stopped: {e.Message}", e);
        }
    }

    private static async Task<bool> Matches(string path, VoiceFile file, CancellationToken cancel)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        if (stream.Length != file.Size) return false;
        var hash = await SHA256.HashDataAsync(stream, cancel);
        return Convert.ToHexString(hash).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Deletes the model's files, half-downloaded ones included. Best effort.</summary>
    public static void Remove(string dir, IEnumerable<VoiceFile>? files = null)
    {
        foreach (var file in files ?? Files)
            foreach (var path in new[] { Path.Combine(dir, file.Name), Path.Combine(dir, file.Name + ".part") })
                try { File.Delete(path); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // No overall timeout: the encoder alone can take many minutes. A connection that goes
    // silent is the caller's cancel, or the read failing.
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>The real opener: the file from Source, resumed with a Range request when some of it is here.</summary>
    public static async Task<(Stream Body, long From)> FromWeb(VoiceFile file, long from, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Source + file.Name);
        if (from > 0) request.Headers.Range = new RangeHeaderValue(from, null);
        var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
        try
        {
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadAsStreamAsync(cancel), response.StatusCode == HttpStatusCode.PartialContent ? from : 0);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }
}

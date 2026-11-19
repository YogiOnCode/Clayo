using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CcxShell.Core;

/// <summary>A GitHub login found on this PC (D15): where it came from, and its token.</summary>
public sealed record GitHubLogin(string Source, string Token);

/// <summary>GitHub said no. Status is the HTTP status: 401/403 a login that can't, 404 a gist gone.</summary>
public sealed class GitHubException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>
/// A public link to a page (D14): a secret gist with a gistpreview or githack preview. The
/// login is one the user already has, from gh or git's credential manager; Clayo keeps no
/// token. No WPF.
/// </summary>
public static partial class GitHubGist
{
    [GeneratedRegex("^[0-9a-f]{20,32}$")]
    private static partial Regex GistIdShape();

    /// <summary>
    /// Whether an id has a gist id's shape. One read back from shares.json goes into an API path,
    /// so "../repos/…" must never get there.
    /// </summary>
    public static bool IsGistId(string id) => GistIdShape().IsMatch(id);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>
    /// The link that shows the gist's file as a page: gistpreview reads the gist from GitHub's
    /// API on each open, so an update or a removal shows within a minute or two (GitHub caches
    /// the answer 60 s). It uses the file's content, which the API gives only up to 1 MB.
    /// </summary>
    public static string PreviewUrl(string id, string file) =>
        $"https://gistpreview.github.io/?{id}/{Uri.EscapeDataString(file)}";

    /// <summary>The link for a page too large for PreviewUrl. githack keeps its own copy: updates and removals show late.</summary>
    public static string CachedPreviewUrl(string owner, string id, string file) =>
        $"https://gist.githack.com/{owner}/{id}/raw/{Uri.EscapeDataString(file)}";

    /// <summary>The largest page, in bytes, that gets PreviewUrl, under the API's 1 MB with room to spare.</summary>
    public const int LiveMax = 900 * 1024;

    public static bool FitsLive(string html) => Encoding.UTF8.GetByteCount(html) <= LiveMax;

    /// <summary>gh's token, else git's saved one for github.com, never a prompt; null when neither has one.</summary>
    public static async Task<GitHubLogin?> FindLogin()
    {
        // github.com's own token: gh's default host may be a GitHub Enterprise one.
        if (await Run("gh", "auth token --hostname github.com", null) is { Length: > 0 } gh) return new("gh", gh.Trim());
        var fill = await Run("git", "credential fill", "protocol=https\nhost=github.com\n\n");
        var token = fill?.Split('\n').FirstOrDefault(l => l.StartsWith("password="))?["password=".Length..].Trim();
        return token is { Length: > 0 } ? new("git", token) : null;
    }

    /// <summary>The account a token belongs to, and whether the token may make gists (CanGist).</summary>
    public static async Task<(string Login, bool CanGist)> User(string token)
    {
        string? scopes = null;
        var user = (await Send(HttpMethod.Get, "user", token, null, h => scopes = h.TryGetValues("X-OAuth-Scopes", out var v) ? string.Join(",", v) : null))!;
        return ((string)user["login"]!, CanGist(scopes));
    }

    /// <summary>
    /// Whether a token with these scopes (GitHub's X-OAuth-Scopes) may make gists: a classic token
    /// needs the gist scope, without which GitHub answers "Not Found". Fine-grained and app tokens
    /// send no scopes; GitHub decides for those.
    /// </summary>
    public static bool CanGist(string? scopes) =>
        scopes is null || scopes.Split(',').Any(s => s.Trim() == "gist");

    /// <summary>A new secret gist holding the page; its id and owner.</summary>
    public static async Task<(string Id, string Owner)> Create(string token, string file, string html)
    {
        var body = new JsonObject
        {
            ["description"] = "A page made with Clayo",
            ["public"] = false,
            ["files"] = new JsonObject { [file] = new JsonObject { ["content"] = html } }
        };
        var gist = (await Send(HttpMethod.Post, "gists", token, body))!;
        return ((string)gist["id"]!, (string)gist["owner"]!["login"]!);
    }

    /// <summary>
    /// The gist's file replaced with the page as it is now; the link stays. GitHub keeps the
    /// earlier versions in the gist's history, readable by anyone with the link.
    /// </summary>
    public static Task Update(string token, string id, string file, string html) =>
        Send(HttpMethod.Patch, $"gists/{Checked(id)}", token,
            new JsonObject { ["files"] = new JsonObject { [file] = new JsonObject { ["content"] = html } } });

    /// <summary>
    /// Deletes the gist. One already gone is fine: call it only with the login of the gist's
    /// owner (User), as GitHub also says "not found" for a gist that is someone else's.
    /// </summary>
    public static async Task Delete(string token, string id)
    {
        try { await Send(HttpMethod.Delete, $"gists/{Checked(id)}", token, null); }
        catch (GitHubException e) when (e.Status == HttpStatusCode.NotFound) { }
    }

    private static string Checked(string id) =>
        IsGistId(id) ? id : throw new GitHubException(HttpStatusCode.BadRequest, "not a gist id");

    private static readonly Uri Api = new("https://api.github.com/");

    private static async Task<JsonNode?> Send(HttpMethod method, string path, string token, JsonObject? body,
        Action<HttpResponseHeaders>? headers = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(Api, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.UserAgent.ParseAdd("Clayo");
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await Http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        headers?.Invoke(response.Headers);
        if (!response.IsSuccessStatusCode)
        {
            string? message = null;
            try { message = (string?)JsonNode.Parse(text)?["message"]; }
            catch (System.Text.Json.JsonException) { }
            throw new GitHubException(response.StatusCode, message ?? response.ReasonPhrase ?? response.StatusCode.ToString());
        }
        return text.Length > 0 ? JsonNode.Parse(text) : null;
    }

    /// <summary>
    /// A tool's full path from PATH's absolute folders only. Started by bare name, Windows would
    /// look in the current folder first, which is often the project: a repo could hold its own
    /// gh.exe.
    /// </summary>
    public static string? OnPath(string exe, string? path = null)
    {
        foreach (var dir in (path ?? Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var folder = dir.Trim().Trim('"');
            if (folder.Length == 0 || !Path.IsPathFullyQualified(folder)) continue;
            var file = Path.Combine(folder, exe + ".exe");
            if (File.Exists(file)) return file;
        }
        return null;
    }

    // A tool's output, or null when it isn't there, fails or takes too long. Never a window or
    // prompt. Run from Clayo's own folder, so git reads no repo's config (a credential.helper
    // there could run anything).
    private static async Task<string?> Run(string exe, string args, string? input)
    {
        if (OnPath(exe) is not { } file) return null;
        var home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clayo");
        var start = new ProcessStartInfo(file, args)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = home
        };
        start.Environment["GCM_INTERACTIVE"] = "never";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GH_PROMPT_DISABLED"] = "1";
        try
        {
            Directory.CreateDirectory(home);
            using var p = Process.Start(start)!;
            if (input is not null) await p.StandardInput.WriteAsync(input);
            p.StandardInput.Close();
            var output = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await p.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { p.Kill(entireProcessTree: true); return null; }
            return p.ExitCode == 0 ? await output : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException) { return null; }
    }
}

using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CcxShell.Core;

/// <summary>GitHub said no. Status is the HTTP status: 401/403 a login that can't, 404 a gist gone.</summary>
public sealed class GitHubException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>
/// A public link to a page (D14): a secret gist with a gistpreview or githack preview. The
/// login is Clayo's own gist-only sign-in (GitHubSignIn), never one the user has for other
/// things (gh, git). No WPF.
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

    /// <summary>The account a token belongs to.</summary>
    public static async Task<string> User(string token) =>
        (string)(await Send(HttpMethod.Get, "user", token, null))!["login"]!;

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

    private static async Task<JsonNode?> Send(HttpMethod method, string path, string token, JsonObject? body)
    {
        using var request = new HttpRequestMessage(method, new Uri(Api, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.UserAgent.ParseAdd("Clayo");
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await Http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            string? message = null;
            try { message = (string?)JsonNode.Parse(text)?["message"]; }
            catch (System.Text.Json.JsonException) { }
            throw new GitHubException(response.StatusCode, message ?? response.ReasonPhrase ?? response.StatusCode.ToString());
        }
        return text.Length > 0 ? JsonNode.Parse(text) : null;
    }
}

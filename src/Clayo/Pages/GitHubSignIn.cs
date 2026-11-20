using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace CcxShell.Core;

/// <summary>A code GitHub gave for the user to enter at Uri; Device is Clayo's half of it.</summary>
public sealed record DeviceCode(string Device, string User, string Uri, int Interval, DateTime Until);

/// <summary>
/// Clayo's own GitHub token, from the Clayo Pages app (docs/GITHUB-SIGNIN.md): gists only. The
/// access token lasts 8 hours; the refresh token renews it for 6 months.
/// </summary>
public sealed record GitHubToken(string Access, DateTime AccessUntil, string Refresh, DateTime RefreshUntil)
{
    /// <summary>Good for a while yet: not within two minutes of running out.</summary>
    public bool Fresh(DateTime now) => now < AccessUntil - TimeSpan.FromMinutes(2);

    public bool Renewable(DateTime now) => Refresh.Length > 0 && RefreshUntil > now + TimeSpan.FromMinutes(2);

    // Never the tokens themselves, should one ever reach a log or a crash report.
    private bool PrintMembers(System.Text.StringBuilder b)
    {
        b.Append($"AccessUntil = {AccessUntil:o}, RefreshUntil = {RefreshUntil:o}");
        return true;
    }
}

/// <summary>Where a wait for the user stands: a token, still waiting (at Interval), or over (Error).</summary>
public sealed record SignInPoll(GitHubToken? Token, int Interval, string? Error);

/// <summary>
/// Signing in to the Clayo Pages GitHub App with a code (GitHub's device flow): no client
/// secret, nothing to leak. The token is kept with DPAPI for this Windows user only, and goes
/// nowhere but github.com and api.github.com. No WPF.
/// </summary>
public static class GitHubSignIn
{
    /// <summary>The Clayo Pages app's Client ID. Public by design.</summary>
    public const string ClientId = "Iv23li8iMl6UJ6eLfiLo";

    /// <summary>Where the user enters the code.</summary>
    public const string VerifyPage = "https://github.com/login/device";

    /// <summary>Where the user revokes the app on GitHub (Disconnect opens it).</summary>
    public const string AppsPage = "https://github.com/settings/apps/authorizations";

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clayo", "github.bin");

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private const string DeviceGrant = "urn:ietf:params:oauth:grant-type:device_code";

    /// <summary>A new code for the user to enter.</summary>
    public static async Task<DeviceCode> Start()
    {
        var a = await Post("https://github.com/login/device/code", new() { ["client_id"] = ClientId });
        if (Text(a, "error") is { } error) throw new GitHubException(System.Net.HttpStatusCode.BadRequest, error);
        if (Text(a, "device_code") is not { } device || Text(a, "user_code") is not { } user)
            throw new GitHubException(System.Net.HttpStatusCode.BadGateway, "no code");
        // The page to enter it on is always GitHub's own, whatever the answer says.
        return new DeviceCode(device, user, VerifyPage, Math.Max(Number(a, "interval") ?? 5, 5),
            DateTime.UtcNow.AddSeconds(Number(a, "expires_in") ?? 900));
    }

    /// <summary>One look at whether the user approved the code.</summary>
    public static async Task<SignInPoll> Poll(DeviceCode code) =>
        Answer(await Post("https://github.com/login/oauth/access_token", new()
        {
            ["client_id"] = ClientId, ["device_code"] = code.Device, ["grant_type"] = DeviceGrant
        }), code.Interval, DateTime.UtcNow);

    /// <summary>A new token for an old one's refresh token; null when GitHub won't (sign in again).</summary>
    public static async Task<GitHubToken?> Renew(GitHubToken token)
    {
        var a = await Post("https://github.com/login/oauth/access_token", new()
        {
            ["client_id"] = ClientId, ["grant_type"] = "refresh_token", ["refresh_token"] = token.Refresh
        });
        return Answer(a, 5, DateTime.UtcNow).Token;
    }

    /// <summary>
    /// What GitHub's token answer means. authorization_pending waits; slow_down waits longer, as
    /// GitHub says; anything else that isn't a token ends the wait.
    /// </summary>
    public static SignInPoll Answer(JsonObject a, int interval, DateTime now)
    {
        switch (Text(a, "error"))
        {
            case null:
                if (Text(a, "access_token") is not { } access) return new(null, interval, "no_token");
                // The app's tokens expire (docs/GITHUB-SIGNIN.md); 8 hours is GitHub's lifetime for them.
                return new(new GitHubToken(access, now.AddSeconds(Number(a, "expires_in") ?? 28800),
                    Text(a, "refresh_token") ?? "", now.AddSeconds(Number(a, "refresh_token_expires_in") ?? 0)), interval, null);
            case "authorization_pending": return new(null, interval, null);
            case "slow_down": return new(null, Number(a, "interval") ?? interval + 5, null);
            case var error: return new(null, interval, error);
        }
    }

    /// <summary>The token to use now, renewed (and kept) when it ran out; null when there is none or it can't be renewed.</summary>
    public static async Task<GitHubToken?> Current(string? file = null)
    {
        // One at a time: a refresh token works once, so a second renewal at the same moment
        // would fail and forget the token the first had just kept.
        await Renewing.WaitAsync();
        try
        {
            if (Load(file) is not { } token) return null;
            var now = DateTime.UtcNow;
            if (token.Fresh(now)) return token;
            if (!token.Renewable(now)) { Forget(file); return null; }
            // No answer (offline) throws and keeps the token; a refusal forgets it.
            var renewed = await Renew(token);
            if (renewed is null) { Forget(file); return null; }
            // The old refresh token is spent: unkept, the new one still serves this share.
            try { Save(renewed, file); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException) { }
            return renewed;
        }
        finally { Renewing.Release(); }
    }

    private static readonly SemaphoreSlim Renewing = new(1, 1);

    // ---- kept on this PC, for this Windows user only ----

    private static readonly byte[] Entropy = "Clayo Pages GitHub token"u8.ToArray();

    public static void Save(GitHubToken t, string? file = null)
    {
        var json = new JsonObject
        {
            ["access"] = t.Access, ["accessUntil"] = t.AccessUntil.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
            ["refresh"] = t.Refresh, ["refreshUntil"] = t.RefreshUntil.ToString("o", System.Globalization.CultureInfo.InvariantCulture)
        }.ToJsonString();
        var path = file ?? DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, ProtectedData.Protect(Encoding.UTF8.GetBytes(json), Entropy, DataProtectionScope.CurrentUser));
    }

    public static GitHubToken? Load(string? file = null)
    {
        try
        {
            var bytes = ProtectedData.Unprotect(File.ReadAllBytes(file ?? DefaultPath), Entropy, DataProtectionScope.CurrentUser);
            var o = JsonNode.Parse(bytes)!.AsObject();
            DateTime When(string key) => DateTime.Parse(Text(o, key)!, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind);
            return new GitHubToken(Text(o, "access") ?? throw new FormatException("no token"), When("accessUntil"), Text(o, "refresh") ?? "", When("refreshUntil"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException
            or System.Text.Json.JsonException or FormatException or ArgumentNullException or InvalidOperationException) { return null; }
    }

    public static bool SignedIn(string? file = null) => File.Exists(file ?? DefaultPath);

    /// <summary>Deletes the token here. Revoking it on GitHub is the user's, at AppsPage.</summary>
    public static void Forget(string? file = null)
    {
        try { File.Delete(file ?? DefaultPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static async Task<JsonObject> Post(string url, Dictionary<string, string> form)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("Clayo");
        using var response = await Http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return JsonNode.Parse(text) as JsonObject
            ?? throw new GitHubException(response.StatusCode, response.ReasonPhrase ?? "no answer");
    }

    private static string? Text(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    private static int? Number(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<int>(out var n) ? n : null;
}

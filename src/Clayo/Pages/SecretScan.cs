using System.Text.RegularExpressions;

namespace CcxShell.Core;

/// <summary>One thing in a page that shouldn't leave the PC, and the line it is on.</summary>
public sealed record SecretFinding(int Line, string What);

/// <summary>
/// What D16 looks for before a page leaves the PC: secrets, local paths and internal hosts.
/// It warns; the user decides. Images can't be read, so the confirm says so. No WPF.
/// </summary>
public static partial class SecretScan
{
    // Each pattern gives up after this long on a line, so a page made to be slow can't hang Clayo.
    private const int Timeout = 250;

    private static readonly (string What, Regex Pattern)[] Rules =
    [
        ("a GitHub token", GitHubToken()),
        ("an API key (sk-)", SkKey()),
        ("a Stripe key", StripeKey()),
        ("a Google API key", GoogleKey()),
        ("an AWS key", AwsKey()),
        ("a Slack token", SlackToken()),
        ("a JWT", Jwt()),
        ("a bearer token", Bearer()),
        ("a password in a URL", UrlPassword()),
        ("a private key", PrivateKey()),
        ("a KEY=value secret", KeyValue()),
        ("a local path", LocalPath()),
        ("an internal host", InternalHost()),
    ];

    [GeneratedRegex(@"\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,})", RegexOptions.None, Timeout)]
    private static partial Regex GitHubToken();

    [GeneratedRegex(@"\bsk-(?:[a-z]+-)?[A-Za-z0-9_\-]{20,}", RegexOptions.None, Timeout)]
    private static partial Regex SkKey();

    [GeneratedRegex(@"\b[sr]k_(?:live|test)_[A-Za-z0-9]{16,}", RegexOptions.None, Timeout)]
    private static partial Regex StripeKey();

    [GeneratedRegex(@"\bAIza[0-9A-Za-z_\-]{35}", RegexOptions.None, Timeout)]
    private static partial Regex GoogleKey();

    [GeneratedRegex(@"\b(?:AKIA|ASIA)[A-Z0-9]{16}\b", RegexOptions.None, Timeout)]
    private static partial Regex AwsKey();

    [GeneratedRegex(@"\bxox[abposr]-[A-Za-z0-9\-]{10,}", RegexOptions.None, Timeout)]
    private static partial Regex SlackToken();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_\-]{10,}\.eyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}", RegexOptions.None, Timeout)]
    private static partial Regex Jwt();

    [GeneratedRegex(@"\bBearer\s+[A-Za-z0-9._~+/\-]{20,}", RegexOptions.IgnoreCase, Timeout)]
    private static partial Regex Bearer();

    // scheme://user:password@host
    [GeneratedRegex(@"\b[a-z][a-z0-9+.\-]*://[^/\s:@""'<>]+:[^/\s@""'<>]+@", RegexOptions.IgnoreCase, Timeout)]
    private static partial Regex UrlPassword();

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----", RegexOptions.None, Timeout)]
    private static partial Regex PrivateKey();

    // API_KEY=…, SECRET: "…", password = …; not an empty value or a placeholder in <angle brackets>.
    [GeneratedRegex(@"\b[A-Z0-9_]*(?:SECRET|TOKEN|PASSWORD|PASSWD|API_?KEY|PRIVATE_KEY|ACCESS_KEY)[A-Z0-9_]*\s*[=:]\s*[""']?[^\s""'<>]{8,}", RegexOptions.IgnoreCase, Timeout)]
    private static partial Regex KeyValue();

    // C:\Users\x, also as JS/JSON writes it (C:\\Users\\x) or with forward slashes; /Users/x,
    // /home/x; a network share \\server\share.
    [GeneratedRegex(@"\b[A-Za-z]:(?:\\{1,2}|/)Users(?:\\{1,2}|/)[^\\/\s""'<>]+|(?<![\w/])/(?:Users|home)/[^/\s""'<>]+|(?<![\w\\])\\{2,4}[A-Za-z0-9][A-Za-z0-9.\-]*\\{1,2}[A-Za-z0-9$]", RegexOptions.IgnoreCase, Timeout)]
    private static partial Regex LocalPath();

    [GeneratedRegex(@"\b(?:localhost|127\.0\.0\.1|10\.\d{1,3}\.\d{1,3}\.\d{1,3}|192\.168\.\d{1,3}\.\d{1,3}|172\.(?:1[6-9]|2\d|3[01])\.\d{1,3}\.\d{1,3}|[a-z0-9\-]+(?:\.[a-z0-9\-]+)*\.(?:local|internal|lan|corp))\b", RegexOptions.IgnoreCase, Timeout)]
    private static partial Regex InternalHost();

    /// <summary>
    /// Every finding, in line order, at most one of a kind per line. A line too slow to read is
    /// a finding too: what it holds is unknown.
    /// </summary>
    public static IReadOnlyList<SecretFinding> Find(string text)
    {
        var found = new List<SecretFinding>();
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
            foreach (var (what, pattern) in Rules)
            {
                bool hit;
                try { hit = pattern.IsMatch(lines[i]); }
                catch (RegexMatchTimeoutException)
                {
                    found.Add(new SecretFinding(i + 1, "a line too long to check"));
                    break;
                }
                if (hit) found.Add(new SecretFinding(i + 1, what));
            }
        return found;
    }
}

using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace CcxShell.Core;

/// <summary>
/// Finds a newer Clayo on GitHub and installs it with scripts/install.ps1, the same script the
/// README's install command runs. The script waits for this Clayo to quit, swaps the folder
/// and starts the new one, so Clayo only has to start it and quit.
/// </summary>
public static class Updater
{
    /// <summary>
    /// The script as it was released with that version, not as main has it now: a push to main
    /// never reaches anyone's Update button.
    /// </summary>
    public static string ScriptUrl(Version version) =>
        $"https://raw.githubusercontent.com/YogiOnCode/Clayo/v{version.ToString(3)}/scripts/install.ps1";
    private const string LatestUrl = "https://api.github.com/repos/YogiOnCode/Clayo/releases/latest";

#if DEBUG
    private static readonly bool IsDebugBuild = true;
#else
    private static readonly bool IsDebugBuild = false;
#endif

    public static Version Current => Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0);

    /// <summary>The latest release's version when it is newer than this Clayo; null otherwise, offline included.</summary>
    public static async Task<Version?> NewerAsync()
    {
        // A dev build would update its own bin folder with the release.
        if (IsDebugBuild) return null;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            // GitHub refuses requests without a User-Agent.
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Clayo");
            using var doc = JsonDocument.Parse(await http.GetStringAsync(LatestUrl));
            var tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            return Newer(tag, Current);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException) { return null; }
    }

    /// <summary>A tag like "v1.0.1", when it is a later version than current.</summary>
    public static Version? Newer(string? tag, Version current)
    {
        if (!Version.TryParse(tag?.TrimStart('v', 'V'), out var latest)) return null;
        return Plain(latest) > Plain(current) ? latest : null;
    }

    // 1.0.0 and 1.0.0.0 are the same release; Version alone ranks the first below the second.
    private static Version Plain(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    /// <summary>Starts the install script in a window you can watch; the caller then quits.</summary>
    public static void Start(Version version) => Process.Start(new ProcessStartInfo("powershell.exe")
    {
        ArgumentList =
        {
            "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command",
            // On a failure the window would close before the error could be read.
            $"try {{ irm '{ScriptUrl(version)}' | iex }} catch {{ Write-Host $_ -ForegroundColor Red; pause }}",
        },
        // Update this Clayo where it is, not a copy in the default folder.
        Environment = { ["CLAYO_DIR"] = AppContext.BaseDirectory },
        UseShellExecute = false,
    })?.Dispose();
}

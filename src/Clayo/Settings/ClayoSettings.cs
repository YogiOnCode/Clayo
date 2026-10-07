using System.IO;
using System.Text.Json;

namespace CcxShell.Core;

/// <summary>How the status strip and footer draw their numbers (design/settings, D6: these four of its six).</summary>
public enum StatusTheme { Numbers, Bars, Rings, Chips }

/// <summary>Which agent New session starts when both are in use (the setup window, docs/SETUP.md).</summary>
public enum NewSessionAgent { Claude, Codex, Ask }

/// <summary>
/// What you chose in Settings, kept in %LOCALAPPDATA%\Clayo\settings.json. Start at login is
/// not here: it lives in the Run key (LoginStartup). The rest is what the setup window chose
/// (docs/SETUP.md); until SetupDone, setup shows before the window.
/// </summary>
public sealed record ClayoSettings(
    bool StatusHeader = true,
    bool StatusFooter = true,
    bool StatusModel = true,
    bool StatusBranch = true,
    bool StatusContext = true,
    bool StatusEffort = true,
    bool StatusFiveHour = true,
    bool StatusSevenDay = true,
    int ReserveAt = 0,
    bool ReserveFiveHour = true,
    bool ReserveSevenDay = true,
    StatusTheme Theme = StatusTheme.Numbers,
    bool SetupDone = false,
    bool UseClaude = true,
    bool UseCodex = true,
    NewSessionAgent DefaultAgent = NewSessionAgent.Claude,
    string? ClaudePath = null,
    string? CodexPath = null,
    bool ScreenshotHotkey = true,
    bool ScreenshotOffer = false)
{
    /// <summary>The reserve thresholds Settings offers, in percent. 0 is off.</summary>
    public static readonly int[] ReserveSteps = [0, 70, 80, 90];

    /// <summary>Who starts a New session; null asks. Never an agent whose Use is off, so one in use never asks.</summary>
    public AgentKind? NewSessionKind =>
        !UseCodex ? AgentKind.Claude
        : !UseClaude ? AgentKind.Codex
        : DefaultAgent switch
        {
            NewSessionAgent.Codex => AgentKind.Codex,
            NewSessionAgent.Ask => null,
            _ => AgentKind.Claude
        };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clayo", "settings.json");

    /// <summary>
    /// Key by key, so one odd value costs only that setting. A missing, unreadable or broken
    /// file is the defaults: settings are never a reason for Clayo not to start.
    /// </summary>
    public static ClayoSettings Load(string path)
    {
        var d = new ClayoSettings();
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            root = doc.RootElement.Clone();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return d; }
        if (root.ValueKind != JsonValueKind.Object) return d;

        bool B(string name, bool fallback) =>
            root.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? v.GetBoolean()
                : fallback;

        string? S(string name) =>
            root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } text
                ? text
                : null;

        // By name only: Enum.TryParse would also take "2".
        T E<T>(string name, T fallback) where T : struct, Enum =>
            S(name) is { } text && Enum.GetNames<T>().FirstOrDefault(n => n.Equals(text, StringComparison.OrdinalIgnoreCase)) is { } found
                ? Enum.Parse<T>(found)
                : fallback;

        return new ClayoSettings(
            B("statusHeader", d.StatusHeader),
            B("statusFooter", d.StatusFooter),
            B("statusModel", d.StatusModel),
            B("statusBranch", d.StatusBranch),
            B("statusContext", d.StatusContext),
            B("statusEffort", d.StatusEffort),
            B("statusFiveHour", d.StatusFiveHour),
            B("statusSevenDay", d.StatusSevenDay),
            root.TryGetProperty("reserveAt", out var r) && r.ValueKind == JsonValueKind.Number
                && r.TryGetInt32(out var at) && ReserveSteps.Contains(at) ? at : d.ReserveAt,
            B("reserveFiveHour", d.ReserveFiveHour),
            B("reserveSevenDay", d.ReserveSevenDay),
            E("theme", d.Theme),
            B("setupDone", d.SetupDone),
            B("useClaude", d.UseClaude),
            B("useCodex", d.UseCodex),
            E("defaultAgent", d.DefaultAgent),
            S("claudePath"),
            S("codexPath"),
            B("screenshotHotkey", d.ScreenshotHotkey),
            B("screenshotOffer", d.ScreenshotOffer));
    }

    /// <summary>Written beside the file then swapped in, so a crash mid-write loses nothing. Best effort.</summary>
    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            using (var file = File.Create(tmp))
            using (var w = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true }))
            {
                // By hand, the same keys Load reads, rather than through reflection.
                w.WriteStartObject();
                w.WriteBoolean("statusHeader", StatusHeader);
                w.WriteBoolean("statusFooter", StatusFooter);
                w.WriteBoolean("statusModel", StatusModel);
                w.WriteBoolean("statusBranch", StatusBranch);
                w.WriteBoolean("statusContext", StatusContext);
                w.WriteBoolean("statusEffort", StatusEffort);
                w.WriteBoolean("statusFiveHour", StatusFiveHour);
                w.WriteBoolean("statusSevenDay", StatusSevenDay);
                w.WriteNumber("reserveAt", ReserveAt);
                w.WriteBoolean("reserveFiveHour", ReserveFiveHour);
                w.WriteBoolean("reserveSevenDay", ReserveSevenDay);
                w.WriteString("theme", Theme.ToString().ToLowerInvariant());
                w.WriteBoolean("setupDone", SetupDone);
                w.WriteBoolean("useClaude", UseClaude);
                w.WriteBoolean("useCodex", UseCodex);
                w.WriteString("defaultAgent", DefaultAgent.ToString().ToLowerInvariant());
                if (ClaudePath is not null) w.WriteString("claudePath", ClaudePath);
                if (CodexPath is not null) w.WriteString("codexPath", CodexPath);
                w.WriteBoolean("screenshotHotkey", ScreenshotHotkey);
                w.WriteBoolean("screenshotOffer", ScreenshotOffer);
                w.WriteEndObject();
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

#:property TargetFramework=net8.0-windows
#:project ../CcxShell.csproj
using CcxShell.Core;

// Never the real settings file: a throwaway folder, removed at the end.
var dir = Path.Combine(Path.GetTempPath(), "clayo-check-settings-" + Guid.NewGuid().ToString("N"));
var path = Path.Combine(dir, "settings.json");

int fail = 0;
void Check<T>(string name, T got, T want)
{
    var ok = EqualityComparer<T>.Default.Equals(got, want);
    if (!ok) fail++;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}  got={got} want={want}");
}

var defaults = new ClayoSettings();
try
{
    Check("no file is the defaults", ClayoSettings.Load(path), defaults);
    Check("every field shows by default", defaults with { }, new ClayoSettings(true, true, true, true, true, true, true, true));

    var changed = defaults with { StatusHeader = false, StatusEffort = false, StatusSevenDay = false };
    changed.Save(path);
    Check("saved settings load back", ClayoSettings.Load(path), changed);
    Check("no temp file left behind", Directory.GetFiles(dir).Length, 1);

    File.WriteAllText(path, """{"statusHeader":false,"someLaterSetting":{"x":1}}""");
    Check("unknown keys are ignored", ClayoSettings.Load(path), defaults with { StatusHeader = false });

    File.WriteAllText(path, """{"statusModel":false}""");
    Check("missing keys fall back to defaults", ClayoSettings.Load(path), defaults with { StatusModel = false });

    File.WriteAllText(path, """{"statusModel":"no","statusBranch":false}""");
    Check("a key of the wrong type falls back, the others still count",
        ClayoSettings.Load(path), defaults with { StatusBranch = false });

    // Reserve (step 6): off by default, one of the offered thresholds, else off.
    Check("reserve is off by default", defaults.ReserveAt, 0);
    var reserve = defaults with { ReserveAt = 80, ReserveSevenDay = false };
    reserve.Save(path);
    Check("a reserve loads back", ClayoSettings.Load(path), reserve);
    File.WriteAllText(path, """{"reserveAt":55}""");
    Check("a threshold not offered is off", ClayoSettings.Load(path).ReserveAt, 0);
    File.WriteAllText(path, """{"reserveAt":"90"}""");
    Check("a threshold of the wrong type is off", ClayoSettings.Load(path).ReserveAt, 0);

    // Theme: Numbers by default (D1), saved by name, anything unknown is Numbers.
    Check("numbers by default", defaults.Theme, StatusTheme.Numbers);
    var rings = defaults with { Theme = StatusTheme.Rings };
    rings.Save(path);
    Check("a theme loads back", ClayoSettings.Load(path).Theme, StatusTheme.Rings);
    Check("saved by its name", File.ReadAllText(path).Contains("\"theme\": \"rings\""), true);
    File.WriteAllText(path, """{"theme":"voxel"}""");
    Check("a theme not offered is numbers", ClayoSettings.Load(path).Theme, StatusTheme.Numbers);
    File.WriteAllText(path, """{"theme":"2"}""");
    Check("a number is not a theme", ClayoSettings.Load(path).Theme, StatusTheme.Numbers);

    // Setup (docs/SETUP.md): not done, both agents in use and Claude for New session by default;
    // paths only when Locate… picked one.
    Check("setup not done by default", defaults.SetupDone, false);
    Check("both agents in use by default", defaults.UseClaude && defaults.UseCodex, true);
    Check("claude starts new sessions by default", defaults.DefaultAgent, NewSessionAgent.Claude);
    var setup = defaults with
    {
        SetupDone = true, UseCodex = false, DefaultAgent = NewSessionAgent.Ask,
        ClaudePath = @"C:\tools\claude.exe", CodexPath = null
    };
    setup.Save(path);
    Check("setup choices load back", ClayoSettings.Load(path), setup);
    Check("no path saved is no key", File.ReadAllText(path).Contains("codexPath"), false);
    Check("an agent saved by its name", File.ReadAllText(path).Contains("\"defaultAgent\": \"ask\""), true);
    File.WriteAllText(path, """{"defaultAgent":"gemini","claudePath":"","codexPath":3}""");
    Check("an agent not offered is claude", ClayoSettings.Load(path).DefaultAgent, NewSessionAgent.Claude);
    Check("an empty path is none", ClayoSettings.Load(path).ClaudePath, null);
    Check("a path of the wrong type is none", ClayoSettings.Load(path).CodexPath, null);
    var withStatus = setup with { StatusHeader = false };
    withStatus.Save(path);
    Check("a status option saved later keeps the setup choices", ClayoSettings.Load(path), withStatus);

    File.WriteAllText(path, """{"statusModel":fal""");
    Check("a half-written file is the defaults", ClayoSettings.Load(path), defaults);
    File.WriteAllText(path, "[true]");
    Check("not an object is the defaults", ClayoSettings.Load(path), defaults);
    File.WriteAllText(path, "");
    Check("an empty file is the defaults", ClayoSettings.Load(path), defaults);

    // Held open without sharing, the way another program might: saving must not throw.
    using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        changed.Save(path);
        Check("a file that cannot be read is the defaults", ClayoSettings.Load(path), defaults);
    }
}
finally
{
    if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
}

Console.WriteLine(fail == 0 ? "\nall checks passed" : $"\n{fail} FAILED");
return fail;

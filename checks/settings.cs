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

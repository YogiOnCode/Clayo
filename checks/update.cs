#:property TargetFramework=net8.0-windows
#:project ../src/Clayo/CcxShell.csproj
using CcxShell.Core;

int fail = 0;
void Check<T>(string name, T got, T want)
{
    var ok = EqualityComparer<T>.Default.Equals(got, want);
    if (!ok) fail++;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}  got={got} want={want}");
}

// The assembly's version has four parts; release tags have three and a v.
var current = new Version(1, 0, 0, 0);
Check("a later patch is newer", Updater.Newer("v1.0.1", current), new Version(1, 0, 1));
Check("a later minor is newer", Updater.Newer("v1.2.0", current), new Version(1, 2, 0));
Check("without the v too", Updater.Newer("2.0.0", current), new Version(2, 0, 0));
Check("the same release is not", Updater.Newer("v1.0.0", current), null);
Check("an older one is not", Updater.Newer("v0.9.9", new Version(1, 0, 0, 0)), null);
Check("a tag that is no version is not", Updater.Newer("nightly", current), null);
Check("no tag is not", Updater.Newer(null, current), null);

// The script comes from the release's own tag, never from main.
Check("script from the tag", Updater.ScriptUrl(new Version(1, 0, 2, 0)),
    "https://raw.githubusercontent.com/YogiOnCode/Clayo/v1.0.2/scripts/install.ps1");

Console.WriteLine(fail == 0 ? "\nall checks passed" : $"\n{fail} FAILED");
return fail;

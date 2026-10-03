#:property TargetFramework=net8.0-windows
#:project ../src/Clayo/CcxShell.csproj
using CcxShell.Core;
using Microsoft.Win32;

// Never the real Run key: a throwaway key and marker, both removed at the end.
const string testKey = @"Software\ClayoTest";
var marker = Path.Combine(Path.GetTempPath(), "clayo-check-no-login-start");
var s = new LoginStartup(testKey, "Clayo", marker);

int fail = 0;
void Check<T>(string name, T got, T want)
{
    var ok = EqualityComparer<T>.Default.Equals(got, want);
    if (!ok) fail++;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}  got={got} want={want}");
}

string? Value()
{
    using var key = Registry.CurrentUser.OpenSubKey(testKey);
    return key?.GetValue("Clayo") as string;
}

try
{
    Check("off before anything is written", s.IsOn, false);

    s.Keep(@"C:\a\clayo.exe");
    Check("on by default", s.IsOn, true);
    Check("pointing at the running exe, hidden", Value(), "\"C:\\a\\clayo.exe\" --background");

    s.Keep(@"C:\b\clayo.exe");
    Check("follows the exe when it moves", Value(), "\"C:\\b\\clayo.exe\" --background");

    s.Set(false, @"C:\b\clayo.exe");
    Check("turned off removes the entry", Value(), null);
    s.Keep(@"C:\b\clayo.exe");
    Check("and the next start keeps it off", s.IsOn, false);

    s.Set(true, @"C:\b\clayo.exe");
    Check("turned on again writes it", Value(), "\"C:\\b\\clayo.exe\" --background");
    s.Keep(@"C:\c\clayo.exe");
    Check("and the next start keeps it on", Value(), "\"C:\\c\\clayo.exe\" --background");

#if DEBUG
    Check("a Debug build never writes the real Run key", LoginStartup.ForThisUser().CanWrite, false);
#else
    Check("a Release build writes the real Run key", LoginStartup.ForThisUser().CanWrite, true);
#endif
    Check("a Debug build may still write a test key", s.CanWrite, true);
}
finally
{
    Registry.CurrentUser.DeleteSubKeyTree(testKey, throwOnMissingSubKey: false);
    File.Delete(marker);
}

Console.WriteLine(fail == 0 ? "\nall checks passed" : $"\n{fail} FAILED");
return fail;

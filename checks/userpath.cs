#:property TargetFramework=net8.0-windows
#:project ../CcxShell.csproj
using CcxShell.Core;
using Microsoft.Win32;

// Never the real HKCU\Environment: a throwaway key, removed at the end.
const string testKey = @"Software\ClayoTestPath";
var p = new UserPath(testKey);

int fail = 0;
void Check<T>(string name, T got, T want)
{
    var ok = EqualityComparer<T>.Default.Equals(got, want);
    if (!ok) fail++;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}  got={got} want={want}");
}

(string? value, RegistryValueKind kind) Raw()
{
    using var key = Registry.CurrentUser.OpenSubKey(testKey);
    return key?.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string v
        ? (v, key.GetValueKind("Path"))
        : (null, RegistryValueKind.Unknown);
}

void Write(string value, RegistryValueKind kind)
{
    using var key = Registry.CurrentUser.CreateSubKey(testKey);
    key.SetValue("Path", value, kind);
}

try
{
    Check("no PATH: not on it", p.Contains(@"C:\clayo"), false);
    p.Set(true, @"C:\clayo");
    Check("added to an empty PATH", Raw(), (@"C:\clayo", RegistryValueKind.ExpandString));

    Write(@"%USERPROFILE%\.local\bin;C:\tools;", RegistryValueKind.ExpandString);
    p.Set(true, @"C:\clayo");
    Check("added at the end, the rest as written", Raw(), (@"%USERPROFILE%\.local\bin;C:\tools;C:\clayo", RegistryValueKind.ExpandString));
    p.Set(true, @"C:\clayo");
    Check("added twice is once", Raw().value, @"%USERPROFILE%\.local\bin;C:\tools;C:\clayo");
    Check("on it", p.Contains(@"C:\clayo"), true);
    Check("on it with a trailing slash too", p.Contains(@"C:\Clayo\"), true);

    p.Set(false, @"C:\clayo\");
    Check("removed, only ours", Raw(), (@"%USERPROFILE%\.local\bin;C:\tools", RegistryValueKind.ExpandString));
    p.Set(false, @"C:\clayo");
    Check("removed when not there changes nothing", Raw().value, @"%USERPROFILE%\.local\bin;C:\tools");

    var home = Environment.GetEnvironmentVariable("USERPROFILE")!;
    Check("an entry written with a %VAR% counts", p.Contains(Path.Combine(home, ".local", "bin")), true);

    Write(@"""C:\Quoted Folder"";C:\x", RegistryValueKind.String);
    Check("a quoted entry counts", p.Contains(@"C:\Quoted Folder"), true);
    p.Set(true, @"C:\clayo");
    Check("a plain value stays plain", Raw(), (@"""C:\Quoted Folder"";C:\x;C:\clayo", RegistryValueKind.String));

#if DEBUG
    Check("a Debug build never writes the real PATH", UserPath.ForThisUser().CanWrite, false);
#else
    Check("a Release build writes the real PATH", UserPath.ForThisUser().CanWrite, true);
#endif
    Check("a Debug build may still write a test key", p.CanWrite, true);
}
finally
{
    Registry.CurrentUser.DeleteSubKeyTree(testKey, throwOnMissingSubKey: false);
}

Console.WriteLine(fail == 0 ? "\nall checks passed" : $"\n{fail} FAILED");
return fail;

#:property TargetFramework=net8.0-windows
#:project ../src/Clayo/CcxShell.csproj
using CcxShell.Core;

// Old status files go, recent ones stay. A temp folder, never the real %LOCALAPPDATA%\Clayo\status.
int fail = 0;
void Check<T>(string name, T got, T want)
{
    var ok = EqualityComparer<T>.Default.Equals(got, want);
    if (!ok) fail++;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}  got={got} want={want}");
}

var dir = Directory.CreateTempSubdirectory("clayo-prune").FullName;
try
{
    var now = DateTime.UtcNow;
    string Write(string name, DateTime at)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, "{}");
        File.SetLastWriteTimeUtc(path, at);
        return path;
    }
    var old = Write("old.json", now - StatusStore.KeepFor - TimeSpan.FromHours(1));
    var recent = Write("recent.json", now - TimeSpan.FromDays(1));
    var other = Write("notes.txt", now - TimeSpan.FromDays(400));

    StatusStore.Prune(dir, now - StatusStore.KeepFor);
    Check("older than a week: deleted", File.Exists(old), false);
    Check("from yesterday: kept", File.Exists(recent), true);
    Check("not a status file: left alone", File.Exists(other), true);
}
finally { Directory.Delete(dir, recursive: true); }

Console.WriteLine(fail == 0 ? "\nall checks passed" : $"\n{fail} FAILED");
return fail;

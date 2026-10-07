#:property TargetFramework=net8.0-windows
#:project ../src/Clayo/CcxShell.csproj
using CcxShell.Core;

// Screenshot files: their names, and the week they are kept. A temp folder, never the real
// %LOCALAPPDATA%\Clayo\shots.
int fail = 0;
void Check<T>(string name, T got, T want)
{
    var ok = EqualityComparer<T>.Default.Equals(got, want);
    if (!ok) fail++;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}  got={got} want={want}");
}

var dir = Directory.CreateTempSubdirectory("clayo-shots").FullName;
try
{
    var at = new DateTime(2026, 10, 6, 19, 2, 11);
    var first = Screenshots.NewPath(dir, at);
    Check("named by the second it was taken", Path.GetFileName(first), "2026-10-06_19-02-11.png");
    File.WriteAllText(first, "");
    var second = Screenshots.NewPath(dir, at);
    Check("a second one in that second gets -2", Path.GetFileName(second), "2026-10-06_19-02-11-2.png");
    File.WriteAllText(second, "");
    Check("and a third -3", Path.GetFileName(Screenshots.NewPath(dir, at)), "2026-10-06_19-02-11-3.png");

    var now = DateTime.UtcNow;
    File.SetLastWriteTimeUtc(first, now - Screenshots.KeepFor - TimeSpan.FromHours(1));
    File.SetLastWriteTimeUtc(second, now - TimeSpan.FromDays(1));
    var notes = Path.Combine(dir, "notes.txt");
    File.WriteAllText(notes, "");
    File.SetLastWriteTimeUtc(notes, now - TimeSpan.FromDays(400));

    Screenshots.Prune(dir, now - Screenshots.KeepFor);
    Check("older than a week: deleted", File.Exists(first), false);
    Check("from yesterday: kept", File.Exists(second), true);
    Check("not a screenshot: left alone", File.Exists(notes), true);
    Check("no folder yet: nothing to do", Try(() => Screenshots.Prune(Path.Combine(dir, "none"), now)), true);
}
finally { Directory.Delete(dir, recursive: true); }

Console.WriteLine(fail == 0 ? "\nall checks passed" : $"\n{fail} FAILED");
return fail;

static bool Try(Action a) { try { a(); return true; } catch { return false; } }

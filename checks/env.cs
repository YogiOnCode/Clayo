#:property TargetFramework=net8.0-windows
#:project ../CcxShell.csproj
using System.Diagnostics;
using CcxShell.Core;

// Planted here rather than relying on how this check was launched, so it behaves the
// same whether or not you run it from inside a Claude Code session.
Environment.SetEnvironmentVariable("CLAUDE_CODE_CHILD_SESSION", "1");
Environment.SetEnvironmentVariable("ANTHROPIC_MODEL", "keep-me");

static string ChildSees(string name)
{
    var psi = new ProcessStartInfo("cmd.exe", $"/c echo %{name}%")
    { RedirectStandardOutput = true, UseShellExecute = false };
    using var p = Process.Start(psi)!;
    var s = p.StandardOutput.ReadToEnd().Trim();
    p.WaitForExit();
    return s == $"%{name}%" ? "" : s;   // cmd echoes %NAME% verbatim when unset
}

int fail = 0;
void Check(string name, string got, string want)
{
    bool ok = got == want;
    if (!ok) fail++;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}  got=\"{got}\" want=\"{want}\"");
}

// Proves the bug is real: a spawned shell does inherit the marker.
Check("child inherits the marker before scrubbing", ChildSees("CLAUDE_CODE_CHILD_SESSION"), "1");

SessionLauncher.ScrubInheritedSession();

Check("child cannot see the marker after scrubbing", ChildSees("CLAUDE_CODE_CHILD_SESSION"), "");
Check("configuration is left untouched", ChildSees("ANTHROPIC_MODEL"), "keep-me");

Console.WriteLine(fail == 0 ? "\nall checks passed" : $"\n{fail} FAILED");
return fail;

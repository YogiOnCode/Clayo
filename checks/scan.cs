#:property TargetFramework=net8.0-windows
#:project ../CcxShell.csproj
using System.Text;
using CcxShell.UI;

static PaneStatus? Feed(ref byte[] carry, params string[] chunks)
{
    PaneStatus? last = null;
    foreach (var c in chunks)
    {
        var got = TerminalPane.Scan(Encoding.UTF8.GetBytes(c), ref carry);
        if (got is not null) last = got;
    }
    return last;
}

int fail = 0;
void Check(string name, PaneStatus? got, PaneStatus? want)
{
    var ok = got == want;
    if (!ok) fail++;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}  got={got?.ToString() ?? "null"} want={want?.ToString() ?? "null"}");
}

byte[] c1 = []; Check("plain output is nothing",
    Feed(ref c1, "\u001b[32mrunning tests\u001b[0m\r\n"), null);

byte[] c2 = []; Check("api error in one chunk",
    Feed(ref c2, "API Error: 529 Overloaded."), PaneStatus.Error);

byte[] c3 = []; Check("permission prompt in one chunk",
    Feed(ref c3, "Do you want to proceed?"), PaneStatus.NeedsInput);

// The reason the carry exists: a pipe read can land mid-marker.
byte[] c4 = []; Check("api error split across two reads",
    Feed(ref c4, "...API Er", "ror: 529"), PaneStatus.Error);

byte[] c5 = []; Check("prompt split across two reads",
    Feed(ref c5, "Do you wa", "nt to proceed?"), PaneStatus.NeedsInput);

// Split one byte at a time, the worst case.
byte[] c6 = []; Check("prompt split byte by byte",
    Feed(ref c6, "Do you want to".Select(ch => ch.ToString()).ToArray()), PaneStatus.NeedsInput);

// A marker must not be invented across unrelated chunks separated by real output.
byte[] c7 = []; Check("no false positive across a gap",
    Feed(ref c7, "Do you wa", "XXXXXXXXXXXXXXXXXXXX", "nt to proceed?"), null);

// Multi-byte UTF-8 must not be mistaken for ASCII.
byte[] c8 = []; Check("utf-8 box drawing is not a marker",
    Feed(ref c8, "╭──────────╮ ✳ thinking"), null);

byte[] c9 = []; Check("error wins over prompt in one chunk",
    Feed(ref c9, "Do you want to proceed? API Error: boom"), PaneStatus.Error);

Console.WriteLine(fail == 0 ? "\nall checks passed" : $"\n{fail} FAILED");
return fail;

#:property TargetFramework=net10.0-windows
#:project ../src/Clayo/CcxShell.csproj
using System.Text;
using CcxShell.Core;
using CcxShell.UI;

// A permission prompt stays "needs you" until it is answered (#27). Replays pty output and keys
// through the pane's own rules: Scan on each chunk, the idle timer every 400 ms counting the
// chunks of the last 900 ms, and the keys you type (TerminalPane's constructor and StartPty).

int fail = 0;
void Check(string name, PaneStatus? got, PaneStatus? want)
{
    var ok = got == want;
    if (!ok) fail++;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}  got={got?.ToString() ?? "null"} want={want?.ToString() ?? "null"}");
}

// ms -> pty output. Claude draws its dialog in several reads, the question in one of them.
static (int At, string Text)[] Dialog(int from) =>
[
    (from, "\u001b[2K\u001b[1A\u001b[2K"),
    (from + 40, "╭──────────────────────────╮\r\n│ Bash command             │\r\n│   git push --dry-run     │\r\n"),
    (from + 80, "│ Do\u001b[1Cyou\u001b[1Cwant\u001b[1Cto\u001b[1Cproceed?  │\r\n"),   // spaces as ConPTY sends them
    (from + 120, "│ ❯ 1. Yes                 │\r\n│   2. No, and tell Claude │\r\n"),
    (from + 160, "╰──────────────────────────╯\r\n"),
];

static (int At, string Text)[] Burst(int from, int chunks) =>
    Enumerable.Range(0, chunks).Select(i => (from + i * 60, "\u001b[2K✳ Running…\r")).ToArray();

// Feeds output and keys until `until` ms, ticking the timer as the pane does; returns the status then.
static PaneStatus Run((int At, string Text)[] output, int until, params (int At, string Keys)[] keys)
{
    var status = PaneStatus.Working;
    bool answered = false;
    var t0 = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
    var keyAt = DateTime.MinValue;
    byte[] carry = [];
    var recent = new Queue<int>();
    int last = int.MinValue / 2;
    var events = output.Select(o => (o.At, o.Text, Key: false))
        .Concat(keys.Select(k => (k.At, Text: k.Keys, Key: true))).OrderBy(e => e.At);
    var queue = new Queue<(int At, string Text, bool Key)>(events);
    for (int tick = 400; tick <= until; tick += 400)
    {
        while (queue.Count > 0 && queue.Peek().At <= tick)
        {
            var (at, text, key) = queue.Dequeue();
            var bytes = Encoding.UTF8.GetBytes(text);
            if (key)
            {
                keyAt = t0.AddMilliseconds(at);
                if (TerminalPane.Answers(bytes)) answered = true;
                continue;
            }
            last = at;
            if (TerminalPane.CountsAsWork(t0.AddMilliseconds(at), DateTime.MinValue, keyAt))
            {
                recent.Enqueue(at);
                while (recent.Count > 12) recent.Dequeue();
            }
            if (TerminalPane.Scan(bytes, ref carry) is { } s) { status = s; answered = false; }
        }
        int burst = recent.Count(t => tick - t < 900);
        if (TerminalPane.FromTimer(status, burst, tick - last, answered) is { } n) status = n;
    }
    return status;
}

Check("a dialog drawn in several reads stays needs you",
    Run([.. Burst(0, 10), .. Dialog(1000)], 5000), PaneStatus.NeedsInput);
Check("an api error drawn in several reads stays an error",
    Run([(0, "\u001b[2K"), (40, "  ⎿  API Error: 529 Overloaded\r\n"), (80, "\u001b[2K> \r\n"), (120, "│ >  │\r\n")], 5000),
    PaneStatus.Error);
Check("a plain turn still ends done", Run(Burst(0, 10), 5000), PaneStatus.Done);

// Moving the choice redraws the dialog without answering it.
Check("an arrow key and its redraw keep it",
    Run([.. Burst(0, 10), .. Dialog(1000), .. Burst(5100, 4)], 9000, (5000, "\u001b[B")), PaneStatus.NeedsInput);
Check("a focus report keeps it",
    Run([.. Burst(0, 10), .. Dialog(1000), .. Burst(5100, 4)], 9000, (5000, "\u001b[I")), PaneStatus.NeedsInput);

// Answered: the command runs (amber), then the turn ends (green).
Check("answered: working while it runs",
    Run([.. Burst(0, 10), .. Dialog(1000), .. Burst(5100, 10)], 5600, (5000, "\r")), PaneStatus.Working);
Check("answered: done when the turn ends",
    Run([.. Burst(0, 10), .. Dialog(1000), .. Burst(5100, 10)], 9000, (5000, "\r")), PaneStatus.Done);
Check("answered no: done, though little is drawn after",
    Run([.. Burst(0, 10), .. Dialog(1000), (5100, "  ⎿  Interrupted\r\n")], 9000, (5000, "\u001b")), PaneStatus.Done);
Check("a second question after the answer needs you again",
    Run([.. Burst(0, 10), .. Dialog(1000), .. Burst(5100, 10), .. Dialog(6000)], 12000, (5000, "1")), PaneStatus.NeedsInput);

// Typing a prompt: each key's echo is output, but not work. Quick typing, then a pause.
{
    var typed = Enumerable.Range(0, 30).Select(i => (At: 6000 + i * 120, Keys: "a")).ToArray();
    var echo = typed.Select(k => (k.At + 30, "\u001b[2Ka")).ToArray();
    Check("typing a prompt: not working while you type, so no Done after",
        Run([.. Burst(0, 10), .. echo], 8000, typed), PaneStatus.Done);
    Check("sending it: working once the reply streams",
        Run([.. Burst(0, 10), .. echo, .. Burst(10500, 10)], 11000, [.. typed, (10200, "\r")]), PaneStatus.Working);
}

// Codex, once its transcript is known: a turn waiting for approval still writes lines.
var asked = new DateTime(2026, 10, 16, 12, 0, 0);
Check("codex: a line written while the approval is open keeps it",
    TerminalPane.FromTurn(PaneStatus.NeedsInput, TurnState.Working, asked.AddSeconds(3), asked, answered: false), null);
Check("codex: answered, the next line is working",
    TerminalPane.FromTurn(PaneStatus.NeedsInput, TurnState.Working, asked.AddSeconds(3), asked, answered: true), PaneStatus.Working);
Check("codex: answered, a finished turn is done",
    TerminalPane.FromTurn(PaneStatus.NeedsInput, TurnState.Done, asked.AddSeconds(9), asked, answered: true), PaneStatus.Done);

Console.WriteLine(fail == 0 ? "\nall checks passed" : $"\n{fail} FAILED");
return fail;

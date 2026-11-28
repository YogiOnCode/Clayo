#:property TargetFramework=net10.0-windows
#:project ../src/Clayo/CcxShell.csproj
using CcxShell.Core;

// Voice input: the turns of a dictation (VoiceFlow) with a made-up clock, and the text as it
// is typed. No microphone and no model.
int fail = 0;
void Check<T>(string name, T got, T want)
{
    var ok = EqualityComparer<T>.Default.Equals(got, want);
    if (!ok) fail++;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}  got={got} want={want}");
}

// ---- the turns
var pane = new object();
var other = new object();

var flow = new VoiceFlow();
Check("idle to begin with", (flow.State, flow.Target), (VoiceState.Idle, (object?)null));
Check("a press starts listening", flow.Toggle(pane, 0), VoiceStep.Start);
Check("  for that session", (flow.State, flow.Target), (VoiceState.Listening, (object?)pane));
Check("a second press stops and transcribes", flow.Toggle(pane, 5000), VoiceStep.Stop);
Check("  still that session's", (flow.State, flow.Target), (VoiceState.Transcribing, (object?)pane));
Check("a press while transcribing does nothing", flow.Toggle(other, 5100), VoiceStep.None);
Check("  and the take stays the first session's", flow.Target, (object?)pane);
Check("Esc while transcribing does nothing", flow.Cancel(), VoiceStep.None);
flow.Done();
Check("idle once the text is typed", (flow.State, flow.Target), (VoiceState.Idle, (object?)null));

flow = new VoiceFlow();
flow.Toggle(pane, 0);
Check("the press from another pane stops the take too", flow.Toggle(other, 4000), VoiceStep.Stop);
Check("  and the text goes where it was started", flow.Target, (object?)pane);

flow = new VoiceFlow();
flow.Toggle(pane, 0);
Check("Esc while listening throws the take away", flow.Cancel(), VoiceStep.Drop);
Check("  idle again", flow.State, VoiceState.Idle);
Check("Esc while idle does nothing", flow.Cancel(), VoiceStep.None);

flow = new VoiceFlow();
flow.Toggle(pane, 1000);
Check("before the limit: still listening", flow.Tick(1000 + VoiceFlow.MaxMs - 1), VoiceStep.None);
Check("at the limit: stops by itself", flow.Tick(1000 + VoiceFlow.MaxMs), VoiceStep.Stop);
Check("  and is transcribed", flow.State, VoiceState.Transcribing);
Check("a tick while transcribing does nothing", flow.Tick(1000 + 2 * VoiceFlow.MaxMs), VoiceStep.None);
flow.Done();
Check("a tick while idle does nothing", flow.Tick(long.MaxValue), VoiceStep.None);

// ---- the text
Check("trimmed", VoiceFlow.Clean("  Fix the login test.  "), "Fix the login test.");
Check("one line", VoiceFlow.Clean("first line\r\nsecond\tline"), "first line second line");
Check("nothing a terminal would act on", VoiceFlow.Clean("run\u001b[2Jthis\u0003 now\r"), "run [2Jthis now");
Check("nothing said", VoiceFlow.Clean(" \n "), "");
Check("null is nothing said", VoiceFlow.Clean(null), "");
Check("other languages pass", VoiceFlow.Clean("Grüße aus Köln"), "Grüße aus Köln");

Console.WriteLine(fail == 0 ? "\nall checks passed" : $"\n{fail} FAILED");
return fail;

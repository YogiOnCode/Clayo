#:property TargetFramework=net8.0-windows
#:project ../CcxShell.csproj
using CcxShell.Core;

// 1920x1080 at 100%: centre x is 960, the zone runs 840..1080, the edge is rows 0 and 1.
var primary = new PxRect(0, 0, 1920, 1080);
var rest = new IslandInput(960, 0, primary, 1.0, false, false, false, false, 0);

// Feeds the same input every 50 ms from `from` to `to` inclusive, like the real poll.
static IslandState Hold(IslandTrigger t, IslandInput i, long from, long to)
{
    var s = t.State;
    for (long ms = from; ms <= to; ms += 50) s = t.Update(i with { NowMs = ms });
    return s;
}

// A trigger that is already peeking, for the leave and busy scenarios.
IslandTrigger Peeking()
{
    var t = new IslandTrigger();
    Hold(t, rest, 0, 600);
    return t;
}

int fail = 0;
void Check<T>(string name, T got, T want)
{
    var ok = EqualityComparer<T>.Default.Equals(got, want);
    if (!ok) fail++;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}  got={got} want={want}");
}

{
    var t = new IslandTrigger();
    Hold(t, rest, 0, 550);
    Check("not yet at 599 ms", t.Update(rest with { NowMs = 599 }), IslandState.Hidden);
    Check("peeks after a 600 ms dwell", t.Update(rest with { NowMs = 600 }), IslandState.Peek);
}

{
    var t = new IslandTrigger();
    Hold(t, rest, 0, 500);
    t.Update(rest with { CursorY = 40, NowMs = 550 });
    Check("leaving before 600 ms cancels the dwell", Hold(t, rest, 600, 1100), IslandState.Hidden);
    Check("and the next dwell starts from zero", t.Update(rest with { NowMs = 1200 }), IslandState.Peek);
}

Check("left button held (window drag) never peeks",
    Hold(new IslandTrigger(), rest with { ButtonDown = true }, 0, 2000), IslandState.Hidden);

Check("move/size mode never peeks",
    Hold(new IslandTrigger(), rest with { MovingOrSizing = true }, 0, 2000), IslandState.Hidden);

{
    // Releasing the button at the edge (a snap that just finished) must not count the time it was held.
    var t = new IslandTrigger();
    Hold(t, rest with { ButtonDown = true }, 0, 1000);
    Check("held time does not count toward the dwell", t.Update(rest with { NowMs = 1050 }), IslandState.Hidden);
}

Check("busy never peeks",
    Hold(new IslandTrigger(), rest with { Busy = true }, 0, 2000), IslandState.Hidden);

{
    var t = Peeking();
    Check("busy hides an active peek", t.Update(rest with { Busy = true, NowMs = 700 }), IslandState.Hidden);
}

// Zone edges on the primary: |x - 960| <= 120.
Check("x at the zone's right edge peeks",
    Hold(new IslandTrigger(), rest with { CursorX = 1080 }, 0, 600), IslandState.Peek);
Check("x just outside the 240 px zone never peeks",
    Hold(new IslandTrigger(), rest with { CursorX = 1081 }, 0, 2000), IslandState.Hidden);
Check("x far left never peeks",
    Hold(new IslandTrigger(), rest with { CursorX = 100 }, 0, 2000), IslandState.Hidden);

Check("y = 1 (second row) peeks",
    Hold(new IslandTrigger(), rest with { CursorY = 1 }, 0, 600), IslandState.Peek);
Check("y = 2 never peeks",
    Hold(new IslandTrigger(), rest with { CursorY = 2 }, 0, 2000), IslandState.Hidden);

{
    var t = Peeking();
    var away = rest with { CursorY = 300 };
    t.Update(away with { NowMs = 1000 });
    Check("still there 399 ms after leaving", t.Update(away with { NowMs = 1399 }), IslandState.Peek);
    Check("hidden 400 ms after leaving", t.Update(away with { NowMs = 1400 }), IslandState.Hidden);
}

{
    // Out of the 2 px edge but over the island's 52 px body: it has to stay.
    var t = Peeking();
    var onIsland = rest with { CursorY = 30, OverIsland = true };
    Check("staying over the island keeps it", Hold(t, onIsland, 650, 5000), IslandState.Peek);
    t.Update(rest with { CursorY = 300, NowMs = 5050 });
    t.Update(onIsland with { NowMs = 5300 });
    Check("coming back before 400 ms resets the leave timer",
        Hold(t, rest with { CursorY = 300 }, 5350, 5700), IslandState.Peek);
}

{
    var t = Peeking();
    t.Dismiss();
    Check("dismissed while resting stays hidden", Hold(t, rest, 700, 3000), IslandState.Hidden);
    t.Update(rest with { CursorY = 300, NowMs = 3050 });
    Check("re-arms once the cursor has left", Hold(t, rest, 3100, 3700), IslandState.Peek);
}

{
    // A 2560x1440 monitor at 150%, right of the primary and 200 px higher. Centre x is
    // 1920 + 1280 = 3200; the zone is 240 * 1.5 = 360 px wide, so 3020..3380; the edge is y -200..-199.
    var second = new PxRect(1920, -200, 4480, 1240);
    var s = new IslandInput(3200, -200, second, 1.5, false, false, false, false, 0);
    Check("second monitor: centre peeks", Hold(new IslandTrigger(), s, 0, 600), IslandState.Peek);
    Check("second monitor: zone is scaled (3380 peeks)",
        Hold(new IslandTrigger(), s with { CursorX = 3380 }, 0, 600), IslandState.Peek);
    Check("second monitor: 3381 is outside",
        Hold(new IslandTrigger(), s with { CursorX = 3381 }, 0, 2000), IslandState.Hidden);
    Check("second monitor: 3020 peeks (an unscaled zone would stop at 3080)",
        Hold(new IslandTrigger(), s with { CursorX = 3020 }, 0, 600), IslandState.Peek);
    Check("second monitor: its own top counts, not y = 0",
        Hold(new IslandTrigger(), s with { CursorY = 0 }, 0, 2000), IslandState.Hidden);
    Check("second monitor: y = -199 peeks",
        Hold(new IslandTrigger(), s with { CursorY = -199 }, 0, 600), IslandState.Peek);
}

// ------------------------------------------------------------------ notifications

// The cursor somewhere in the page, nowhere near the island.
var desk = rest with { CursorY = 500 };
object paneA = new(), paneB = new();
var needsA = new IslandNote(paneA, "api-refactor", NoteKind.NeedsYou);
var doneA = new IslandNote(paneA, "api-refactor", NoteKind.Done);
var doneB = new IslandNote(paneB, "docs-pass", NoteKind.Done);
var errorB = new IslandNote(paneB, "docs-pass", NoteKind.Error);

{
    var t = new IslandTrigger();
    Hold(t, desk, 0, 1000);
    t.Notify(needsA);
    Check("notify from hidden", t.Update(desk with { NowMs = 1050 }), IslandState.Notify);
    Check("and it shows that note", t.Note, needsA);
    Check("needs-you stays for a minute", Hold(t, desk, 1100, 61100), IslandState.Notify);
    t.Resolve(paneA);
    Check("leaves once the session moves on", t.Update(desk with { NowMs = 61150 }), IslandState.Hidden);
}

{
    var t = new IslandTrigger();
    t.Notify(errorB);
    Hold(t, desk, 0, 30000);
    t.Dismiss();
    Check("error stays until dismissed, then goes", t.Update(desk with { NowMs = 30050 }), IslandState.Hidden);
    Check("and does not come back", Hold(t, desk, 30100, 32000), IslandState.Hidden);
}

{
    var t = Peeking();
    t.Notify(doneA);
    Check("notify while peeking replaces the peek", t.Update(rest with { NowMs = 700 }), IslandState.Notify);
}

{
    // Shown at 1000; the poll lands on 4999 and 5000 to pin the edge.
    var t = new IslandTrigger();
    t.Notify(doneA);
    t.Update(desk with { NowMs = 1000 });
    Hold(t, desk, 1050, 4950);
    Check("done still there at 3.999 s", t.Update(desk with { NowMs = 4999 }), IslandState.Notify);
    Check("done leaves at 4 s", t.Update(desk with { NowMs = 5000 }), IslandState.Hidden);
}

{
    var t = new IslandTrigger();
    var over = desk with { CursorY = 30, OverIsland = true };
    t.Notify(doneA);
    t.Update(desk with { NowMs = 0 });
    Check("hovering holds a done past 4 s", Hold(t, over, 3000, 9000), IslandState.Notify);
    t.Update(desk with { NowMs = 9050 });
    Check("still there 399 ms after the cursor leaves", t.Update(desk with { NowMs = 9449 }), IslandState.Notify);
    Check("gone 400 ms after the cursor leaves", t.Update(desk with { NowMs = 9450 }), IslandState.Hidden);
}

{
    // Fullscreen: nothing shows, and the note comes out when it ends.
    var t = new IslandTrigger();
    var busy = desk with { Busy = true };
    t.Notify(needsA);
    Check("busy holds a note", Hold(t, busy, 0, 5000), IslandState.Hidden);
    Check("and shows it when busy clears", t.Update(desk with { NowMs = 5050 }), IslandState.Notify);
    Check("held note is the one queued", t.Note, needsA);
}

{
    // A Done held behind a game gets its full 4 s once it is on screen.
    var t = new IslandTrigger();
    t.Notify(doneA);
    Hold(t, desk with { Busy = true }, 0, 10000);
    t.Update(desk with { NowMs = 10050 });
    Check("held done shown for 4 s from release", t.Update(desk with { NowMs = 14000 }), IslandState.Notify);
    Check("then leaves", t.Update(desk with { NowMs = 14050 }), IslandState.Hidden);
}

{
    var t = new IslandTrigger();
    t.Notify(doneA);
    t.Update(desk with { NowMs = 0 });
    Check("busy hides a showing note", t.Update(desk with { Busy = true, NowMs = 50 }), IslandState.Hidden);
}

{
    // Several pending: the urgent one first, even though the Done is newer.
    var t = new IslandTrigger();
    t.Notify(errorB);
    t.Notify(doneA);
    Hold(t, desk with { Busy = true }, 0, 1000);
    t.Update(desk with { NowMs = 1050 });
    Check("urgent before done", t.Note, errorB);
    t.Dismiss();
    t.Update(desk with { NowMs = 1100 });
    Check("then the done", t.Note, doneA);
}

{
    // Two urgent: the newest first.
    var t = new IslandTrigger();
    t.Notify(new IslandNote(paneB, "docs-pass", NoteKind.NeedsYou));
    t.Notify(needsA);
    t.Update(desk with { NowMs = 0 });
    Check("newest urgent first", t.Note, needsA);
}

{
    var t = new IslandTrigger();
    t.Notify(doneA);
    t.Update(desk with { NowMs = 0 });
    t.Notify(needsA);
    t.Update(desk with { NowMs = 50 });
    Check("a newer note from the same session replaces it", t.Note, needsA);
    t.Notify(doneB);
    t.Update(desk with { NowMs = 100 });
    Check("a done does not push aside a needs-you", t.Note, needsA);
}

{
    // The session got an answer while the game ran: nothing to replay.
    var t = new IslandTrigger();
    t.Notify(needsA);
    Hold(t, desk with { Busy = true }, 0, 1000);
    t.Resolve(paneA);
    Check("stale held note dropped", Hold(t, desk, 1050, 2000), IslandState.Hidden);
}

{
    // With Clayo in front the sidebar says it: a Done goes, a needs-you waits.
    var t = new IslandTrigger();
    t.Notify(doneB);
    t.Notify(needsA);
    Hold(t, desk with { ClayoActive = true }, 0, 1000);
    t.Update(desk with { NowMs = 1050 });
    Check("clayo active: needs-you held", t.Note, needsA);
    t.Dismiss();
    Check("clayo active: done dropped", Hold(t, desk, 1100, 2000), IslandState.Hidden);
}

{
    var t = new IslandTrigger();
    t.Notify(needsA);
    t.Update(desk with { NowMs = 0 });
    Check("clayo active hides a showing note",
        t.Update(desk with { ClayoActive = true, NowMs = 50 }), IslandState.Hidden);
}

{
    var t = new IslandTrigger();
    t.Notify(needsA);
    t.Update(rest with { NowMs = 0 });
    t.Dismiss();
    Check("dismissed note at the edge does not turn into a peek", Hold(t, rest, 50, 3000), IslandState.Hidden);
}

// ------------------------------------------------------------------ dwell bar

{
    var t = new IslandTrigger();
    t.Update(rest with { NowMs = 0 });
    Check("dwell progress 0 at the start", t.DwellProgress, 0.0);
    t.Update(rest with { NowMs = 300 });
    Check("dwell progress halfway at 300 ms", t.DwellProgress, 0.5);
    t.Update(rest with { NowMs = 450 });
    Check("dwell progress 0.75 at 450 ms", t.DwellProgress, 0.75);
    t.Update(rest with { NowMs = 600 });
    Check("dwell progress 0 once peeking", t.DwellProgress, 0.0);
}

{
    var t = new IslandTrigger();
    Hold(t, rest, 0, 300);
    t.Update(rest with { CursorY = 40, NowMs = 350 });
    Check("dwell progress 0 the moment the cursor leaves", t.DwellProgress, 0.0);
    t.Update(rest with { ButtonDown = true, NowMs = 400 });
    Check("dwell progress 0 with a button held", t.DwellProgress, 0.0);
    t.Update(rest with { Busy = true, NowMs = 450 });
    Check("dwell progress 0 while busy", t.DwellProgress, 0.0);
}

{
    var t = new IslandTrigger();
    Hold(t, desk, 0, 1000);
    Check("dwell progress 0 away from the edge", t.DwellProgress, 0.0);
}

Console.WriteLine(fail == 0 ? "\nall checks passed" : $"\n{fail} FAILED");
return fail;

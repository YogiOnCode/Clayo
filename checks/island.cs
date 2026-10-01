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
void Check(string name, IslandState got, IslandState want)
{
    var ok = got == want;
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

Console.WriteLine(fail == 0 ? "\nall checks passed" : $"\n{fail} FAILED");
return fail;

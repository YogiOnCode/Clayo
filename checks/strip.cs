#:property TargetFramework=net8.0-windows
#:project ../src/Clayo/CcxShell.csproj
using CcxShell.Core;

int fail = 0;
void Check<T>(string name, T got, T want)
{
    var ok = EqualityComparer<T>.Default.Equals(got, want);
    if (!ok) fail++;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}  got={got} want={want}");
}

var now = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
var at = now.UtcDateTime;
var all = new ClayoSettings();

// The design's sample session: Opus 5.5 1M, 340k of 1M, xhigh; 5h at 62%, 7d at 14%.
var status = new SessionStatus("3f136615-e5ba-45b2-9766-6cc4d88ef5d9", "Opus 5.5 (1M context)", @"C:\work",
    340_012, 1_000_000, 34, "xhigh",
    new Limit(62, now.AddHours(1).AddMinutes(20)), new Limit(14, now.AddDays(4).AddHours(10)), at);
var git = new GitStatus("feature/upgrade-clayo-v1", 12, 3);
var limits = new AccountLimits(status.FiveHour, status.SevenDay, at);

// Colour bands: green under 50, yellow 50 to 69, orange 70 to 89, red from 90.
Check("band 0", StatusStrip.BandFor(0), Band.Green);
Check("band 49", StatusStrip.BandFor(49), Band.Green);
Check("band 50", StatusStrip.BandFor(50), Band.Yellow);
Check("band 69", StatusStrip.BandFor(69), Band.Yellow);
Check("band 70", StatusStrip.BandFor(70), Band.Orange);
Check("band 89", StatusStrip.BandFor(89), Band.Orange);
Check("band 90", StatusStrip.BandFor(90), Band.Red);
Check("band over 100", StatusStrip.BandFor(104), Band.Red);

// Token counts as the user's script writes them, with the design's capital M.
Check("tokens under a thousand", StatusStrip.Tokens(999), "999");
Check("tokens in k", StatusStrip.Tokens(340_012), "340k");
Check("tokens 200k", StatusStrip.Tokens(200_000), "200k");
Check("tokens a round million", StatusStrip.Tokens(1_000_000), "1M");
Check("tokens a million and a half", StatusStrip.Tokens(1_500_000), "1.5M");

// The model's "(1M context)" folds into " 1M", as in the user's script and the design.
Check("model with a context size", StatusStrip.ModelName("Opus 5.5 (1M context)"), "Opus 5.5 1M");
Check("model with a k size", StatusStrip.ModelName("Sonnet 5.5 (200k context)"), "Sonnet 5.5 200k");
Check("model without one", StatusStrip.ModelName("Haiku 4.5"), "Haiku 4.5");

// Reset countdowns, the user's script's format.
Check("reset in hours", StatusStrip.ResetIn(now.AddHours(1).AddMinutes(20), now), "1h20m");
Check("reset in hours pads minutes", StatusStrip.ResetIn(now.AddHours(3).AddMinutes(5), now), "3h05m");
Check("reset in days", StatusStrip.ResetIn(now.AddDays(4).AddHours(10), now), "4d10h");
Check("reset in minutes", StatusStrip.ResetIn(now.AddMinutes(20).AddSeconds(30), now), "20m");
Check("reset passed", StatusStrip.ResetIn(now.AddMinutes(-1), now), "now");

{
    var s = StatusStrip.For(status, git, limits, all, now)!;
    Check("model", s.Model, "Opus 5.5 1M");
    Check("branch", s.Git, git);
    Check("context label", s.Context?.Label, "ctx");
    Check("context numbers", s.Context?.Detail, "340k/1M");
    Check("context percent", s.Context?.Percent, 34);
    Check("context band", s.Context?.Band, Band.Green);
    Check("context tooltip", s.Context?.Tip, "Context: 340k of 1M tokens used (34%)");
    Check("effort", s.Effort, "xhigh");
    Check("limits stay in the footer while it is on", (s.FiveHour, s.SevenDay), ((Meter?)null, (Meter?)null));
}

{
    // Footer off: the limits move into the header (D5).
    var s = StatusStrip.For(status, git, limits, all with { StatusFooter = false }, now)!;
    Check("5h label", s.FiveHour?.Label, "5h");
    Check("5h reset", s.FiveHour?.Detail, "\u21BB 1h20m");
    Check("5h band", s.FiveHour?.Band, Band.Yellow);
    Check("5h tooltip", s.FiveHour?.Tip, "5-hour limit: 62% used, resets in 1h20m");
    Check("7d tooltip", s.SevenDay?.Tip, "7-day limit: 14% used, resets in 4d10h");
    Check("a limit unticked stays out", StatusStrip.For(status, git, limits,
        all with { StatusFooter = false, StatusSevenDay = false }, now)!.SevenDay, null);
    Check("no limits reported yet shows none", StatusStrip.For(status, git, null,
        all with { StatusFooter = false }, now)!.FiveHour, null);
    Check("a limit without a reset time has no countdown",
        StatusStrip.LimitMeter(true, new Limit(5, null), now).Detail, null);
}

// The bare reset time, for the themes that word it "resets in 1h20m" or show it without ↻.
Check("a limit's reset alone", StatusStrip.Footer(limits, all, now)[0].Reset, "1h20m");
Check("context has no reset", StatusStrip.For(status, git, limits, all, now)!.Context?.Reset, null);

// Effort as a step of five, for the themes that draw it as rising bars.
Check("effort low", StatusStrip.EffortStep("low"), 0);
Check("effort medium", StatusStrip.EffortStep("medium"), 1);
Check("effort med", StatusStrip.EffortStep("med"), 1);
Check("effort xhigh", StatusStrip.EffortStep("xhigh"), 3);
Check("effort max", StatusStrip.EffortStep("max"), 4);
Check("effort unknown shows no steps", StatusStrip.EffortStep("turbo"), -1);

// The sidebar footer's limits (step 4).
{
    var f = StatusStrip.Footer(limits, all, now);
    Check("footer shows 5h then 7d", string.Join(" ", f.Select(m => m.Label)), "5h 7d");
    Check("footer 5h reset", f[0].Detail, "↻ 1h20m");
    Check("footer 7d band", f[1].Band, Band.Green);
    Check("a current limit is not stale", f[0].Stale, false);
    Check("footer off shows none there", StatusStrip.Footer(limits, all with { StatusFooter = false }, now).Count, 0);
    Check("no limits reported yet shows no footer", StatusStrip.Footer(null, all, now).Count, 0);
    Check("an unticked limit stays out of the footer",
        StatusStrip.Footer(limits, all with { StatusFiveHour = false }, now).Single().Label, "7d");
    Check("the header off leaves the footer as it is", StatusStrip.Footer(limits, all with { StatusHeader = false }, now).Count, 2);

    // Past its reset time the number belongs to the old window until Claude reports again.
    var later = now.AddHours(2);
    var stale = StatusStrip.Footer(limits, all, later);
    Check("a limit past its reset is stale", stale[0].Stale, true);
    Check("a stale limit says so", stale[0].Tip, "5-hour limit: reset since the last report, which said 62%");
    Check("a stale limit has no countdown", stale[0].Detail, null);
    Check("the 7d limit is still current then", stale[1].Stale, false);
    Check("the countdown ticks without new data", StatusStrip.Footer(limits, all, now.AddMinutes(30))[0].Detail, "↻ 50m");
}

// Reserve (step 6): at or past the threshold a limit reads red, and warns once per window.
{
    var at80 = all with { ReserveAt = 80 };
    var near = new AccountLimits(new Limit(82, now.AddHours(1)), new Limit(14, now.AddDays(4)), at);
    var f = StatusStrip.Footer(near, at80, now);
    Check("past the reserve reads red", f[0].Band, Band.Red);
    Check("under the reserve keeps its band", f[1].Band, Band.Green);
    Check("the tooltip says why it is red", f[0].Tip, "5-hour limit: 82% used, resets in 1h00m. Past your 80% reserve");
    Check("reserve off keeps the band", StatusStrip.Footer(near, all, now)[0].Band, Band.Orange);
    Check("a limit the reserve does not watch keeps its band",
        StatusStrip.Footer(near, at80 with { ReserveFiveHour = false }, now)[0].Band, Band.Orange);
    Check("the reserve shows in the header too (D5)",
        StatusStrip.For(status, git, near, at80 with { StatusFooter = false }, now)!.FiveHour?.Band, Band.Red);
    Check("a stale limit is not past the reserve", StatusStrip.Footer(near, at80, now.AddHours(2))[0].Band, Band.Orange);

    var watch = new ReserveWatch();
    Check("crossing warns", string.Join(",", watch.Crossed(near, at80, now).Select(m => m.Label)), "5h");
    Check("once per window", watch.Crossed(near, at80, now).Count, 0);
    Check("nothing reported warns nothing", watch.Crossed(null, at80, now).Count, 0);
    var next = near with { FiveHour = new Limit(85, now.AddHours(6)) };
    Check("a new window warns again", string.Join(",", watch.Crossed(next, at80, now.AddHours(1.5)).Select(m => m.Label)), "5h");
    var both = new AccountLimits(new Limit(91, now.AddHours(1)), new Limit(90, now.AddDays(4)), at);
    Check("both can cross at once", string.Join(",", new ReserveWatch().Crossed(both, all with { ReserveAt = 90 }, now).Select(m => m.Label)), "5h,7d");
    Check("reserve off warns nothing", new ReserveWatch().Crossed(both, all, now).Count, 0);
    Check("stale limits warn nothing", new ReserveWatch().Crossed(near, at80, now.AddHours(2)).Count, 0);
}

Check("near limit: context orange", StatusStrip.For(status with { ContextUsed = 810_000, ContextPercent = 81 },
    git, limits, all, now)!.Context?.Band, Band.Orange);

// Nothing to show is an empty strip, never zeros.
Check("no status yet is no strip", StatusStrip.For(null, git, limits, all, now), null);
Check("header off is no strip", StatusStrip.For(status, git, limits, all with { StatusHeader = false }, now), null);
Check("every field off is no strip", StatusStrip.For(status, git, limits,
    all with { StatusModel = false, StatusBranch = false, StatusContext = false, StatusEffort = false }, now), null);

{
    var s = StatusStrip.For(status, null, limits, all with { StatusModel = false, StatusEffort = false }, now)!;
    Check("an unticked field is left out", (s.Model, s.Effort), ((string?)null, (string?)null));
    Check("not a repo is no branch", s.Git, null);
    Check("the rest still shows", s.Context?.Percent, 34);
}
Check("a session without a model name shows none",
    StatusStrip.For(status with { Model = null }, git, limits, all, now)!.Model, null);

Console.WriteLine(fail == 0 ? "\nall checks passed" : $"\n{fail} FAILED");
return fail;

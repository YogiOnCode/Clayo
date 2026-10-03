using System.Globalization;
using System.Text.RegularExpressions;

namespace CcxShell.Core;

/// <summary>How full a meter is, on the design's scale: green under 50, yellow to 69, orange to 89, red from 90.</summary>
public enum Band { Green, Yellow, Orange, Red }

/// <summary>
/// A percentage in the strip or the footer: its label, the numbers beside it (if any) and its
/// tooltip. Stale is a limit past its reset that Claude has not reported on since; Reserve is
/// one at or past the threshold you set, which reads red whatever its band. Reset is a limit's
/// time to its reset alone ("1h20m"), for the themes that word it their own way.
/// </summary>
public sealed record Meter(string Label, string? Detail, int Percent, string Tip, bool Stale = false, bool Reserve = false,
                          string? Reset = null)
{
    public Band Band => Reserve ? Band.Red : StatusStrip.BandFor(Percent);
}

/// <summary>What the pane header strip shows for one session, in order. A null part is not shown.</summary>
public sealed record Strip(string? Model, GitStatus? Git, Meter? Context, string? Effort, Meter? FiveHour, Meter? SevenDay);

/// <summary>
/// Turns what a session's status line was handed (StatusStore) and its folder's git state
/// (GitInfo) into the strip's text, as design/settings/settings-prototype.html words it.
/// Which theme draws it (StatusTheme) is the view's business.
/// </summary>
public static class StatusStrip
{
    /// <summary>
    /// Null when there is nothing to show: no status yet, the header switched off, or every
    /// field unticked. An empty strip, never zeros.
    /// </summary>
    public static Strip? For(SessionStatus? status, GitStatus? git, AccountLimits? limits,
                             ClayoSettings settings, DateTimeOffset now)
    {
        if (status is null || !settings.StatusHeader) return null;

        // With the footer off the account's limits ride in the header instead (D5).
        bool limitsHere = !settings.StatusFooter;
        var strip = new Strip(
            settings.StatusModel && status.Model is { } m ? ModelName(m) : null,
            settings.StatusBranch ? git : null,
            settings.StatusContext ? ContextMeter(status) : null,
            settings.StatusEffort ? status.Effort : null,
            limitsHere && settings.StatusFiveHour && limits?.FiveHour is { } h5 ? LimitMeter(true, h5, now, ReserveFor(settings, true)) : null,
            limitsHere && settings.StatusSevenDay && limits?.SevenDay is { } d7 ? LimitMeter(false, d7, now, ReserveFor(settings, false)) : null);

        return strip == new Strip(null, null, null, null, null, null) ? null : strip;
    }

    private static Meter ContextMeter(SessionStatus s)
    {
        string used = Tokens(s.ContextUsed), size = Tokens(s.ContextSize);
        return new Meter("ctx", $"{used}/{size}", s.ContextPercent,
                         $"Context: {used} of {size} tokens used ({s.ContextPercent}%)");
    }

    /// <summary>The sidebar footer's limits, 5h then 7d: none while it is off or nothing has reported them.</summary>
    public static IReadOnlyList<Meter> Footer(AccountLimits? limits, ClayoSettings settings, DateTimeOffset now)
    {
        var meters = new List<Meter>();
        if (!settings.StatusFooter || limits is null) return meters;
        if (settings.StatusFiveHour && limits.FiveHour is { } h5) meters.Add(LimitMeter(true, h5, now, ReserveFor(settings, true)));
        if (settings.StatusSevenDay && limits.SevenDay is { } d7) meters.Add(LimitMeter(false, d7, now, ReserveFor(settings, false)));
        return meters;
    }

    /// <summary>A reserveAt of 0 is no reserve on this limit.</summary>
    public static Meter LimitMeter(bool fiveHour, Limit limit, DateTimeOffset now, int reserveAt = 0)
    {
        string name = fiveHour ? "5-hour" : "7-day", label = fiveHour ? "5h" : "7d";
        // Limits only change when Claude refreshes its status line, so past the reset the
        // number is the old window's, not the current use.
        if (limit.ResetsAt <= now)
            return new Meter(label, null, limit.Percent,
                             $"{name} limit: reset since the last report, which said {limit.Percent}%", Stale: true);

        var reset = limit.ResetsAt is { } at ? ResetIn(at, now) : null;
        bool past = PastReserve(limit, reserveAt, now);
        var tip = $"{name} limit: {limit.Percent}% used" + (reset is null ? "" : $", resets in {reset}")
                  + (past ? $". Past your {reserveAt}% reserve" : "");
        return new Meter(label, reset is null ? null : $"↻ {reset}", limit.Percent, tip, Reserve: past, Reset: reset);
    }

    /// <summary>The threshold watching this limit, or 0 when the reserve is off or not on it.</summary>
    public static int ReserveFor(ClayoSettings s, bool fiveHour) =>
        (fiveHour ? s.ReserveFiveHour : s.ReserveSevenDay) ? s.ReserveAt : 0;

    /// <summary>A stale limit is the old window's, so it is past nothing.</summary>
    public static bool PastReserve(Limit limit, int reserveAt, DateTimeOffset now) =>
        reserveAt > 0 && limit.Percent >= reserveAt && !(limit.ResetsAt <= now);

    public static Band BandFor(int percent) => percent switch
    {
        >= 90 => Band.Red,
        >= 70 => Band.Orange,
        >= 50 => Band.Yellow,
        _ => Band.Green,
    };

    /// <summary>Effort as a step of five, low to max, for the themes that draw it as rising bars. -1 for one not on the scale.</summary>
    public static int EffortStep(string effort) => effort switch
    {
        "low" => 0,
        "medium" or "med" => 1,
        "high" => 2,
        "xhigh" => 3,
        "max" => 4,
        _ => -1,
    };

    /// <summary>As the user's script writes token counts (340k, 1M, 1.5M), with the design's capital M.</summary>
    public static string Tokens(long n)
    {
        var inv = CultureInfo.InvariantCulture;
        if (n >= 1_000_000)
        {
            var m = Math.Round(n / 1_000_000.0, 1);
            return (Math.Abs(m - Math.Round(m)) < 0.05 ? m.ToString("F0", inv) : m.ToString("F1", inv)) + "M";
        }
        return n >= 1000 ? (n / 1000.0).ToString("F0", inv) + "k" : n.ToString(inv);
    }

    /// <summary>"Opus 5.5 (1M context)" reads "Opus 5.5 1M", as in the user's script.</summary>
    public static string ModelName(string displayName) =>
        Regex.Replace(displayName, @"\s*\((\d+\.?\d*[kKmM])\s+context\)", " $1").Trim();

    /// <summary>Time to a reset in the user's script's format: 4d10h, 1h20m, 20m, or now.</summary>
    public static string ResetIn(DateTimeOffset at, DateTimeOffset now)
    {
        var left = at - now;
        if (left <= TimeSpan.Zero) return "now";
        if (left.Days > 0) return $"{left.Days}d{left.Hours}h";
        if (left.Hours > 0) return $"{left.Hours}h{left.Minutes:00}m";
        return $"{left.Minutes}m";
    }
}

/// <summary>
/// Which limits have just gone past the reserve, so the island says so once per window rather
/// than on every status refresh. A limit's window is told apart by its reset time.
/// </summary>
public sealed class ReserveWatch
{
    private readonly HashSet<(bool FiveHour, DateTimeOffset? ResetsAt)> _warned = [];

    public IReadOnlyList<Meter> Crossed(AccountLimits? limits, ClayoSettings settings, DateTimeOffset now)
    {
        var crossed = new List<Meter>();
        foreach (var (fiveHour, limit) in new[] { (true, limits?.FiveHour), (false, limits?.SevenDay) })
        {
            int reserveAt = StatusStrip.ReserveFor(settings, fiveHour);
            if (limit is not null && StatusStrip.PastReserve(limit, reserveAt, now) && _warned.Add((fiveHour, limit.ResetsAt)))
                crossed.Add(StatusStrip.LimitMeter(fiveHour, limit, now, reserveAt));
        }
        return crossed;
    }
}

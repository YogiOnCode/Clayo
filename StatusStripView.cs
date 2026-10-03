using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using CcxShell.Core;

namespace CcxShell;

/// <summary>
/// The status strip beside a session's title, as design/settings/settings-prototype.html draws
/// it in each theme: model · branch +n −n · context · effort, then 5h and 7d while the footer
/// is off. Numbers rules each field off; Bars and Rings space them; Chips puts each in a pill.
/// </summary>
public sealed class StatusStripView : StackPanel
{
    private StatusTheme _theme;
    private int _fields;

    public StatusStripView()
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        // Too narrow a header cuts the strip off rather than squeezing the title or the buttons.
        ClipToBounds = true;
        Typography.SetNumeralAlignment(this, FontNumeralAlignment.Tabular);
        Visibility = Visibility.Collapsed;
    }

    /// <summary>Redraws from scratch: a handful of elements, and only when the data changes.</summary>
    public void Show(Strip? strip, StatusTheme theme)
    {
        Children.Clear();
        _theme = theme;
        _fields = 0;
        Visibility = strip is null ? Visibility.Collapsed : Visibility.Visible;
        if (strip is null) return;

        // The rule that sets the strip off from the title, in every theme.
        Children.Add(Rule(14, new Thickness(2, 0, 12, 0)));

        if (strip.Model is { } model)
            Field("Model", [Draw.Text(this, model, "TextPrimary")], chip: true);
        if (strip.Git is { } git)
        {
            var parts = new List<UIElement> { BranchGlyph(), Draw.Text(this, git.Branch, "TextPrimary", maxWidth: 190) };
            // Nothing uncommitted shows no counts, as in the user's own status line.
            if (git.Added + git.Deleted > 0)
            {
                parts.Add(Draw.Text(this, $"+{git.Added}", "DiffAdded"));
                parts.Add(Draw.Text(this, $"−{git.Deleted}", "DiffDeleted"));
            }
            Field("Branch and uncommitted diff", parts, chip: true);
        }
        if (strip.Context is { } ctx) MeterField(ctx);
        if (strip.Effort is { } effort) EffortField(effort);
        if (strip.FiveHour is { } h5) MeterField(h5);
        if (strip.SevenDay is { } d7) MeterField(d7);
    }

    private void MeterField(Meter m)
    {
        // Bars and Rings spell context out; the limits are short either way.
        string label = m.Label == "ctx" ? "Context" : m.Label;
        List<UIElement> parts = _theme switch
        {
            StatusTheme.Bars => [Draw.Text(this, label, "MutedAA"), Draw.Bar(this, m, width: 52), Draw.Percent(this, m)],
            StatusTheme.Rings => [Draw.Ring(this, m, 16, 2.5), Draw.Text(this, label, "MutedAA"), Draw.Percent(this, m)],
            StatusTheme.Chips => [Draw.Text(this, m.Label, "MutedAA"), Draw.Percent(this, m)],
            _ => [Draw.Text(this, m.Label, "MutedAA"), Draw.Percent(this, m)],
        };
        // Numbers puts "used/size" or "↻ 1h20m" before the percentage; Chips the bare reset after it.
        if (_theme == StatusTheme.Numbers && m.Detail is { } detail) parts.Insert(1, Draw.Text(this, detail, "MutedAA"));
        if (_theme == StatusTheme.Chips && m.Reset is { } reset) parts.Add(Draw.Text(this, reset, "MutedAA"));
        Draw.Describe(Field(m.Tip, parts, chip: true, tint: m), m);
    }

    private void EffortField(string effort)
    {
        var value = Draw.Text(this, effort, "TextPrimary");
        int step = StatusStrip.EffortStep(effort);
        List<UIElement> parts = _theme switch
        {
            StatusTheme.Chips => [Draw.Text(this, "effort", "MutedAA"), value],
            StatusTheme.Bars or StatusTheme.Rings when step >= 0 => [Draw.Steps(this, step), value],
            _ => [value],
        };
        Field($"Effort: {effort}", parts, chip: true);
    }

    /// <summary>
    /// One field, its parts 6 px apart: ruled off from the one before in Numbers, 14 px from it
    /// in Bars and Rings. In Chips it sits in a pill, 6 px from the one before, tinted by the
    /// meter's band when it has one.
    /// </summary>
    private FrameworkElement Field(string tip, List<UIElement> parts, bool chip = false, Meter? tint = null)
    {
        // A background, so the tooltip shows between the parts as well as over them.
        var row = Draw.Row(parts, 6);
        bool chips = chip && _theme == StatusTheme.Chips;
        FrameworkElement field = chips ? Draw.Chip(this, row, tint) : row;
        field.ToolTip = tip;

        if (_fields++ > 0)
        {
            if (_theme == StatusTheme.Numbers) Children.Add(Rule(12, new Thickness(14, 0, 14, 0)));
            else field.Margin = new Thickness(_theme == StatusTheme.Chips ? 6 : 14, 0, 0, 0);
        }
        Children.Add(field);
        return field;
    }

    private Rectangle Rule(double height, Thickness margin) => new()
    {
        Width = 1,
        Height = height,
        Margin = margin,
        Fill = (Brush)FindResource("StripRule"),
        VerticalAlignment = VerticalAlignment.Center,
        SnapsToDevicePixels = true,
    };

    /// <summary>The 16 px glyph drawn at 12 px, its 1.3 px stroke scaling with it, as the design's SVG does.</summary>
    private UIElement BranchGlyph() => new Viewbox
    {
        Width = 12,
        Height = 12,
        VerticalAlignment = VerticalAlignment.Center,
        Child = new Canvas
        {
            Width = 16,
            Height = 16,
            Children =
            {
                new Path
                {
                    Data = (Geometry)FindResource("BranchGlyph"),
                    Stroke = (Brush)FindResource("MutedAA"),
                    StrokeThickness = 1.3,
                },
            },
        },
    };
}

/// <summary>
/// The account's limits in the sidebar footer, as the design draws them per theme: Numbers in
/// two ruled columns, Bars as a row each, Rings and Chips as two cards side by side.
/// </summary>
public sealed class LimitsView : Grid
{
    public LimitsView()
    {
        Typography.SetNumeralAlignment(this, FontNumeralAlignment.Tabular);
        AutomationProperties.SetName(this, "Account limits");
        Visibility = Visibility.Collapsed;
    }

    /// <summary>Redraws from scratch; nothing to show hides the row, gap included.</summary>
    public void Show(IReadOnlyList<Meter> meters, StatusTheme theme)
    {
        Children.Clear();
        ColumnDefinitions.Clear();
        Visibility = meters.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        // Bars stack, a row each; the rest sit side by side in two equal columns.
        bool rows = theme == StatusTheme.Bars;
        if (!rows)
        {
            ColumnDefinitions.Add(new ColumnDefinition());
            ColumnDefinitions.Add(new ColumnDefinition());
        }

        var stack = new StackPanel();
        if (rows) Children.Add(stack);
        for (int i = 0; i < meters.Count; i++)
        {
            var m = meters[i];
            var cell = theme switch
            {
                StatusTheme.Bars => BarRow(m),
                StatusTheme.Rings => RingCell(m),
                StatusTheme.Chips => ChipCell(m),
                _ => NumbersCell(m),
            };
            Draw.Describe(cell, m);

            if (rows)
            {
                if (i > 0) cell.Margin = new Thickness(0, 8, 0, 0);
                stack.Children.Add(cell);
                continue;
            }

            FrameworkElement child = cell;
            if (i > 0)
            {
                // Numbers rules the second column off, 12 px either side; the cards keep a gap.
                if (theme == StatusTheme.Numbers)
                    child = new Border
                    {
                        BorderBrush = (Brush)FindResource("LimitRule"),
                        BorderThickness = new Thickness(1, 0, 0, 0),
                        Padding = new Thickness(12, 0, 0, 0),
                        Child = cell,
                    };
                child.Margin = new Thickness(theme switch { StatusTheme.Rings => 10, StatusTheme.Chips => 8, _ => 12 }, 0, 0, 0);
            }
            SetColumn(child, i);
            Children.Add(child);
        }
    }

    private static string LongName(Meter m) => m.Label == "5h" ? "5-hour" : "7-day";

    /// <summary>"5h 62% ↻ 1h20m".</summary>
    private FrameworkElement NumbersCell(Meter m)
    {
        var parts = new List<UIElement> { Draw.Text(this, m.Label, "MutedAA"), Draw.Percent(this, m) };
        if (m.Detail is { } detail) parts.Add(Draw.Text(this, detail, "MutedAA", size: 11.5));
        return Draw.Row(parts, 6);
    }

    /// <summary>"5-hour [bar] 62% 1h20m", in the design's 44 px | rest | 34 px | 48 px columns, 10 px apart.</summary>
    private FrameworkElement BarRow(Meter m)
    {
        var row = new Grid { Background = Brushes.Transparent };
        foreach (var w in new[] { new GridLength(44), new GridLength(1, GridUnitType.Star), new GridLength(44), new GridLength(58) })
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = w });

        var bar = Draw.Bar(this, m);
        var pct = Draw.Percent(this, m);
        pct.HorizontalAlignment = HorizontalAlignment.Right;
        var reset = Draw.Text(this, m.Reset ?? "", "MutedAA", size: 11.5);
        reset.HorizontalAlignment = HorizontalAlignment.Right;
        UIElement[] cells = [Draw.Text(this, LongName(m), "MutedAA"), bar, pct, reset];
        for (int c = 0; c < cells.Length; c++)
        {
            if (c > 0) ((FrameworkElement)cells[c]).Margin = new Thickness(10, 0, 0, 0);
            SetColumn(cells[c], c);
            row.Children.Add(cells[c]);
        }
        return row;
    }

    /// <summary>A 34 px ring with the number in it, then "5-hour limit" over "resets in 1h20m".</summary>
    private FrameworkElement RingCell(Meter m)
    {
        var number = Draw.Text(this, m.Percent.ToString(), Draw.BandKey(m.Band), size: 10.5);
        number.FontWeight = FontWeights.SemiBold;
        number.HorizontalAlignment = HorizontalAlignment.Center;
        var ring = Draw.Ring(this, m, 34, 3);
        ring.Children.Add(number);

        var words = new StackPanel { Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(Draw.Text(this, $"{LongName(m)} limit", "TextPrimary"));
        if (m.Reset is { } reset)
        {
            var sub = Draw.Text(this, $"resets in {reset}", "MutedAA", size: 11);
            sub.Margin = new Thickness(0, 2, 0, 0);
            words.Children.Add(sub);
        }
        // Docked rather than a row: the words get what is left of the column and trim there,
        // instead of running into the next card in a narrow sidebar.
        DockPanel.SetDock(ring, Dock.Left);
        return new DockPanel { Background = Brushes.Transparent, Children = { ring, words } };
    }

    /// <summary>A card tinted by the band: "5-hour" and the percentage, then "resets in 1h20m".</summary>
    private FrameworkElement ChipCell(Meter m)
    {
        var pct = Draw.Percent(this, m);
        pct.FontSize = 15;
        DockPanel.SetDock(pct, Dock.Right);
        var top = new DockPanel { LastChildFill = false };
        top.Children.Add(pct);
        top.Children.Add(Draw.Text(this, LongName(m), "MutedAA", size: 11.5));

        var body = new StackPanel();
        body.Children.Add(top);
        if (m.Reset is { } reset)
        {
            var sub = Draw.Text(this, $"resets in {reset}", "MutedAA", size: 11);
            sub.Margin = new Thickness(0, 1, 0, 0);
            body.Children.Add(sub);
        }
        return new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 7, 10, 7),
            BorderThickness = new Thickness(1),
            BorderBrush = Draw.Tint(this, m.Band, 0.30),
            Background = Draw.Tint(this, m.Band, 0.08),
            Child = body,
        };
    }
}

/// <summary>The pieces both views draw with, in the design's sizes and colours.</summary>
internal static class Draw
{
    /// <summary>A limit past its reset is dimmed: last known, not current.</summary>
    public const double StaleOpacity = 0.45;

    public static TextBlock Text(FrameworkElement host, string text, string brush,
                                 double maxWidth = double.PositiveInfinity, double size = 12) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = (Brush)host.FindResource(brush),
        VerticalAlignment = VerticalAlignment.Center,
        MaxWidth = maxWidth,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    public static string BandKey(Band band) => band switch
    {
        Band.Red => "BandRed",
        Band.Orange => "BandOrange",
        Band.Yellow => "BandYellow",
        _ => "BandGreen",
    };

    /// <summary>The percentage in its band's colour.</summary>
    public static TextBlock Percent(FrameworkElement host, Meter m)
    {
        var pct = Text(host, $"{m.Percent}%", BandKey(m.Band));
        pct.FontWeight = FontWeights.SemiBold;
        return pct;
    }

    /// <summary>The band's colour at a fraction of its strength, for a chip's rim and fill.</summary>
    public static Brush Tint(FrameworkElement host, Band band, double alpha)
    {
        var c = ((SolidColorBrush)host.FindResource(BandKey(band))).Color;
        var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(alpha * 255), c.R, c.G, c.B));
        brush.Freeze();
        return brush;
    }

    /// <summary>Parts side by side, gap px apart.</summary>
    public static StackPanel Row(IEnumerable<UIElement> parts, double gap)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent };
        foreach (var part in parts)
        {
            if (row.Children.Count > 0 && part is FrameworkElement fe) fe.Margin = new Thickness(gap, 0, 0, 0);
            row.Children.Add(part);
        }
        return row;
    }

    /// <summary>Tooltip, screen reader name and stale dimming for a meter's field or cell.</summary>
    public static void Describe(FrameworkElement el, Meter m)
    {
        el.ToolTip = m.Tip;
        AutomationProperties.SetName(el, m.Tip);
        if (m.Stale) el.Opacity = StaleOpacity;
    }

    /// <summary>A 4 px bar filled to the percentage in the band's colour; without a width it takes its column's.</summary>
    public static FrameworkElement Bar(FrameworkElement host, Meter m, double width = double.NaN)
    {
        double p = Math.Clamp(m.Percent, 0, 100);
        var bar = new Grid { Width = width, Height = 4, VerticalAlignment = VerticalAlignment.Center };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(p, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100 - p, GridUnitType.Star) });
        var track = new Border { CornerRadius = new CornerRadius(2), Background = (Brush)host.FindResource("MeterTrack") };
        Grid.SetColumnSpan(track, 2);
        bar.Children.Add(track);
        bar.Children.Add(new Border { CornerRadius = new CornerRadius(2), Background = (Brush)host.FindResource(BandKey(m.Band)) });
        return bar;
    }

    /// <summary>A ring gauge: a dark track, and the percentage swept clockwise from the top with round ends.</summary>
    public static Grid Ring(FrameworkElement host, Meter m, double size, double stroke)
    {
        var ring = new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        ring.Children.Add(new Ellipse { Stroke = (Brush)host.FindResource("MeterTrack"), StrokeThickness = stroke });

        double p = Math.Clamp(m.Percent, 0, 100);
        if (p > 0)
        {
            // Just short of a full turn at 100%: an arc cannot end where it starts.
            double mid = size / 2, r = (size - stroke) / 2, a = Math.Min(p / 100 * 2 * Math.PI, 2 * Math.PI - 0.001);
            var arc = new ArcSegment(new Point(mid + r * Math.Sin(a), mid - r * Math.Cos(a)), new Size(r, r), 0,
                                     a > Math.PI, SweepDirection.Clockwise, isStroked: true);
            ring.Children.Add(new Path
            {
                Data = new PathGeometry { Figures = { new PathFigure { StartPoint = new Point(mid, mid - r), Segments = { arc } } } },
                Stroke = (Brush)host.FindResource(BandKey(m.Band)),
                StrokeThickness = stroke,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            });
        }
        return ring;
    }

    /// <summary>Effort as five rising bars, lit up to its step (0 is low, 4 is max).</summary>
    public static FrameworkElement Steps(FrameworkElement host, int step)
    {
        var steps = new StackPanel { Orientation = Orientation.Horizontal, Height = 12, VerticalAlignment = VerticalAlignment.Center };
        for (int i = 0; i < 5; i++)
            steps.Children.Add(new Rectangle
            {
                Width = 3,
                Height = 4 + 2 * i,
                Margin = new Thickness(i > 0 ? 2 : 0, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Bottom,
                Fill = (Brush)host.FindResource(i <= step ? "StepOn" : "StepOff"),
            });
        return steps;
    }

    /// <summary>
    /// A 22 px pill around a field, its text a size smaller: neutral, or tinted by a meter's
    /// band so the fullness reads at a glance.
    /// </summary>
    public static Border Chip(FrameworkElement host, Panel content, Meter? tint)
    {
        foreach (var text in content.Children.OfType<TextBlock>()) text.FontSize = 11.5;
        return new Border
        {
            Height = 22,
            Padding = new Thickness(9, 0, 9, 0),
            CornerRadius = new CornerRadius(11),
            BorderThickness = new Thickness(1),
            BorderBrush = tint is null ? (Brush)host.FindResource("StripRule") : Tint(host, tint.Band, 0.34),
            Background = tint is null ? (Brush)host.FindResource("ChipBg") : Tint(host, tint.Band, 0.10),
            VerticalAlignment = VerticalAlignment.Center,
            Child = content,
        };
    }
}

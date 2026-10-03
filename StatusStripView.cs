using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using CcxShell.Core;

namespace CcxShell;

/// <summary>
/// The status strip beside a session's title, in the Numbers theme of
/// design/settings/settings-prototype.html: model · branch +n −n · ctx used/size n% · effort,
/// a rule between each.
/// </summary>
public sealed class StatusStripView : StackPanel
{
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
    public void Show(Strip? strip)
    {
        Children.Clear();
        Visibility = strip is null ? Visibility.Collapsed : Visibility.Visible;
        if (strip is null) return;

        // The rule that sets the strip off from the title.
        Children.Add(Rule(14, new Thickness(2, 0, 12, 0)));

        if (strip.Model is { } model)
            Field("Model", Text(this, model, "TextPrimary"));
        if (strip.Git is { } git)
        {
            var parts = new List<UIElement> { BranchGlyph(), Text(this, git.Branch, "TextPrimary", maxWidth: 190) };
            // Nothing uncommitted shows no counts, as in the user's own status line.
            if (git.Added + git.Deleted > 0)
            {
                parts.Add(Text(this, $"+{git.Added}", "DiffAdded"));
                parts.Add(Text(this, $"−{git.Deleted}", "DiffDeleted"));
            }
            Field("Branch and uncommitted diff", parts.ToArray());
        }
        if (strip.Context is { } ctx) MeterField(ctx);
        if (strip.Effort is { } effort)
            Field($"Effort: {effort}", Text(this, effort, "TextPrimary"));
        if (strip.FiveHour is { } h5) MeterField(h5);
        if (strip.SevenDay is { } d7) MeterField(d7);
    }

    private void MeterField(Meter m)
    {
        var parts = new List<UIElement> { Text(this, m.Label, "MutedAA") };
        if (m.Detail is { } detail) parts.Add(Text(this, detail, "MutedAA"));
        parts.Add(Percent(this, m));
        var field = Field(m.Tip, parts.ToArray());
        AutomationProperties.SetName(field, m.Tip);
        if (m.Stale) field.Opacity = StaleOpacity;
    }

    /// <summary>A limit past its reset is dimmed: last known, not current.</summary>
    internal const double StaleOpacity = 0.45;

    /// <summary>The percentage in its band's colour.</summary>
    internal static TextBlock Percent(FrameworkElement host, Meter m)
    {
        var pct = Text(host, $"{m.Percent}%", m.Band switch
        {
            Band.Red => "BandRed",
            Band.Orange => "BandOrange",
            Band.Yellow => "BandYellow",
            _ => "BandGreen",
        });
        pct.FontWeight = FontWeights.SemiBold;
        return pct;
    }

    /// <summary>One field, its parts 6 px apart, ruled off from the one before.</summary>
    private StackPanel Field(string tip, params UIElement[] parts)
    {
        if (Children.OfType<StackPanel>().Any()) Children.Add(Rule(12, new Thickness(14, 0, 14, 0)));

        // A background, so the tooltip shows between the parts as well as over them.
        var field = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent, ToolTip = tip };
        foreach (var part in parts)
        {
            if (field.Children.Count > 0 && part is FrameworkElement fe) fe.Margin = new Thickness(6, 0, 0, 0);
            field.Children.Add(part);
        }
        Children.Add(field);
        return field;
    }

    internal static TextBlock Text(FrameworkElement host, string text, string brush,
                                   double maxWidth = double.PositiveInfinity) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = (Brush)host.FindResource(brush),
        VerticalAlignment = VerticalAlignment.Center,
        MaxWidth = maxWidth,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

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
/// The account's limits in the sidebar footer, Numbers theme: "5h 62% ↻ 1h20m", then 7d, in
/// two equal columns with a rule between, as the design's footer row.
/// </summary>
public sealed class LimitsView : Grid
{
    public LimitsView()
    {
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition());
        Typography.SetNumeralAlignment(this, FontNumeralAlignment.Tabular);
        AutomationProperties.SetName(this, "Account limits");
        Visibility = Visibility.Collapsed;
    }

    /// <summary>Redraws from scratch; nothing to show hides the row, gap included.</summary>
    public void Show(IReadOnlyList<Meter> meters)
    {
        Children.Clear();
        Visibility = meters.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        for (int i = 0; i < meters.Count; i++)
        {
            var m = meters[i];
            var cell = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                // A background, so the tooltip shows between the parts as well as over them.
                Background = Brushes.Transparent,
                ToolTip = m.Tip,
                Opacity = m.Stale ? StatusStripView.StaleOpacity : 1,
            };
            AutomationProperties.SetName(cell, m.Tip);
            cell.Children.Add(StatusStripView.Text(this, m.Label, "MutedAA"));
            var pct = StatusStripView.Percent(this, m);
            pct.Margin = new Thickness(6, 0, 0, 0);
            cell.Children.Add(pct);
            if (m.Detail is { } detail)
            {
                var sub = StatusStripView.Text(this, detail, "MutedAA");
                sub.FontSize = 11.5;
                sub.Margin = new Thickness(6, 0, 0, 0);
                cell.Children.Add(sub);
            }

            UIElement child = cell;
            // The second column is ruled off from the first, 12 px either side.
            if (i > 0)
                child = new Border
                {
                    BorderBrush = (Brush)FindResource("LimitRule"),
                    BorderThickness = new Thickness(1, 0, 0, 0),
                    Margin = new Thickness(12, 0, 0, 0),
                    Padding = new Thickness(12, 0, 0, 0),
                    Child = cell,
                };
            SetColumn(child, i);
            Children.Add(child);
        }
    }
}

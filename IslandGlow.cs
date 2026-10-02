using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using CcxShell.Core;

namespace CcxShell;

/// <summary>
/// The light under the island and the dwell bar at the top edge. Both are partly transparent,
/// so they live in a window of their own that is click-through as a whole
/// (WS_EX_TRANSPARENT): in the island window those pixels would catch clicks meant for the
/// browser's tabs. The window is shown only while the island shows or the cursor dwells, so
/// nothing sits at the edge otherwise.
/// </summary>
public sealed class IslandGlow : Window
{
    // Logical px. Much wider than the 420 px pill so the light fades out well past its ends.
    public const double GlowWidth = 760;
    public const double GlowHeight = 200;

    // The glow table in docs/ISLAND.md.
    public static readonly Color Warm = Color.FromRgb(255, 186, 140);
    public static readonly Color Blue = Color.FromRgb(120, 165, 255);
    public static readonly Color Amber = Color.FromRgb(255, 168, 60);
    public static readonly Color Green = Color.FromRgb(60, 214, 160);

    private readonly SolidColorBrush _tint = new(Warm);
    private readonly TranslateTransform _drop = new();
    // Narrower under the small peek pill than under a notification, from the centre out.
    private readonly ScaleTransform _spread = new(1, 1, GlowWidth / 2, 0);
    private readonly Rectangle _light;
    private readonly Grid _breath;
    private readonly Rectangle _bar;

    private bool _lit, _fading, _dwelling, _closed;

    public IslandGlow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        Width = GlowWidth;
        Height = GlowHeight;

        // The colour sits in a plain brush and the shape in a fixed mask, so a change of state
        // is one colour animation.
        _light = new Rectangle { Fill = _tint, OpacityMask = Falloff(), Opacity = 0,
                              RenderTransform = new TransformGroup { Children = { _spread, _drop } } };
        _breath = new Grid { Children = { _light } };
        _bar = new Rectangle
        {
            Height = 2, Width = 0,
            VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center,
            Fill = BarBrush(),
        };
        Content = new Grid { Children = { _breath, _bar } };

        // As for the island: the styles must be in place before the first Show.
        new WindowInteropHelper(this).EnsureHandle();
        Closed += (_, _) => _closed = true;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // TRANSPARENT passes every click and hover through to whatever is underneath, even
        // where the light is nearly opaque; LAYERED is what makes TRANSPARENT mean that.
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE)
            | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    /// <summary>
    /// Lights up under the island in this colour, sliding in with the pill the first time and
    /// easing to the new colour after that. `island` is the window to sit just below; `spread`
    /// is the width as a fraction of the full light, following the pill's width.
    /// </summary>
    public void Light(PxRect monitor, double scale, IntPtr island, Color color, double spread)
    {
        if (_closed) return;
        bool fresh = !_lit && !_fading;
        _lit = true;
        _fading = false;
        Put(monitor, scale, island);

        if (fresh)
        {
            // From nothing there is no old colour to ease from.
            _tint.BeginAnimation(SolidColorBrush.ColorProperty, null);
            _tint.Color = color;
            _spread.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _spread.ScaleX = spread;
        }
        else
        {
            _tint.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(color, TimeSpan.FromMilliseconds(500))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            });
            _spread.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(spread, TimeSpan.FromMilliseconds(400))
            {
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 },
            });
        }

        // The pill's slide, so the light comes down with it.
        _drop.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(450))
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 },
        });
        _light.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(450)));

        // Slow and shallow: noticeable only as the light being alive. None when Windows has
        // animations turned off.
        if (SystemParameters.ClientAreaAnimation && !_breath.HasAnimatedProperties)
            _breath.BeginAnimation(OpacityProperty, new DoubleAnimation(1, .72, TimeSpan.FromSeconds(1.8))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            });
    }

    /// <summary>Slides and fades out with the pill, then hides the window unless a dwell needs it.</summary>
    public void Dim(double slide)
    {
        if (_closed || !_lit) return;
        _lit = false;
        _fading = true;

        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        _drop.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(-slide, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease };
        fade.Completed += (_, _) =>
        {
            // Lit again before the fade ended: that call owns the window now.
            if (_lit || !_fading) return;
            _fading = false;
            _breath.BeginAnimation(OpacityProperty, null);
            HideIfIdle();
        };
        _light.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>
    /// The dwell bar: grows from the centre of the top edge to the zone's full width over the
    /// rest of the dwell, and vanishes the moment progress is back to 0.
    /// </summary>
    public void Dwell(PxRect monitor, double scale, double progress)
    {
        if (_closed) return;
        if (progress <= 0)
        {
            if (!_dwelling) return;
            _dwelling = false;
            _bar.BeginAnimation(WidthProperty, null);
            _bar.Width = 0;
            HideIfIdle();
            return;
        }
        if (_dwelling) return;
        _dwelling = true;
        if (!_lit && !_fading) Put(monitor, scale, IntPtr.Zero);

        // One animation for the whole dwell rather than a step per 50 ms poll, so the line
        // grows smoothly. It starts where the dwell already is: the first poll that sees it.
        double zone = IslandTrigger.ZoneWidth;
        _bar.BeginAnimation(WidthProperty, new DoubleAnimation(progress * zone, zone,
            TimeSpan.FromMilliseconds((1 - progress) * IslandTrigger.DwellMs)));
    }

    /// <summary>Closes with the island, unless shutdown already closed it first.</summary>
    public void Retire()
    {
        if (!_closed) Close();
    }

    private void HideIfIdle()
    {
        if (!_lit && !_fading && !_dwelling && IsVisible) Hide();
    }

    /// <summary>
    /// Centred at the top of the monitor in physical px, for the same reason as the island
    /// (see IslandWindow.SlideIn), and just below the island in z-order so the pill covers
    /// the light's centre. With no island to sit under, it goes to the top of the topmost band.
    /// </summary>
    private void Put(PxRect monitor, double scale, IntPtr island)
    {
        int w = (int)Math.Round(GlowWidth * scale);
        int h = (int)Math.Round(GlowHeight * scale);
        int x = (monitor.Left + monitor.Right) / 2 - w / 2;
        var hwnd = new WindowInteropHelper(this).Handle;
        var after = island == IntPtr.Zero ? HWND_TOPMOST : island;
        void Place() => SetWindowPos(hwnd, after, x, monitor.Top, w, h, SWP_NOACTIVATE);

        Place();
        if (!IsVisible) Show();
        Place();
    }

    /// <summary>
    /// The light's shape: an ellipse centred a little below the top, as wide as the window,
    /// fading on a Gaussian so there is no visible rim. The tail is subtracted so it reaches
    /// exactly 0 at the ellipse, and everything outside it is 0 too.
    /// </summary>
    private static Brush Falloff()
    {
        const double peak = .45, k = 3.2;
        var brush = new RadialGradientBrush
        {
            Center = new Point(.5, .18), GradientOrigin = new Point(.5, .18),
            RadiusX = .5, RadiusY = .66,
        };
        double tail = Math.Exp(-k);
        for (int s = 0; s <= 12; s++)
        {
            double r = s / 12.0;
            double a = peak * (Math.Exp(-k * r * r) - tail) / (1 - tail);
            brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)Math.Round(a * 255), 0, 0, 0), r));
        }
        brush.Freeze();
        return brush;
    }

    private static Brush BarBrush()
    {
        Color At(double a) => Color.FromArgb((byte)(a * 255), Warm.R, Warm.G, Warm.B);
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0), EndPoint = new Point(1, 0),
            GradientStops = { new(At(0), 0), new(At(.9), .3), new(At(.9), .7), new(At(0), 1) },
        };
        brush.Freeze();
        return brush;
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const uint SWP_NOACTIVATE = 0x0010;
}

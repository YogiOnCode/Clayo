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
    private readonly SolidColorBrush _coreTint = new(Lift(Warm));
    private readonly SolidColorBrush _auraTint = new(Aura(Warm));
    private readonly TranslateTransform _drop = new();
    // Narrower under the small peek pill than under a notification, from the centre out.
    private readonly ScaleTransform _spread = new(1, 1, GlowWidth / 2, 0);
    private readonly Grid _light;
    private readonly Rectangle _core;
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

        // The colour sits in plain brushes and the shape in fixed masks, so a change of state
        // is a colour animation. Three layers: a faint, wide aura in a neighbouring hue, a soft
        // halo in the state's colour, and a tight, paler core hugging the pill's lower edge. A
        // single tint over a dark tab strip reads as a grey smudge; the bright core is what
        // makes it look like light, and the hue drifting outwards is what makes it rich.
        _core = new Rectangle { Fill = _coreTint, OpacityMask = Falloff(.95, 5, .27, .2, .25) };
        _light = new Grid
        {
            Opacity = 0,
            RenderTransform = new TransformGroup { Children = { _spread, _drop } },
            Children =
            {
                new Rectangle { Fill = _auraTint, OpacityMask = Falloff(.3, 2.2, .5, .8, .2) },
                new Rectangle { Fill = _tint, OpacityMask = Falloff(.5, 3.6, .45, .55, .2) },
                _core,
            },
        };
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

        // Scaled by the pill's width alone, the light under the small pill was a fifth as wide
        // but just as tall and bright: a beam, not a glow. So the width shrinks by less than the
        // pill, the height shrinks too, and the core dims, keeping it a soft oval at every size.
        double wide = .35 + .65 * spread, tall = .55 + .45 * spread, core = .5 + .5 * spread;

        if (fresh)
        {
            // From nothing there is no old colour to ease from.
            _tint.BeginAnimation(SolidColorBrush.ColorProperty, null);
            _tint.Color = color;
            _coreTint.BeginAnimation(SolidColorBrush.ColorProperty, null);
            _coreTint.Color = Lift(color);
            _auraTint.BeginAnimation(SolidColorBrush.ColorProperty, null);
            _auraTint.Color = Aura(color);
            _spread.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _spread.ScaleX = wide;
            _spread.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _spread.ScaleY = tall;
            _coreTint.BeginAnimation(Brush.OpacityProperty, null);
            _coreTint.Opacity = core;
        }
        else
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
            _tint.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(color, TimeSpan.FromMilliseconds(500)) { EasingFunction = ease });
            _coreTint.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(Lift(color), TimeSpan.FromMilliseconds(500)) { EasingFunction = ease });
            _auraTint.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(Aura(color), TimeSpan.FromMilliseconds(500)) { EasingFunction = ease });
            var spring = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 };
            _spread.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(wide, TimeSpan.FromMilliseconds(400)) { EasingFunction = spring });
            _spread.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(tall, TimeSpan.FromMilliseconds(400)) { EasingFunction = spring });
            _coreTint.BeginAnimation(Brush.OpacityProperty,
                new DoubleAnimation(core, TimeSpan.FromMilliseconds(400)) { EasingFunction = ease });
        }

        // The pill's slide, so the light comes down with it.
        _drop.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(450))
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 },
        });
        _light.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(450)));

        // A slow breath, a little under 6 s in and out. The core runs on a slightly different
        // period from the whole, so the two drift in and out of step and it never settles into
        // a mechanical blink. None when Windows has animations turned off.
        if (SystemParameters.ClientAreaAnimation && !_breath.HasAnimatedProperties)
        {
            _breath.BeginAnimation(OpacityProperty, Breath(.6, 2.8));
            _core.BeginAnimation(OpacityProperty, Breath(.7, 3.7));
        }
    }

    /// <summary>Stops the breathing and holds the light as it is. The next Light breathes again.</summary>
    public void Rest()
    {
        _breath.BeginAnimation(OpacityProperty, null);
        _core.BeginAnimation(OpacityProperty, null);
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
            _core.BeginAnimation(OpacityProperty, null);
            HideIfIdle();
        };
        _light.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>
    /// The dwell bar: grows from the centre of the top edge to the zone's full width over the
    /// rest of the dwell, and vanishes the moment progress is back to 0.
    /// </summary>
    public void Dwell(PxRect monitor, double scale, double progress, long lengthMs)
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
            TimeSpan.FromMilliseconds((1 - progress) * lengthMs)));
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
    /// A layer's shape: an ellipse centred at (.5, cy), fading on a Gaussian so there is no
    /// visible rim. The tail is subtracted so it reaches exactly 0 at the ellipse, and
    /// everything outside it is 0 too. Enough stops that no rings show on a light tab strip.
    /// </summary>
    private static Brush Falloff(double peak, double k, double rx, double ry, double cy)
    {
        var brush = new RadialGradientBrush
        {
            Center = new Point(.5, cy), GradientOrigin = new Point(.5, cy),
            RadiusX = rx, RadiusY = ry,
        };
        double tail = Math.Exp(-k);
        for (int s = 0; s <= 32; s++)
        {
            double r = s / 32.0;
            double a = peak * (Math.Exp(-k * r * r) - tail) / (1 - tail);
            brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)Math.Round(a * 255), 0, 0, 0), r));
        }
        brush.Freeze();
        return brush;
    }

    /// <summary>The core's colour: the state's colour taken halfway to white.</summary>
    private static Color Lift(Color c) =>
        Color.FromRgb((byte)((c.R + 255) / 2), (byte)((c.G + 255) / 2), (byte)((c.B + 255) / 2));

    /// <summary>The aura's colour: each state's colour leaning to a neighbouring hue.</summary>
    private static Color Aura(Color c) =>
        c == Blue ? Color.FromRgb(160, 130, 255)    // to violet
        : c == Amber ? Color.FromRgb(255, 120, 120) // to rose
        : c == Green ? Color.FromRgb(70, 200, 235)  // to teal
        : c == Warm ? Color.FromRgb(255, 150, 180)  // to pink
        : c;

    private static DoubleAnimation Breath(double low, double seconds) =>
        new(1, low, TimeSpan.FromSeconds(seconds))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };

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

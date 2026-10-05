using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace CcxShell.UI;

public enum MascotMove { Idle, Talk, Walk, Wave, Peek, Alert, Jump }

/// <summary>
/// The Voxel 3D mascot, drawn natively so the island needs no WebView2. A port of
/// <c>svg.mascot.voxel</c> in design/mascot/clayo-mascot.html: same 64x64 grid, colours and
/// moves, so the HTML stays the reference. All geometry is in grid units on one canvas, which
/// lets every transform name its pivot in grid units too, like the CSS transform-origins.
/// </summary>
public sealed class ClayoMascot : Viewbox
{
    // Grid units. CSS transform-box: fill-box resolves the origins against each part's box:
    // the whole figure squashes from its bottom centre, the right arm pivots on its left edge,
    // the mouth opens downwards from its top.
    private static readonly Point WholePivot = new(32, 52);
    private static readonly Point ArmPivot = new(53, 32.5);
    private static readonly Point MouthPivot = new(31, 37);

    // cubic-bezier(.45,0,.2,1), the design's --ease, applied per keyframe segment as in CSS.
    private static readonly KeySpline Ease = Frozen(new KeySpline(.45, 0, .2, 1));

    private readonly ScaleTransform _wholeScale = new(1, 1, WholePivot.X, WholePivot.Y);
    private readonly RotateTransform _wholeTurn = new(0, WholePivot.X, WholePivot.Y);
    private readonly TranslateTransform _wholeMove = new();
    private readonly TranslateTransform _legA = new(), _legB = new(), _armL = new(), _armRMove = new();
    private readonly RotateTransform _armRTurn = new(0, ArmPivot.X, ArmPivot.Y);
    private readonly ScaleTransform _mouth = new(1, 1, MouthPivot.X, MouthPivot.Y);
    private readonly TranslateTransform _pupils = new(), _glasses = new();

    private MascotMove[] _moves = [];
    private bool _still;

    public ClayoMascot()
    {
        var body = Gradient("#e07f60", "#b55a3f");
        var lens = Gradient("#3a4652", "#0c1013");
        var frame = Brush("#1a1f23");
        var white = Brush("#ffffff");

        var armR = new TransformGroup { Children = { _armRTurn, _armRMove } };

        // The SVG extrudes the body parts with one filter on their group, so all the dark
        // copies sit behind all the faces (the body's copy does not cover the legs). Each part's
        // copy is a separate shape here that shares the part's transform, so it moves with it.
        (Geometry shape, Transform? move)[] parts =
        [
            (Rect(13, 44, 3, 8), _legA), (Rect(20, 44, 3, 8), _legB),
            (Rect(39, 44, 3, 8), _legB), (Rect(46, 44, 3, 8), _legA),
            (Rect(2, 28, 9, 9), _armL), (Rect(53, 28, 9, 9), armR),
            (Rect(10, 12, 43, 33), null),
        ];

        var whole = new Canvas
        {
            RenderTransform = new TransformGroup { Children = { _wholeScale, _wholeTurn, _wholeMove } },
        };
        foreach (var (shape, move) in parts)
            whole.Children.Add(Part(Extrude(shape, .7, 1.4, 2.1), Brush("#6e2d1b"), move));
        foreach (var (shape, move) in parts)
            whole.Children.Add(Part(shape, body, move));

        // Light from the top left: a bright top edge and a fainter left edge.
        whole.Children.Add(Part(Rect(10, 12, 43, 1), white, null, .28));
        whole.Children.Add(Part(Rect(10, 12, 1, 33), white, null, .12));

        whole.Children.Add(Part(Union(Rect(15, 23, 8, 6), Rect(37, 23, 8, 6)), Brush("#f4f6f8"), null));
        whole.Children.Add(Part(Union(Rect(18, 25, 3, 3), Rect(40, 25, 3, 3)), Brush("#1b2126"), _pupils));
        whole.Children.Add(Part(Rect(29, 37, 4, 1), Brush("#2a0e06"), _mouth));

        // The glasses are one rigid piece, so they and their copies can share a canvas.
        var bar = Rect(9, 20, 45, 2);
        var lensL = Poly(11, 21, 28, 21, 28, 30, 26, 32, 13, 32, 11, 30);
        var lensR = Poly(32, 21, 50, 21, 50, 30, 48, 32, 34, 32, 32, 30);
        var bridge = Rect(28, 22, 4, 2);
        var glasses = new Canvas { RenderTransform = _glasses };
        glasses.Children.Add(Part(Extrude(Union(bar, lensL, lensR, bridge), .6, 1.2), Brush("#050607"), null));
        glasses.Children.Add(Part(bar, frame, null));
        glasses.Children.Add(Part(Union(lensL, lensR), lens, null));
        glasses.Children.Add(Part(bridge, frame, null));
        glasses.Children.Add(Part(Union(Rect(12, 22, 2, 1), Rect(44, 22, 2, 1), Rect(45, 23, 2, 1),
                                        Rect(46, 24, 1, 1)), white, null));
        whole.Children.Add(glasses);

        var grid = new Canvas { Width = 64, Height = 64, Children = { whole } };
        // The SVG's shape-rendering: crispEdges. Antialiasing would blur the voxel steps.
        RenderOptions.SetEdgeMode(grid, EdgeMode.Aliased);
        Child = grid;
        SnapsToDevicePixels = true;

        IsVisibleChanged += (_, _) => Apply();
        Apply();
    }

    /// <summary>
    /// Shows these moves together, like the design's classes (e.g. Alert + Talk). Idle
    /// breathing runs underneath whatever does not move the whole figure.
    /// </summary>
    public void Play(params MascotMove[] moves)
    {
        _moves = moves;
        Apply();
    }

    /// <summary>Holds the current pose without moving: the island rests once a note has been up a while.</summary>
    public bool Still
    {
        get => _still;
        set
        {
            if (_still == value) return;
            _still = value;
            Apply();
        }
    }

    private bool Has(MascotMove m) => Array.IndexOf(_moves, m) >= 0;

    private void Apply()
    {
        // Hidden or with animations turned off in Windows there are no clocks at all, only the
        // held pose. That pose is also what RenderTargetBitmap sees for an unshown control.
        bool animate = IsVisible && SystemParameters.ClientAreaAnimation && !_still;
        double glassesY = Has(MascotMove.Peek) ? 9 : 0;

        Run(_wholeScale, ScaleTransform.ScaleXProperty, null);
        Run(_wholeScale, ScaleTransform.ScaleYProperty, null);
        Run(_wholeTurn, RotateTransform.AngleProperty, null);
        Run(_wholeMove, TranslateTransform.XProperty, null);
        Run(_wholeMove, TranslateTransform.YProperty, null);
        Run(_legA, TranslateTransform.YProperty, null);
        Run(_legB, TranslateTransform.YProperty, null);
        Run(_armL, TranslateTransform.YProperty, null);
        Run(_armRMove, TranslateTransform.YProperty, null);
        Run(_armRTurn, RotateTransform.AngleProperty, null);
        Run(_mouth, ScaleTransform.ScaleYProperty, null);
        Run(_pupils, TranslateTransform.XProperty, null);

        if (!animate)
        {
            Run(_glasses, TranslateTransform.YProperty, null);
            _glasses.Y = glassesY;
            return;
        }

        // A transition in the design, not a loop: from wherever the glasses are now. BackEase
        // stands in for cubic-bezier(.34,1.56,.64,1), whose ~10% overshoot a KeySpline cannot
        // express (its control points must stay within 0..1).
        Run(_glasses, TranslateTransform.YProperty, new DoubleAnimation(glassesY, TimeSpan.FromMilliseconds(500))
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = .45 },
        });

        // Only one move can own the whole figure; as in the CSS, later rules win.
        if (Has(MascotMove.Alert))
        {
            Run(_wholeMove, TranslateTransform.XProperty, Loop(.45, (0, 0), (.25, -1.5), (.75, 1.5), (1, 0)));
            Run(_wholeTurn, RotateTransform.AngleProperty, Loop(.45, (0, 0), (.25, -2), (.75, 2), (1, 0)));
        }
        else if (Has(MascotMove.Jump))
        {
            // Twice, then still, as in the island prototype: a celebration, not a state.
            var count = new RepeatBehavior(2);
            Run(_wholeScale, ScaleTransform.ScaleXProperty,
                Loop(1, (0, 1), (.14, 1.12), (.44, .95), (.72, 1.09), (.86, .98), (1, 1)), count);
            Run(_wholeScale, ScaleTransform.ScaleYProperty,
                Loop(1, (0, 1), (.14, .86), (.44, 1.07), (.72, .91), (.86, 1.02), (1, 1)), count);
            Run(_wholeMove, TranslateTransform.YProperty,
                Loop(1, (0, 0), (.14, 0), (.44, -10), (.72, 0), (1, 0)), count);
        }
        else if (Has(MascotMove.Walk))
        {
            // CSS bob: 0.25 s, alternate.
            Run(_wholeMove, TranslateTransform.YProperty, Loop(.5, (0, 0), (.5, -1.2), (1, 0)));
            Run(_wholeTurn, RotateTransform.AngleProperty, Loop(.5, (0, 0), (.5, 1.2), (1, 0)));
        }
        else
        {
            Run(_wholeScale, ScaleTransform.ScaleXProperty, Loop(3.6, (0, 1), (.5, 1.018), (1, 1)));
            Run(_wholeScale, ScaleTransform.ScaleYProperty, Loop(3.6, (0, 1), (.5, .982), (1, 1)));
        }

        if (Has(MascotMove.Walk))
        {
            // In place: a 44 px island has no room to cross. The b pair runs half a cycle
            // behind (CSS delay -.25s), written as shifted keyframes.
            Run(_legA, TranslateTransform.YProperty, Loop(.5, (0, 0), (.5, -2.4), (1, 0)));
            Run(_legB, TranslateTransform.YProperty, Loop(.5, (0, -2.4), (.5, 0), (1, -2.4)));
            Run(_armL, TranslateTransform.YProperty, Loop(.5, (0, 0), (.5, 1.2), (1, 0)));
            Run(_armRMove, TranslateTransform.YProperty, Loop(.5, (0, 1.2), (.5, 0), (1, 1.2)));
        }
        if (Has(MascotMove.Wave))
            Run(_armRTurn, RotateTransform.AngleProperty, Loop(1.1, (0, -18), (.5, -60), (1, -18)));
        if (Has(MascotMove.Talk))
            Run(_mouth, ScaleTransform.ScaleYProperty, Loop(.42, (0, 1), (.4, 3.2), (.7, 1.8), (1, 1)));
        if (Has(MascotMove.Peek))
            Run(_pupils, TranslateTransform.XProperty,
                Loop(2.4, (0, 0), (.2, 0), (.35, -2), (.5, -2), (.65, 2), (.8, 2), (1, 0)));
    }

    private static void Run(Animatable target, DependencyProperty property, AnimationTimeline? anim,
                            RepeatBehavior? repeat = null)
    {
        if (anim is not null && repeat is { } r) anim.RepeatBehavior = r;
        target.BeginAnimation(property, anim);
    }

    /// <summary>A CSS keyframes loop: (fraction of the cycle, value) pairs, eased per segment.</summary>
    private static DoubleAnimationUsingKeyFrames Loop(double seconds, params (double at, double value)[] keys)
    {
        var anim = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(seconds),
            RepeatBehavior = RepeatBehavior.Forever,
        };
        foreach (var (at, value) in keys)
            anim.KeyFrames.Add(new SplineDoubleKeyFrame(value, KeyTime.FromPercent(at), Ease));
        return anim;
    }

    // ------------------------------------------------------------------ drawing

    private static Path Part(Geometry shape, Brush fill, Transform? move, double opacity = 1) =>
        new() { Data = shape, Fill = fill, RenderTransform = move ?? Transform.Identity, Opacity = opacity };

    /// <summary>The SVG filter's darker copies, offset down-right, merged into one shape.</summary>
    private static Geometry Extrude(Geometry shape, params double[] offsets)
    {
        var copies = new GeometryGroup { FillRule = FillRule.Nonzero };
        foreach (var o in offsets)
        {
            var copy = shape.Clone();
            copy.Transform = new TranslateTransform(o, o);
            copies.Children.Add(copy);
        }
        return Frozen(copies);
    }

    private static Geometry Union(params Geometry[] shapes)
    {
        var group = new GeometryGroup { FillRule = FillRule.Nonzero };
        foreach (var s in shapes) group.Children.Add(s);
        return Frozen(group);
    }

    private static Geometry Rect(double x, double y, double w, double h) =>
        Frozen(new RectangleGeometry(new System.Windows.Rect(x, y, w, h)));

    private static Geometry Poly(params double[] xy)
    {
        var points = new Point[xy.Length / 2];
        for (int i = 0; i < points.Length; i++) points[i] = new Point(xy[2 * i], xy[2 * i + 1]);
        var figure = new PathFigure(points[0], [new PolyLineSegment(points[1..], true)], closed: true);
        return Frozen(new PathGeometry([figure]));
    }

    private static Brush Brush(string hex) =>
        Frozen(new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)));

    /// <summary>Top to bottom over each shape's own box, like the SVG's objectBoundingBox default.</summary>
    private static Brush Gradient(string top, string bottom) =>
        Frozen(new LinearGradientBrush((Color)ColorConverter.ConvertFromString(top),
                                       (Color)ColorConverter.ConvertFromString(bottom), 90));

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}

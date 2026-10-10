using System.Windows;
using System.Windows.Media;

namespace Insomnia.App.Controls;

/// <summary>One frame of the eye: how far the lid is lifted and where the pupil is.</summary>
/// <param name="Openness">0 closed (lens shaded, lashes below) to 1 open (lens clear, lashes above).</param>
/// <param name="PupilScale">Pupil scale about its centre; 0.6 in the closed eye, 1 open.</param>
/// <param name="PupilOpacity">0 to 1.</param>
/// <param name="Crossfade">Reduced motion: the lid fades instead of lifting.</param>
public readonly record struct EyeFrame(double Openness, double PupilScale, double PupilOpacity, bool Crossfade = false)
{
    public static EyeFrame Closed => new(0, 0.6, 0);

    public static EyeFrame Open => new(1, 1, 1);
}

/// <summary>
/// The Insomnia mark on the upstream 24-unit grid (docs/assets/eye-open.svg
/// and EyeMarkGeometry.swift): an almond lens with two cubic lids, a lid that
/// shades it, five lashes above (open) or below (closed), and a round pupil
/// with a highlight bitten out of its upper right. Shared by the flyout's
/// <see cref="EyeMark"/> and the tray icon renderer.
/// </summary>
public static class EyeGeometry
{
    public const double DesignSize = 24;

    /// <summary>Outline weight in grid units (1.5 of 24).</summary>
    public const double StrokeUnits = 1.5;

    private const double AxisY = 12;

    // Upper lid, left to right, and lower lid, right to left: the lens outline.
    private static readonly Point UpperP0 = new(1.5, 12);
    private static readonly Point UpperC1 = new(7, 4.5);
    private static readonly Point UpperC2 = new(17, 4.5);
    private static readonly Point UpperP3 = new(22.5, 12);
    private static readonly Point LowerC1 = new(17, 19.5);
    private static readonly Point LowerC2 = new(7, 19.5);

    /// <summary>Upper lashes (start, end) from the upstream mark: on the upper lid's outward normal at 20/35/50/65/80% of the width.</summary>
    private static readonly (Point Start, Point End)[] UpperLashes =
    [
        (new(4.5499, 6.1977), new(3.1442, 3.8341)),
        (new(8.2636, 4.6218), new(7.5468, 1.9668)),
        (new(12, 4.125), new(12, 1.375)),
        (new(15.7364, 4.6218), new(16.4532, 1.9668)),
        (new(19.4501, 6.1977), new(20.8558, 3.8341)),
    ];

    public static readonly Geometry Lens = Frozen(Geometry.Parse("M1.5,12 C7,4.5 17,4.5 22.5,12 C17,19.5 7,19.5 1.5,12 Z"));

    /// <summary>Pupil radius 4 at (12,12) with a 1.4 highlight at (14,10) cut from its rim, as one closed figure.</summary>
    public static readonly Geometry Pupil = Frozen(Geometry.Parse("M15.3953,9.8853 A4,4 0 1 1 14.1147,8.6047 A1.4,1.4 0 1 0 15.3953,9.8853 Z"));

    public static readonly Geometry LashesAbove = Frozen(Lashes(above: true));

    public static readonly Geometry LashesBelow = Frozen(Lashes(above: false));

    public static readonly Point PupilCenter = new(12, 12);

    /// <summary>
    /// The lid, to be filled: from the upper lid down to an edge interpolated
    /// between the lower lid (closed, progress 0) and the upper lid (open,
    /// progress 1), so it lifts like an eyelid.
    /// </summary>
    public static Geometry Lid(double progress)
    {
        var p = Math.Clamp(progress, 0, 1);
        var edge1 = Lerp(LowerC1, UpperC2, p);
        var edge2 = Lerp(LowerC2, UpperC1, p);
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(UpperP0, isFilled: true, isClosed: true);
            ctx.BezierTo(UpperC1, UpperC2, UpperP3, isStroked: false, isSmoothJoin: false);
            ctx.BezierTo(edge1, edge2, UpperP0, isStroked: false, isSmoothJoin: false);
        }
        geometry.Freeze();
        return geometry;
    }

    /// <summary>
    /// Lash opacity during a blink: the lower set is gone by half way and the
    /// upper set only appears from there, so no lash shows while the lid edge
    /// passes its side.
    /// </summary>
    public static double LashOpacity(double progress, bool above)
    {
        var x = above ? (progress - 0.5) * 2 : 1 - progress * 2;
        var t = Math.Clamp(x, 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>Draw a frame into the 24-unit grid. The caller sets up the transform to the target size.</summary>
    public static void Draw(DrawingContext dc, EyeFrame frame, Brush brush, double strokeUnits = StrokeUnits)
    {
        var pen = new Pen(brush, strokeUnits)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        pen.Freeze();
        var openness = Math.Clamp(frame.Openness, 0, 1);

        dc.DrawGeometry(null, pen, Lens);

        // The pupil sits behind the lid and is clipped to the lens, so neither
        // the opening spring's overshoot nor an interrupted blink shows it outside.
        var pupilOpacity = Math.Clamp(frame.PupilOpacity, 0, 1);
        if (pupilOpacity > 0.001 && frame.PupilScale > 0.001)
        {
            dc.PushClip(Lens);
            dc.PushOpacity(pupilOpacity);
            dc.PushTransform(new ScaleTransform(frame.PupilScale, frame.PupilScale, PupilCenter.X, PupilCenter.Y));
            dc.DrawGeometry(brush, null, Pupil);
            dc.Pop();
            dc.Pop();
            dc.Pop();
        }

        if (frame.Crossfade)
        {
            var lidOpacity = 1 - openness;
            if (lidOpacity > 0.001)
            {
                dc.PushOpacity(lidOpacity);
                dc.DrawGeometry(brush, null, Lid(0));
                dc.Pop();
            }
        }
        else if (openness < 0.999)
        {
            dc.DrawGeometry(brush, null, Lid(openness));
        }

        DrawLashes(dc, LashesBelow, pen, LashOpacity(openness, above: false));
        DrawLashes(dc, LashesAbove, pen, LashOpacity(openness, above: true));
    }

    private static void DrawLashes(DrawingContext dc, Geometry lashes, Pen pen, double opacity)
    {
        if (opacity <= 0.001)
        {
            return;
        }
        dc.PushOpacity(opacity);
        dc.DrawGeometry(null, pen, lashes);
        dc.Pop();
    }

    private static StreamGeometry Lashes(bool above)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            foreach (var (start, end) in UpperLashes)
            {
                ctx.BeginFigure(Fold(start, above), isFilled: false, isClosed: false);
                ctx.LineTo(Fold(end, above), isStroked: true, isSmoothJoin: false);
            }
        }
        return geometry;
    }

    private static Point Fold(Point p, bool above) => above ? p : new Point(p.X, AxisY - (p.Y - AxisY));

    private static Point Lerp(Point a, Point b, double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

    private static T Frozen<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}

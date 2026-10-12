using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Insomnia.App.Controls;
using Insomnia.App.Interop;

namespace Insomnia.App.Tray;

/// <summary>
/// Draws the eye mark into an HICON at an exact pixel size, so the tray icon
/// is sharp at every scale without image files. The caller owns the returned
/// handle and destroys it with <see cref="NativeMethods.DestroyIcon"/>.
/// </summary>
internal static class TrayIconRenderer
{
    /// <param name="size">Icon edge in physical pixels (16, 20, 24, 32...).</param>
    /// <param name="frame">Blink frame to draw.</param>
    /// <param name="glyph">Mark color for the taskbar's theme.</param>
    /// <param name="dot">Warning dot color, or null for none.</param>
    public static IntPtr CreateIcon(int size, EyeFrame frame, Color glyph, Color? dot)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var dotRadius = Math.Max(2.5, size * 0.17);
            var dotCenter = new Point(size - dotRadius - 0.5, size - dotRadius - 0.5);
            if (dot is not null)
            {
                // Knock the mark out around the dot so it reads at 16 px.
                var gap = Math.Max(1, size / 16.0);
                var clip = new CombinedGeometry(
                    GeometryCombineMode.Exclude,
                    new RectangleGeometry(new Rect(0, 0, size, size)),
                    new EllipseGeometry(dotCenter, dotRadius + gap, dotRadius + gap));
                clip.Freeze();
                dc.PushClip(clip);
            }
            var scale = size / EyeGeometry.DesignSize;
            dc.PushTransform(new ScaleTransform(scale, scale));
            var brush = new SolidColorBrush(glyph);
            brush.Freeze();
            // Never thinner than one device pixel.
            var stroke = Math.Max(EyeGeometry.StrokeUnits, 1.0 / scale);
            EyeGeometry.Draw(dc, frame, brush, stroke);
            dc.Pop();
            if (dot is { } dotColor)
            {
                dc.Pop();
                var dotBrush = new SolidColorBrush(dotColor);
                dotBrush.Freeze();
                dc.DrawEllipse(dotBrush, null, dotCenter, dotRadius, dotRadius);
            }
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var stride = size * 4;
        var pixels = new byte[stride * size];
        bitmap.CopyPixels(pixels, stride, 0);
        Unpremultiply(pixels);
        return CreateHIcon(size, pixels);
    }

    /// <summary>Icons take straight (not premultiplied) alpha.</summary>
    private static void Unpremultiply(byte[] bgra)
    {
        for (var i = 0; i < bgra.Length; i += 4)
        {
            var a = bgra[i + 3];
            if (a is 0 or 255)
            {
                continue;
            }
            bgra[i] = (byte)Math.Min(255, bgra[i] * 255 / a);
            bgra[i + 1] = (byte)Math.Min(255, bgra[i + 1] * 255 / a);
            bgra[i + 2] = (byte)Math.Min(255, bgra[i + 2] * 255 / a);
        }
    }

    private static IntPtr CreateHIcon(int size, byte[] bgra)
    {
        var header = new BitmapInfoHeader
        {
            Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
            Width = size,
            Height = -size, // top-down
            Planes = 1,
            BitCount = 32,
            Compression = 0, // BI_RGB
        };
        var color = NativeMethods.CreateDIBSection(IntPtr.Zero, ref header, 0, out var bits, IntPtr.Zero, 0);
        if (color == IntPtr.Zero || bits == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }
        IntPtr mask = IntPtr.Zero;
        try
        {
            Marshal.Copy(bgra, 0, bits, bgra.Length);
            // An all-zero AND mask: the alpha channel decides transparency.
            var maskStride = (size + 15) / 16 * 2;
            var maskBits = new byte[maskStride * size];
            var handle = GCHandle.Alloc(maskBits, GCHandleType.Pinned);
            try
            {
                mask = NativeMethods.CreateBitmap(size, size, 1, 1, handle.AddrOfPinnedObject());
            }
            finally
            {
                handle.Free();
            }
            var info = new IconInfo { IsIcon = 1, Mask = mask, Color = color };
            return NativeMethods.CreateIconIndirect(ref info);
        }
        finally
        {
            NativeMethods.DeleteObject(color);
            if (mask != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(mask);
            }
        }
    }
}

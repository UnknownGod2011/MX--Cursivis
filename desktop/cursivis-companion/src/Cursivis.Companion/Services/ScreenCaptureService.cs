using Cursivis.Companion.Infrastructure;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;

namespace Cursivis.Companion.Services;

public sealed class ScreenCaptureService
{
    public string? TryCaptureContextAroundCursorAsBase64Png(System.Windows.Point cursor, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var region = BuildCursorContextRegion(cursor, width, height);
        if (region.Width <= 0 || region.Height <= 0)
        {
            return null;
        }

        try
        {
            return CaptureRegionAsBase64Png(region);
        }
        catch
        {
            return null;
        }
    }

    public string CaptureRegionAsBase64Png(System.Windows.Int32Rect region)
    {
        var normalizedRegion = NormalizeRegionToVirtualScreen(region);
        if (normalizedRegion.Width <= 0 || normalizedRegion.Height <= 0)
        {
            throw new ArgumentException("Capture region must have non-zero dimensions.");
        }

        using var bitmap = new Bitmap(normalizedRegion.Width, normalizedRegion.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(
                normalizedRegion.X,
                normalizedRegion.Y,
                0,
                0,
                new System.Drawing.Size(normalizedRegion.Width, normalizedRegion.Height));
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return Convert.ToBase64String(stream.ToArray());
    }

    public string SamplePixelHex(System.Windows.Point cursor)
    {
        var x = (int)Math.Round(cursor.X);
        var y = (int)Math.Round(cursor.Y);
        using var bitmap = new Bitmap(1, 1, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(1, 1));
        }

        var color = bitmap.GetPixel(0, 0);
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private static System.Windows.Int32Rect BuildCursorContextRegion(System.Windows.Point cursor, int width, int height)
    {
        var virtualScreen = NativeMethods.GetVirtualScreenBounds();
        if (virtualScreen.Width <= 0 || virtualScreen.Height <= 0)
        {
            return default;
        }

        var x = (int)Math.Round(cursor.X - (width / 2.0));
        var y = (int)Math.Round(cursor.Y - (height / 2.0));
        var maxX = virtualScreen.X + virtualScreen.Width - width;
        var maxY = virtualScreen.Y + virtualScreen.Height - height;

        x = Math.Max(virtualScreen.X, Math.Min(x, maxX));
        y = Math.Max(virtualScreen.Y, Math.Min(y, maxY));

        return NormalizeRegionToVirtualScreen(new System.Windows.Int32Rect(x, y, width, height));
    }

    private static System.Windows.Int32Rect NormalizeRegionToVirtualScreen(System.Windows.Int32Rect region)
    {
        if (region.Width <= 0 || region.Height <= 0)
        {
            return default;
        }

        var virtualScreen = NativeMethods.GetVirtualScreenBounds();
        if (virtualScreen.Width <= 0 || virtualScreen.Height <= 0)
        {
            return default;
        }

        var virtualLeft = virtualScreen.X;
        var virtualTop = virtualScreen.Y;
        var virtualRight = virtualScreen.X + virtualScreen.Width;
        var virtualBottom = virtualScreen.Y + virtualScreen.Height;

        var x = Math.Max(virtualLeft, region.X);
        var y = Math.Max(virtualTop, region.Y);
        var right = Math.Min(virtualRight, region.X + region.Width);
        var bottom = Math.Min(virtualBottom, region.Y + region.Height);

        var width = Math.Max(0, right - x);
        var height = Math.Max(0, bottom - y);

        return width == 0 || height == 0
            ? default
            : new System.Windows.Int32Rect(x, y, width, height);
    }
}

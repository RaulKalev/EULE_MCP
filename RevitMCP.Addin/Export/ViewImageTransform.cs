using System.IO;

namespace RevitMCP.Addin.Export;

/// <summary>A point or direction in the exported view's coordinate system, in millimetres.</summary>
public readonly struct ImageVector
{
    public ImageVector(double x, double y, double z = 0)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public double X { get; }
    public double Y { get; }
    public double Z { get; }
}

/// <summary>A rectangle of pixels, measured from the top-left pixel of the image.</summary>
public readonly struct PixelBox
{
    public PixelBox(int x, int y, int width, int height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public int X { get; }
    public int Y { get; }
    public int Width { get; }
    public int Height { get; }
}

/// <summary>
/// Maps pixels of an image from revit_export_view_image to model mm (or sheet mm) and back (#139). The PNG does not
/// show just the crop: grids, levels and annotation reaching past it make the image larger, on any side, so the crop's
/// place in it cannot be worked out. <see cref="FromMarker"/> measures it from a temporary line drawn across the crop,
/// and <see cref="ContentBoxPx"/> says where the crop is. Pure, unit tested in RevitMCP.Tests.
/// </summary>
public sealed class ViewImageTransform
{
    public int WidthPx { get; private set; }
    public int HeightPx { get; private set; }
    public double MmPerPx { get; private set; }
    public ImageVector OriginMm { get; private set; }
    public ImageVector RightDirection { get; private set; }
    public ImageVector UpDirection { get; private set; }
    public PixelBox ContentBoxPx { get; private set; }

    /// <summary>
    /// <paramref name="topLeftMm"/> is the model point at the crop's top-left corner; the directions are unit vectors
    /// along the image's right and up. Null when the image or the crop has no size.
    /// </summary>
    public static ViewImageTransform? Create(
        int widthPx, int heightPx, double cropWidthMm, double cropHeightMm,
        ImageVector topLeftMm, ImageVector rightDirection, ImageVector upDirection)
    {
        if (widthPx <= 0 || heightPx <= 0 || cropWidthMm <= 0 || cropHeightMm <= 0) return null;

        // The export fits the width. A crop too tall for the image at that scale can only have been fitted by height.
        var mmPerPx = cropWidthMm / widthPx;
        if (cropHeightMm / mmPerPx > heightPx + 1) mmPerPx = cropHeightMm / heightPx;

        return new ViewImageTransform
        {
            WidthPx = widthPx,
            HeightPx = heightPx,
            MmPerPx = mmPerPx,
            OriginMm = topLeftMm,
            RightDirection = rightDirection,
            UpDirection = upDirection,
            ContentBoxPx = new PixelBox(
                0, 0,
                Math.Min(widthPx, (int)Math.Round(cropWidthMm / mmPerPx)),
                Math.Min(heightPx, (int)Math.Round(cropHeightMm / mmPerPx)))
        };
    }

    /// <summary>
    /// From a marker line drawn corner to corner across the crop, inset by <paramref name="insetFraction"/> of its size
    /// on every side. <paramref name="marker"/> is the box of the pixels it changed, in an image
    /// <paramref name="pxScale"/> times smaller than this one. Null when the marker is too small to measure or its two
    /// directions disagree on the scale.
    /// </summary>
    public static ViewImageTransform? FromMarker(
        int widthPx, int heightPx, double cropWidthMm, double cropHeightMm,
        ImageVector cropTopLeftMm, ImageVector rightDirection, ImageVector upDirection,
        double insetFraction, PixelBox marker, double pxScale = 1)
    {
        var spanX = (marker.Width - 1) * pxScale;
        var spanY = (marker.Height - 1) * pxScale;
        if (widthPx <= 0 || heightPx <= 0 || spanX < 2 || spanY < 2) return null;

        var markedShare = 1 - 2 * insetFraction;
        var mmPerPxX = cropWidthMm * markedShare / spanX;
        var mmPerPxY = cropHeightMm * markedShare / spanY;
        // A marker cut off by something else in the picture shows up as two different scales.
        if (Math.Abs(mmPerPxX - mmPerPxY) > 0.03 * mmPerPxX) return null;
        var mmPerPx = (mmPerPxX + mmPerPxY) / 2;

        var cropX = marker.X * pxScale - insetFraction * cropWidthMm / mmPerPx;
        var cropY = marker.Y * pxScale - insetFraction * cropHeightMm / mmPerPx;
        var right = -cropX * mmPerPx;
        var up = cropY * mmPerPx;
        var left = Math.Max(0, (int)Math.Round(cropX));
        var top = Math.Max(0, (int)Math.Round(cropY));
        return new ViewImageTransform
        {
            WidthPx = widthPx,
            HeightPx = heightPx,
            MmPerPx = mmPerPx,
            OriginMm = new ImageVector(
                cropTopLeftMm.X + rightDirection.X * right + upDirection.X * up,
                cropTopLeftMm.Y + rightDirection.Y * right + upDirection.Y * up,
                cropTopLeftMm.Z + rightDirection.Z * right + upDirection.Z * up),
            RightDirection = rightDirection,
            UpDirection = upDirection,
            ContentBoxPx = new PixelBox(
                left, top,
                Math.Min(widthPx - left, (int)Math.Round(cropWidthMm / mmPerPx)),
                Math.Min(heightPx - top, (int)Math.Round(cropHeightMm / mmPerPx)))
        };
    }

    /// <summary>The box of the pixels that differ between two same-sized images of 4 bytes a pixel; null when none do.</summary>
    public static PixelBox? DifferenceBox(byte[] a, byte[] b, int width, int height)
    {
        const int Threshold = 48;
        var minX = int.MaxValue;
        var minY = int.MaxValue;
        var maxX = -1;
        var maxY = -1;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                var difference = Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]);
                if (difference < Threshold) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
        return maxX < 0 ? null : new PixelBox(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    /// <summary>The model point under a pixel; pixel y grows downwards, the model's up does not.</summary>
    public ImageVector PixelToModel(double xPx, double yPx)
    {
        var right = xPx * MmPerPx;
        var up = -yPx * MmPerPx;
        return new ImageVector(
            OriginMm.X + RightDirection.X * right + UpDirection.X * up,
            OriginMm.Y + RightDirection.Y * right + UpDirection.Y * up,
            OriginMm.Z + RightDirection.Z * right + UpDirection.Z * up);
    }

    /// <summary>The pixel of a model point.</summary>
    public (double X, double Y) ModelToPixel(ImageVector model)
    {
        var dx = model.X - OriginMm.X;
        var dy = model.Y - OriginMm.Y;
        var dz = model.Z - OriginMm.Z;
        return (
            (dx * RightDirection.X + dy * RightDirection.Y + dz * RightDirection.Z) / MmPerPx,
            -(dx * UpDirection.X + dy * UpDirection.Y + dz * UpDirection.Z) / MmPerPx);
    }

    /// <summary>Width and height from a PNG's IHDR chunk, without decoding the image.</summary>
    public static bool TryReadPngSize(Stream stream, out int widthPx, out int heightPx)
    {
        widthPx = heightPx = 0;
        var header = new byte[24];
        var read = 0;
        while (read < header.Length)
        {
            var n = stream.Read(header, read, header.Length - read);
            if (n <= 0) return false;
            read += n;
        }

        // Signature, then the IHDR length and type, then big-endian width and height.
        byte[] signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        for (var i = 0; i < signature.Length; i++)
            if (header[i] != signature[i]) return false;
        if (header[12] != 'I' || header[13] != 'H' || header[14] != 'D' || header[15] != 'R') return false;

        widthPx = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
        heightPx = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
        return widthPx > 0 && heightPx > 0;
    }
}

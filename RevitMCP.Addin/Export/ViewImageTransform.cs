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
/// Maps pixels of an image from revit_export_view_image to model mm (or sheet mm) and back (#139). Revit pads the PNG
/// so it does not have the crop's aspect ratio: the crop fills the fit direction from the top-left corner and the rest
/// stays empty, which <see cref="ContentBoxPx"/> marks. Pure, unit tested in RevitMCP.Tests.
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

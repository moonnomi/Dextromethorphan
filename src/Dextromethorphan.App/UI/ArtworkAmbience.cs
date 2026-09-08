using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Dextromethorphan.App.UI;

internal static class ArtworkAmbience
{
    // Decode only a thumbnail, once per artwork change, away from the UI thread.
    public static string? ReadColor(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            using var stream = File.OpenRead(path);
            var image = new BitmapImage();
            image.BeginInit();
            image.DecodePixelWidth = 32;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
            converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
            double r = 0, g = 0, b = 0, total = 0;
            for (var i = 0; i < pixels.Length; i += 4)
            {
                var max = Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2]));
                var min = Math.Min(pixels[i], Math.Min(pixels[i + 1], pixels[i + 2]));
                var weight = (max - min + 1) * (pixels[i + 3] / 255d);
                b += pixels[i] * weight; g += pixels[i + 1] * weight; r += pixels[i + 2] * weight; total += weight;
            }
            return total == 0 ? null : ThemeManager.ToHex(Color.FromRgb((byte)(r / total), (byte)(g / total), (byte)(b / total)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or System.IO.FileFormatException or ArgumentException)
        {
            return null;
        }
    }
}

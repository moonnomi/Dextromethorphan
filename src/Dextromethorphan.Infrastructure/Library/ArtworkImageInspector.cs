using System.Buffers.Binary;

namespace Dextromethorphan.Infrastructure.Library;

public static class ArtworkImageInspector
{
    public const long MaximumEncodedBytes = 32L * 1024 * 1024;
    public const int MaximumDimension = 16_384;
    public const long MaximumPixels = 64L * 1024 * 1024;

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static bool TryInspect(
        ReadOnlySpan<byte> bytes,
        out ArtworkImageInfo info,
        out ArtworkImageRejectionReason rejection)
    {
        info = default;
        if (bytes.IsEmpty)
        {
            rejection = ArtworkImageRejectionReason.Empty;
            return false;
        }
        if (bytes.Length > MaximumEncodedBytes)
        {
            rejection = ArtworkImageRejectionReason.EncodedSizeLimit;
            return false;
        }

        ArtworkImageFormat format;
        string extension;
        int width;
        int height;
        bool structurallyValid;

        if (bytes.StartsWith(PngSignature))
        {
            format = ArtworkImageFormat.Png;
            extension = ".png";
            structurallyValid = TryReadPng(bytes, out width, out height);
        }
        else if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            format = ArtworkImageFormat.Jpeg;
            extension = ".jpg";
            structurallyValid = TryReadJpeg(bytes, out width, out height);
        }
        else if (bytes.Length >= 6
                 && (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8)))
        {
            format = ArtworkImageFormat.Gif;
            extension = ".gif";
            structurallyValid = TryReadGif(bytes, out width, out height);
        }
        else if (bytes.Length >= 2 && bytes[0] == (byte)'B' && bytes[1] == (byte)'M')
        {
            format = ArtworkImageFormat.Bmp;
            extension = ".bmp";
            structurallyValid = TryReadBmp(bytes, out width, out height);
        }
        else if (IsTiff(bytes))
        {
            format = ArtworkImageFormat.Tiff;
            extension = ".tiff";
            structurallyValid = TryReadTiff(bytes, out width, out height);
        }
        else if (bytes.Length >= 16
                 && bytes[..4].SequenceEqual("RIFF"u8)
                 && bytes.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            format = ArtworkImageFormat.WebP;
            extension = ".webp";
            structurallyValid = TryReadWebP(bytes, out width, out height);
        }
        else
        {
            rejection = ArtworkImageRejectionReason.UnsupportedFormat;
            return false;
        }

        if (!structurallyValid)
        {
            rejection = ArtworkImageRejectionReason.CorruptStructure;
            return false;
        }
        if (width < 1 || height < 1 || width > MaximumDimension || height > MaximumDimension
            || (long)width * height > MaximumPixels)
        {
            rejection = ArtworkImageRejectionReason.DimensionLimit;
            return false;
        }

        info = new ArtworkImageInfo(format, extension, width, height, bytes.Length);
        rejection = ArtworkImageRejectionReason.None;
        return true;
    }

    public static bool TryInspectFile(
        string path,
        out ArtworkImageInfo info,
        out ArtworkImageRejectionReason rejection,
        CancellationToken cancellationToken = default)
    {
        info = default;
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            rejection = ArtworkImageRejectionReason.Missing;
            return false;
        }
        if (file.Length <= 0)
        {
            rejection = ArtworkImageRejectionReason.Empty;
            return false;
        }
        if (file.Length > MaximumEncodedBytes)
        {
            rejection = ArtworkImageRejectionReason.EncodedSizeLimit;
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var bytes = GC.AllocateUninitializedArray<byte>(checked((int)file.Length));
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var offset = 0;
        while (offset < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(bytes, offset, bytes.Length - offset);
            if (read == 0)
            {
                rejection = ArtworkImageRejectionReason.CorruptStructure;
                return false;
            }
            offset += read;
        }
        return TryInspect(bytes, out info, out rejection);
    }

    private static bool TryReadPng(ReadOnlySpan<byte> bytes, out int width, out int height)
    {
        width = height = 0;
        if (bytes.Length < 45) return false;
        var position = 8;
        var firstChunk = true;
        var sawImageData = false;
        while (position <= bytes.Length - 12)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(position, 4));
            if (length > int.MaxValue || length > bytes.Length - position - 12) return false;
            var type = bytes.Slice(position + 4, 4);
            var data = bytes.Slice(position + 8, (int)length);
            if (firstChunk)
            {
                if (!type.SequenceEqual("IHDR"u8) || length != 13) return false;
                var rawWidth = BinaryPrimitives.ReadUInt32BigEndian(data[..4]);
                var rawHeight = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4));
                if (rawWidth > int.MaxValue || rawHeight > int.MaxValue) return false;
                width = (int)rawWidth;
                height = (int)rawHeight;
                firstChunk = false;
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                sawImageData = true;
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                return length == 0 && sawImageData;
            }
            position += 12 + (int)length;
        }
        return false;
    }

    private static bool TryReadJpeg(ReadOnlySpan<byte> bytes, out int width, out int height)
    {
        width = height = 0;
        if (bytes.Length < 12 || !HasJpegEndMarker(bytes)) return false;
        var position = 2;
        while (position < bytes.Length - 1)
        {
            while (position < bytes.Length && bytes[position] != 0xFF) position++;
            while (position < bytes.Length && bytes[position] == 0xFF) position++;
            if (position >= bytes.Length) break;
            var marker = bytes[position++];
            if (marker is 0xD8 or 0xD9 || marker is >= 0xD0 and <= 0xD7 || marker == 0x01) continue;
            if (marker == 0xDA) break;
            if (position > bytes.Length - 2) return false;
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(position, 2));
            if (length < 2 || position + length > bytes.Length) return false;
            if (IsStartOfFrame(marker))
            {
                if (length < 7) return false;
                height = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(position + 3, 2));
                width = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(position + 5, 2));
                return width > 0 && height > 0;
            }
            position += length;
        }
        return false;
    }

    private static bool TryReadGif(ReadOnlySpan<byte> bytes, out int width, out int height)
    {
        width = height = 0;
        if (bytes.Length < 14 || bytes[^1] != 0x3B) return false;
        width = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(6, 2));
        height = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(8, 2));
        return width > 0 && height > 0;
    }

    private static bool TryReadBmp(ReadOnlySpan<byte> bytes, out int width, out int height)
    {
        width = height = 0;
        if (bytes.Length < 54) return false;
        var declaredSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(2, 4));
        var pixelOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(10, 4));
        var dibSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(14, 4));
        if (declaredSize > bytes.Length || pixelOffset >= bytes.Length || dibSize < 40) return false;
        width = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(18, 4));
        var signedHeight = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(22, 4));
        if (width <= 0 || signedHeight == int.MinValue) return false;
        height = Math.Abs(signedHeight);
        return height > 0;
    }

    private static bool TryReadTiff(ReadOnlySpan<byte> bytes, out int width, out int height)
    {
        width = height = 0;
        if (bytes.Length < 14) return false;
        var littleEndian = bytes[0] == (byte)'I';
        var ifdOffset = ReadUInt32(bytes.Slice(4, 4), littleEndian);
        if (ifdOffset > int.MaxValue || ifdOffset > bytes.Length - 2) return false;
        var position = (int)ifdOffset;
        var entries = ReadUInt16(bytes.Slice(position, 2), littleEndian);
        position += 2;
        if (entries > 4096 || position + entries * 12L > bytes.Length) return false;
        for (var index = 0; index < entries; index++, position += 12)
        {
            var entry = bytes.Slice(position, 12);
            var tag = ReadUInt16(entry[..2], littleEndian);
            if (tag is not (256 or 257)) continue;
            var value = ReadTiffScalar(entry, littleEndian);
            if (value is null or > int.MaxValue) return false;
            if (tag == 256) width = (int)value.Value;
            else height = (int)value.Value;
        }
        return width > 0 && height > 0;
    }

    private static bool TryReadWebP(ReadOnlySpan<byte> bytes, out int width, out int height)
    {
        width = height = 0;
        if (bytes.Length < 30) return false;
        var declaredSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(4, 4)) + 8L;
        if (declaredSize > bytes.Length) return false;
        var chunk = bytes.Slice(12, 4);
        if (chunk.SequenceEqual("VP8X"u8))
        {
            width = 1 + ReadUInt24LittleEndian(bytes.Slice(24, 3));
            height = 1 + ReadUInt24LittleEndian(bytes.Slice(27, 3));
            return true;
        }
        if (chunk.SequenceEqual("VP8 "u8)
            && bytes[23] == 0x9D && bytes[24] == 0x01 && bytes[25] == 0x2A)
        {
            width = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(26, 2)) & 0x3FFF;
            height = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(28, 2)) & 0x3FFF;
            return true;
        }
        if (chunk.SequenceEqual("VP8L"u8) && bytes[20] == 0x2F)
        {
            width = 1 + bytes[21] + ((bytes[22] & 0x3F) << 8);
            height = 1 + (bytes[22] >> 6) + (bytes[23] << 2) + ((bytes[24] & 0x0F) << 10);
            return true;
        }
        return false;
    }

    private static bool IsTiff(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 8
        && ((bytes[..4].SequenceEqual(new byte[] { 0x49, 0x49, 0x2A, 0x00 }))
            || bytes[..4].SequenceEqual(new byte[] { 0x4D, 0x4D, 0x00, 0x2A }));

    private static bool HasJpegEndMarker(ReadOnlySpan<byte> bytes)
    {
        for (var index = bytes.Length - 2; index >= 2; index--)
        {
            if (bytes[index] == 0xFF && bytes[index + 1] == 0xD9) return true;
        }
        return false;
    }

    private static bool IsStartOfFrame(byte marker) =>
        marker is 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7
            or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF;

    private static uint? ReadTiffScalar(ReadOnlySpan<byte> entry, bool littleEndian)
    {
        var type = ReadUInt16(entry.Slice(2, 2), littleEndian);
        var count = ReadUInt32(entry.Slice(4, 4), littleEndian);
        if (count != 1) return null;
        return type switch
        {
            3 => ReadUInt16(entry.Slice(8, 2), littleEndian),
            4 => ReadUInt32(entry.Slice(8, 4), littleEndian),
            _ => null
        };
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, bool littleEndian) =>
        littleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(bytes)
            : BinaryPrimitives.ReadUInt16BigEndian(bytes);

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, bool littleEndian) =>
        littleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(bytes)
            : BinaryPrimitives.ReadUInt32BigEndian(bytes);

    private static int ReadUInt24LittleEndian(ReadOnlySpan<byte> bytes) =>
        bytes[0] | (bytes[1] << 8) | (bytes[2] << 16);
}

public enum ArtworkImageFormat
{
    Jpeg,
    Png,
    Gif,
    Bmp,
    Tiff,
    WebP
}

public enum ArtworkImageRejectionReason
{
    None,
    Missing,
    Empty,
    EncodedSizeLimit,
    UnsupportedFormat,
    CorruptStructure,
    DimensionLimit
}

public readonly record struct ArtworkImageInfo(
    ArtworkImageFormat Format,
    string Extension,
    int Width,
    int Height,
    long EncodedBytes)
{
    public long Pixels => (long)Width * Height;
}

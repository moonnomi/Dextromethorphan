using System.Text;

namespace Dextromethorphan.DopQualification;

internal static class DsdSilenceFixture
{
    internal const int Channels = 2;
    internal const int BlockSizePerChannel = 4_096;
    internal const byte SilenceByte = 0x69;

    internal static GeneratedDsdFixture Write(
        string directory,
        int dsdSampleRate,
        int seconds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(seconds, 2);
        if (dsdSampleRate is not (2_822_400 or 5_644_800))
            throw new ArgumentOutOfRangeException(nameof(dsdSampleRate));

        Directory.CreateDirectory(directory);
        var dsdLevel = dsdSampleRate == 2_822_400 ? 64 : 128;
        var path = Path.Combine(directory, $"generated-dsd{dsdLevel}-silence.dsf");
        var sampleCount = checked((long)dsdSampleRate * seconds);
        var sourceBytesPerChannel = checked((sampleCount + 7) / 8);
        var blocks = checked((int)Math.Ceiling(
            sourceBytesPerChannel / (double)BlockSizePerChannel));
        var storedBytesPerChannel = checked((long)blocks * BlockSizePerChannel);
        var dataBytes = checked(storedBytesPerChannel * Channels);

        using var file = File.Create(path);
        using var writer = new BinaryWriter(file, Encoding.ASCII, true);
        writer.Write(Encoding.ASCII.GetBytes("DSD "));
        writer.Write(28L);
        writer.Write(0L); // Patched after the data chunk is complete.
        writer.Write(0L);
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(52L);
        writer.Write(1);
        writer.Write(0);
        writer.Write(2); // Stereo channel type.
        writer.Write(Channels);
        writer.Write(dsdSampleRate);
        writer.Write(1); // One-bit samples in the DSF-defined bit order.
        writer.Write(sampleCount);
        writer.Write(BlockSizePerChannel);
        writer.Write(0);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(checked(12L + dataBytes));

        var silenceBlock = new byte[BlockSizePerChannel];
        Array.Fill(silenceBlock, SilenceByte);
        for (var block = 0; block < blocks; block++)
        for (var channel = 0; channel < Channels; channel++)
            writer.Write(silenceBlock);

        writer.Flush();
        var fileSize = file.Length;
        file.Position = 12;
        writer.Write(fileSize);
        writer.Flush();
        return new(
            path,
            dsdSampleRate,
            dsdSampleRate / 16,
            TimeSpan.FromSeconds(seconds),
            fileSize);
    }
}

internal sealed record GeneratedDsdFixture(
    string Path,
    int DsdSampleRate,
    int DopCarrierSampleRate,
    TimeSpan Duration,
    long FileSize);

using System.Runtime.InteropServices;
using System.Text;

namespace Dextromethorphan.Infrastructure.Audio;

internal sealed class DstNativeDecoder : IDisposable
{
    private const int ErrorCapacity = 512;
    private IntPtr _handle;

    public DstNativeDecoder(int channels, int sampleRate)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException(
                "DST-compressed DFF playback currently requires the win-x64 build.");
        if (NativeMethods.ApiVersion() != 1)
            throw new InvalidOperationException("Unsupported native DST decoder API version.");

        var error = new byte[ErrorCapacity];
        _handle = NativeMethods.Create(
            checked((uint)channels),
            checked((uint)sampleRate),
            out var frameBytes,
            error,
            (nuint)error.Length);
        if (_handle == IntPtr.Zero)
            throw new InvalidDataException(ReadError(error));
        FrameBytes = checked((int)frameBytes);
    }

    public int FrameBytes { get; }

    public int Decode(byte[] input, int inputLength, byte[] output)
    {
        ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
        ArgumentOutOfRangeException.ThrowIfNegative(inputLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(inputLength, input.Length);
        if (output.Length < FrameBytes)
            throw new ArgumentException(
                $"DST output requires at least {FrameBytes:N0} bytes.",
                nameof(output));

        var error = new byte[ErrorCapacity];
        var written = NativeMethods.Decode(
            _handle,
            input,
            checked((nuint)inputLength),
            output,
            checked((nuint)output.Length),
            error,
            (nuint)error.Length);
        if (written < 0)
            throw new InvalidDataException(
                "Invalid DST frame: " + ReadError(error));
        return checked((int)written);
    }

    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (handle != IntPtr.Zero)
            NativeMethods.Destroy(handle);
        GC.SuppressFinalize(this);
    }

    ~DstNativeDecoder() => Dispose();

    private static string ReadError(byte[] error)
    {
        var terminator = Array.IndexOf(error, (byte)0);
        if (terminator < 0) terminator = error.Length;
        var message = Encoding.UTF8.GetString(error, 0, terminator);
        return string.IsNullOrWhiteSpace(message)
            ? "The native DST decoder did not provide an error message."
            : message;
    }

    private static class NativeMethods
    {
        private const string Library = "dextromethorphan_dst";

        [DefaultDllImportSearchPaths(
            DllImportSearchPath.AssemblyDirectory
            | DllImportSearchPath.SafeDirectories)]
        [DllImport(Library, EntryPoint = "dext_dst_api_version", CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint ApiVersion();

        [DefaultDllImportSearchPaths(
            DllImportSearchPath.AssemblyDirectory
            | DllImportSearchPath.SafeDirectories)]
        [DllImport(Library, EntryPoint = "dext_dst_create", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr Create(
            uint channels,
            uint sampleRate,
            out nuint frameBytes,
            [Out] byte[] error,
            nuint errorCapacity);

        [DefaultDllImportSearchPaths(
            DllImportSearchPath.AssemblyDirectory
            | DllImportSearchPath.SafeDirectories)]
        [DllImport(Library, EntryPoint = "dext_dst_decode", CallingConvention = CallingConvention.Cdecl)]
        internal static extern nint Decode(
            IntPtr decoder,
            byte[] input,
            nuint inputLength,
            [Out] byte[] output,
            nuint outputLength,
            [Out] byte[] error,
            nuint errorCapacity);

        [DefaultDllImportSearchPaths(
            DllImportSearchPath.AssemblyDirectory
            | DllImportSearchPath.SafeDirectories)]
        [DllImport(Library, EntryPoint = "dext_dst_destroy", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Destroy(IntPtr decoder);
    }
}

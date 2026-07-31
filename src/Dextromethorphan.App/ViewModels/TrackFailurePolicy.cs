using System.IO;
using System.Windows.Media;

namespace Dextromethorphan.App.ViewModels;

internal static class TrackFailurePolicy
{
    private const int UnsupportedMediaHResult =
        unchecked((int)0xC00D36C4);

    public static bool IsRecoverable(Exception exception)
    {
        var error = exception.GetBaseException();
        return error is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or InvalidDataException
            or FileFormatException
            || error.HResult == UnsupportedMediaHResult;
    }

    public static string FriendlyMessage(Exception exception) =>
        exception.GetBaseException().HResult == UnsupportedMediaHResult
            ? "Windows could not decode this audio format"
            : exception.GetBaseException().Message;
}

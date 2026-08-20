using System.Net.Sockets;
using NzbWebDAV.Exceptions;

namespace NzbWebDAV.Extensions;

public static class ExceptionExtensions
{
    public static bool IsRetryableDownloadException(this Exception exception)
    {
        return exception is RetryableDownloadException;
    }

    public static bool IsNonRetryableDownloadException(this Exception exception)
    {
        return exception is NonRetryableDownloadException
            or SharpCompress.Common.InvalidFormatException;
    }

    public static bool IsCancellationException(this Exception exception)
    {
        return exception is TaskCanceledException or OperationCanceledException;
    }

    /// <summary>
    /// Returns true when a socket operation failed because the process or host could not
    /// allocate another file descriptor. Retrying against another provider cannot help in
    /// this state and only makes descriptor pressure worse.
    /// </summary>
    public static bool IsFileDescriptorExhaustion(this Exception exception)
    {
        if (!exception.TryGetCausingException(out SocketException? socketException)) return false;

        // Linux: ENFILE=23 (system-wide), EMFILE=24 (per-process).
        // Windows: WSAEMFILE=10024.
        return socketException?.NativeErrorCode is 23 or 24 or 10024;
    }

    public static bool TryGetCausingException<T>(this Exception exception, out T? exceptionType) where T : Exception
    {
        ArgumentNullException.ThrowIfNull(exception);
        var current = exception;

        while (current != null)
        {
            if (current is T matching)
            {
                exceptionType = matching;
                return true;
            }

            current = current.InnerException;
        }

        exceptionType = null;
        return false;
    }
}

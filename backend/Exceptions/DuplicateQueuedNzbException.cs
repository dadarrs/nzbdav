using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace NzbWebDAV.Exceptions;

public sealed class DuplicateQueuedNzbException(string fileName, string category, Exception? inner = null)
    : Exception($"\"{fileName}\" is already queued in category \"{category}\". " +
                "Wait for the existing import to finish, or remove it from the queue before uploading again.", inner)
{
    public static bool IsQueueDuplicate(DbUpdateException exception)
    {
        // Only translate this specific unique constraint, not unrelated database failures.
        return exception.InnerException is SqliteException { SqliteExtendedErrorCode: 2067 } sqlite
               && sqlite.Message.Contains("UNIQUE constraint failed: QueueItems.Category, QueueItems.FileName",
                   StringComparison.Ordinal);
    }
}

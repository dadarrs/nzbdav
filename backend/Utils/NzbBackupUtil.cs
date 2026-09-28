using Serilog;

namespace NzbWebDAV.Utils;

public static class NzbBackupUtil
{
    // The caller owns source. Nothing here should reject an otherwise valid upload.
    public static async Task TryBackupAsync(Stream source, string fileName, string category, string? backupLocation)
    {
        if (string.IsNullOrWhiteSpace(backupLocation))
        {
            Log.Warning("NZB backups are enabled but no backup directory is configured. " +
                        "Skipping optional backup for {FileName}; import will continue", fileName);
            return;
        }

        string? createdPath = null;
        try
        {
            var destDir = Path.Combine(backupLocation, category);
            Directory.CreateDirectory(destDir);
            var baseName = Path.GetFileNameWithoutExtension(fileName);
            var ext = Path.GetExtension(fileName);
            if (string.IsNullOrEmpty(ext)) ext = ".nzb";

            for (var counter = 1; ; counter++)
            {
                var name = counter == 1 ? $"{baseName}{ext}" : $"{baseName} ({counter}){ext}";
                var destPath = Path.Combine(destDir, name);
                FileStream destination;
                try
                {
                    // Reserve the name atomically: never truncate another upload's backup.
                    destination = new FileStream(destPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                }
                catch (IOException) when (File.Exists(destPath))
                {
                    continue;
                }

                createdPath = destPath;
                await using (destination)
                    await source.CopyToAsync(destination).ConfigureAwait(false);
                return;
            }
        }
        catch (Exception e)
        {
            Log.Warning(e, "Could not save optional NZB backup for {FileName} to {BackupLocation}. " +
                           "Import will continue", fileName, backupLocation);

            if (createdPath == null) return;
            try
            {
                // Only remove a file this attempt actually created, after closing its handle.
                File.Delete(createdPath);
            }
            catch (Exception cleanupError)
            {
                Log.Warning(cleanupError, "Could not remove partial NZB backup {BackupPath}", createdPath);
            }
        }
    }
}

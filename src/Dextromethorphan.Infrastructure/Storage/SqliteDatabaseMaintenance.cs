using Microsoft.Data.Sqlite;

namespace Dextromethorphan.Infrastructure.Storage;

public sealed record DatabaseIntegrityResult(
    bool IsHealthy,
    string Message,
    int SchemaVersion);

public sealed class DatabaseMigrationException(
    int fromVersion,
    int targetVersion,
    string? backupPath,
    Exception innerException)
    : Exception(
        $"Library database migration {fromVersion} → {targetVersion} failed."
        + (backupPath is null ? "" : " The pre-migration backup was restored."),
        innerException)
{
    public int FromVersion { get; } = fromVersion;
    public int TargetVersion { get; } = targetVersion;
    public string? BackupPath { get; } = backupPath;
}

public sealed class DatabaseCorruptionException(
    string message,
    string databasePath)
    : Exception(
        $"The library database failed its integrity check: {message}")
{
    public string DatabasePath { get; } = databasePath;
}

public static class SqliteDatabaseMaintenance
{
    public const int DefaultRetainedBackups = 5;

    public static async Task<string?> CreateBackupAsync(
        AppPaths paths,
        int retainedBackups = DefaultRetainedBackups,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(paths.DatabaseFile)
            || new FileInfo(paths.DatabaseFile).Length == 0)
            return null;
        paths.EnsureCreated();
        var backupPath = Path.Combine(
            paths.DatabaseBackups,
            $"library-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fffffff}.db");
        await using var source = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabaseFile,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = backupPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        await source.OpenAsync(cancellationToken);
        await destination.OpenAsync(cancellationToken);
        source.BackupDatabase(destination);
        await destination.CloseAsync();
        await source.CloseAsync();
        PruneBackups(paths, retainedBackups);
        return backupPath;
    }

    public static async Task RestoreBackupAsync(
        AppPaths paths,
        string backupPath,
        CancellationToken cancellationToken = default)
    {
        var fullBackup = Path.GetFullPath(backupPath);
        var backupRoot = Path.GetFullPath(paths.DatabaseBackups)
            .TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!fullBackup.StartsWith(backupRoot, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(fullBackup))
            throw new InvalidOperationException("The selected database backup is outside the application backup directory.");

        SqliteConnection.ClearAllPools();
        var temporary = paths.DatabaseFile + ".restore.tmp";
        await using (var source = new FileStream(
                         fullBackup,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         64 * 1024,
                         true))
        await using (var target = new FileStream(
                         temporary,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         64 * 1024,
                         true))
        {
            await source.CopyToAsync(target, cancellationToken);
            await target.FlushAsync(cancellationToken);
            target.Flush(flushToDisk: true);
        }
        if (File.Exists(paths.DatabaseFile))
            File.Replace(
                temporary,
                paths.DatabaseFile,
                paths.DatabaseFile + ".before-restore",
                ignoreMetadataErrors: true);
        else
            File.Move(temporary, paths.DatabaseFile);
        DeleteSidecars(paths.DatabaseFile);
    }

    public static async Task<DatabaseIntegrityResult> CheckIntegrityAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check; PRAGMA user_version;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var message = await reader.ReadAsync(cancellationToken)
            ? reader.GetString(0)
            : "No integrity result was returned.";
        await reader.NextResultAsync(cancellationToken);
        var version = await reader.ReadAsync(cancellationToken)
            ? reader.GetInt32(0)
            : 0;
        return new DatabaseIntegrityResult(
            message.Equals("ok", StringComparison.OrdinalIgnoreCase),
            message,
            version);
    }

    public static IReadOnlyList<string> ListBackups(AppPaths paths) =>
        Directory.Exists(paths.DatabaseBackups)
            ? Directory.EnumerateFiles(paths.DatabaseBackups, "library-*.db")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToArray()
            : [];

    private static void PruneBackups(AppPaths paths, int retainedBackups)
    {
        retainedBackups = Math.Clamp(retainedBackups, 1, 20);
        foreach (var backup in ListBackups(paths).Skip(retainedBackups))
        {
            try { File.Delete(backup); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void DeleteSidecars(string databasePath)
    {
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            try
            {
                var path = databasePath + suffix;
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

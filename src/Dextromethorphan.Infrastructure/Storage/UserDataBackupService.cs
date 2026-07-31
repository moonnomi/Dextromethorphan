using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dextromethorphan.Core.Abstractions;
using Dextromethorphan.Core.Models;
using Dextromethorphan.Infrastructure.Settings;
using Microsoft.Data.Sqlite;

namespace Dextromethorphan.Infrastructure.Storage;

public sealed class UserDataBackupService(
    AppPaths paths,
    ISettingsService settings,
    SqliteLibraryRepository library)
{
    private const long MaximumSettingsBytes = 4L * 1024 * 1024;
    private const long MaximumDatabaseBytes = 4L * 1024 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task ExportAsync(
        string destination,
        CancellationToken cancellationToken = default)
    {
        var outputPath = Path.GetFullPath(destination);
        Directory.CreateDirectory(
            Path.GetDirectoryName(outputPath)
            ?? throw new InvalidOperationException(
                "Backup destination has no parent directory."));
        await settings.SaveAsync(cancellationToken);
        var databaseBackup =
            await SqliteDatabaseMaintenance.CreateBackupAsync(
                paths,
                cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException(
                "The library database has not been created yet.");
        var temporary = outputPath + ".tmp";
        try
        {
            await using (var output = new FileStream(
                             temporary,
                             FileMode.Create,
                             FileAccess.ReadWrite,
                             FileShare.None,
                             64 * 1024,
                             true))
            using (var archive = new ZipArchive(
                       output,
                       ZipArchiveMode.Create,
                       leaveOpen: true))
            {
                await WriteJsonAsync(
                    archive,
                    "manifest.json",
                    new UserDataBackupManifest
                    {
                        CreatedAt = DateTimeOffset.UtcNow,
                        AppSettingsSchema = AppSettings.CurrentSchemaVersion,
                        DatabaseSchema =
                            SqliteLibraryRepository.CurrentSchemaVersion,
                        IncludesLibraryIndex = true,
                        IncludesMediaFiles = false
                    },
                    cancellationToken);
                await AddFileAsync(
                    archive,
                    "settings.json",
                    paths.SettingsFile,
                    cancellationToken);
                await AddFileAsync(
                    archive,
                    "library.db",
                    databaseBackup,
                    cancellationToken);
            }
            File.Move(temporary, outputPath, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    public async Task RestoreAsync(
        string source,
        CancellationToken cancellationToken = default)
    {
        var sourcePath = Path.GetFullPath(source);
        var stagingRoot = Path.Combine(
            paths.Root,
            "restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingRoot);
        var stagedSettings = Path.Combine(stagingRoot, "settings.json");
        var stagedDatabase = Path.Combine(stagingRoot, "library.db");
        string? preRestoreBackup = null;
        try
        {
            using (var archive = ZipFile.OpenRead(sourcePath))
            {
                var manifest = await ReadJsonAsync<UserDataBackupManifest>(
                    RequiredEntry(archive, "manifest.json"),
                    64 * 1024,
                    cancellationToken);
                if (manifest.SchemaVersion != 1)
                    throw new InvalidDataException(
                        "Unsupported backup format.");
                if (manifest.DatabaseSchema
                    > SqliteLibraryRepository.CurrentSchemaVersion)
                    throw new InvalidDataException(
                        "This backup was created by a newer app version.");

                await ExtractEntryAsync(
                    RequiredEntry(archive, "settings.json"),
                    stagedSettings,
                    MaximumSettingsBytes,
                    cancellationToken);
                await ExtractEntryAsync(
                    RequiredEntry(archive, "library.db"),
                    stagedDatabase,
                    MaximumDatabaseBytes,
                    cancellationToken);
            }

            await ValidateSettingsAsync(
                stagedSettings,
                cancellationToken);
            await ValidateDatabaseAsync(
                stagedDatabase,
                cancellationToken);
            preRestoreBackup =
                await SqliteDatabaseMaintenance.CreateBackupAsync(
                    paths,
                    cancellationToken: cancellationToken);

            SqliteConnection.ClearAllPools();
            var databaseTemporary = paths.DatabaseFile + ".user-restore.tmp";
            File.Copy(stagedDatabase, databaseTemporary, overwrite: true);
            if (File.Exists(paths.DatabaseFile))
                File.Replace(
                    databaseTemporary,
                    paths.DatabaseFile,
                    paths.DatabaseFile + ".before-user-restore",
                    ignoreMetadataErrors: true);
            else
                File.Move(databaseTemporary, paths.DatabaseFile);
            DeleteSidecars(paths.DatabaseFile);
            try
            {
                await settings.ImportAsync(
                    stagedSettings,
                    cancellationToken);
                await library.InitializeAsync(cancellationToken);
            }
            catch
            {
                if (preRestoreBackup is not null)
                    await SqliteDatabaseMaintenance.RestoreBackupAsync(
                        paths,
                        preRestoreBackup,
                        CancellationToken.None);
                throw;
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(stagingRoot))
                    Directory.Delete(stagingRoot, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task ValidateSettingsAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var imported = await JsonSerializer.DeserializeAsync<AppSettings>(
                           stream,
                           JsonOptions,
                           cancellationToken)
                       ?? throw new InvalidDataException(
                           "Backup settings are empty.");
        JsonSettingsService.Normalize(imported);
    }

    private static async Task ValidateDatabaseAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
        await connection.OpenAsync(cancellationToken);
        var integrity = await SqliteDatabaseMaintenance.CheckIntegrityAsync(
            connection,
            cancellationToken);
        if (!integrity.IsHealthy)
            throw new InvalidDataException(
                $"Backup database is damaged: {integrity.Message}");
        if (integrity.SchemaVersion
            > SqliteLibraryRepository.CurrentSchemaVersion)
            throw new InvalidDataException(
                "Backup database schema is newer than this app supports.");
    }

    private static ZipArchiveEntry RequiredEntry(
        ZipArchive archive,
        string name)
    {
        var matches = archive.Entries
            .Where(entry => entry.FullName.Equals(
                name,
                StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
            throw new InvalidDataException(
                $"Backup must contain exactly one {name} entry.");
        return matches[0];
    }

    private static async Task ExtractEntryAsync(
        ZipArchiveEntry entry,
        string destination,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        if (entry.Length < 0 || entry.Length > maximumBytes)
            throw new InvalidDataException(
                $"{entry.FullName} exceeds the backup safety limit.");
        await using var source = entry.Open();
        await using var target = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            true);
        await source.CopyToAsync(target, cancellationToken);
        await target.FlushAsync(cancellationToken);
        target.Flush(flushToDisk: true);
    }

    private static async Task<T> ReadJsonAsync<T>(
        ZipArchiveEntry entry,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        if (entry.Length < 0 || entry.Length > maximumBytes)
            throw new InvalidDataException(
                $"{entry.FullName} exceeds the backup safety limit.");
        await using var stream = entry.Open();
        return await JsonSerializer.DeserializeAsync<T>(
                   stream,
                   JsonOptions,
                   cancellationToken)
               ?? throw new InvalidDataException(
                   $"{entry.FullName} is empty.");
    }

    private static async Task AddFileAsync(
        ZipArchive archive,
        string name,
        string source,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var input = new FileStream(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            true);
        await using var output = entry.Open();
        await input.CopyToAsync(output, cancellationToken);
    }

    private static async Task WriteJsonAsync(
        ZipArchive archive,
        string name,
        object value,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await JsonSerializer.SerializeAsync(
            stream,
            value,
            value.GetType(),
            JsonOptions,
            cancellationToken);
    }

    private static void DeleteSidecars(string databasePath)
    {
        foreach (var suffix in new[] { "-wal", "-shm" })
            TryDelete(databasePath + suffix);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public sealed record UserDataBackupManifest
{
    public int SchemaVersion { get; init; } = 1;
    public DateTimeOffset CreatedAt { get; init; }
    public int AppSettingsSchema { get; init; }
    public int DatabaseSchema { get; init; }
    public bool IncludesLibraryIndex { get; init; }
    public bool IncludesMediaFiles { get; init; }
}

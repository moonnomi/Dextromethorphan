using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dextromethorphan.Core.Abstractions;
using Dextromethorphan.Infrastructure.Audio;
using Dextromethorphan.Infrastructure.Storage;

namespace Dextromethorphan.App.Diagnostics;

public sealed partial class DiagnosticsBundleExporter(
    AppPaths paths,
    ISettingsService settings,
    IAudioEngine audio,
    SqliteLibraryRepository library,
    AudioDecoderCapabilityService decoders)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new() { WriteIndented = true };

    public async Task ExportAsync(
        string destination,
        CancellationToken cancellationToken = default)
    {
        var fullDestination = Path.GetFullPath(destination);
        Directory.CreateDirectory(
            Path.GetDirectoryName(fullDestination)
            ?? throw new InvalidOperationException(
                "Diagnostics destination has no parent directory."));
        var temporary = fullDestination + ".tmp";
        if (File.Exists(temporary)) File.Delete(temporary);
        try
        {
            await using (var output = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.ReadWrite,
                             FileShare.None,
                             64 * 1024,
                             true))
            using (var archive = new ZipArchive(
                       output,
                       ZipArchiveMode.Create,
                       leaveOpen: true))
            {
                var redactor = new DiagnosticsRedactor(
                    paths.Root,
                    settings.Current.LibraryFolders);
                await WriteJsonAsync(
                    archive,
                    "manifest.json",
                    new
                    {
                        schemaVersion = 1,
                        createdAt = DateTimeOffset.UtcNow,
                        appVersion = Assembly.GetExecutingAssembly()
                            .GetName().Version?.ToString(),
                        runtime = Environment.Version.ToString(),
                        os = Environment.OSVersion.VersionString,
                        architecture =
                            System.Runtime.InteropServices.RuntimeInformation
                                .ProcessArchitecture.ToString(),
                        containsMusicDatabase = false,
                        containsMediaFiles = false,
                        pathsRedacted = true
                    },
                    cancellationToken);
                await WriteJsonAsync(
                    archive,
                    "settings.redacted.json",
                    new
                    {
                        settings.Current.SchemaVersion,
                        settings.Current.Theme,
                        settings.Current.FontSize,
                        settings.Current.AnimationsEnabled,
                        settings.Current.ResumeOnStartup,
                        settings.Current.ReplayGainMode,
                        settings.Current.TransitionMode,
                        settings.Current.CrossfadeSeconds,
                        settings.Current.PlaybackSpeed,
                        settings.Current.AlbumTileSize,
                        settings.Current.ArtworkCacheMegabytes,
                        librarySourceCount =
                            settings.Current.LibraryFolders.Count,
                        outputProfiles =
                            settings.Current.OutputProfiles.Select(
                                profile => new
                                {
                                    device = DiagnosticsRedactor.HashIdentifier(
                                        profile.DeviceId),
                                    profile.Mode,
                                    profile.BufferMilliseconds,
                                    profile.SampleRatePolicy,
                                    profile.PreferredSampleRate,
                                    profile.BitDepthPolicy,
                                    profile.PreferredBitDepth,
                                    profile.ChannelPolicy,
                                    profile.VolumeControl,
                                    profile.DsdMode,
                                    profile.FallbackPolicy,
                                    profile.RecoveryMaximumAttempts,
                                    profile.RecoveryInitialDelayMilliseconds
                                })
                    },
                    cancellationToken);

                object database;
                try
                {
                    var integrity = await library.CheckIntegrityAsync(
                        cancellationToken);
                    database = new
                    {
                        integrity.IsHealthy,
                        integrity.Message,
                        integrity.SchemaVersion,
                        retainedBackups =
                            SqliteDatabaseMaintenance.ListBackups(paths).Count
                    };
                }
                catch (Exception exception)
                {
                    database = new
                    {
                        isHealthy = false,
                        message = exception.GetBaseException().Message,
                        schemaVersion = 0,
                        retainedBackups =
                            SqliteDatabaseMaintenance.ListBackups(paths).Count
                    };
                }
                await WriteJsonAsync(
                    archive,
                    "database.json",
                    database,
                    cancellationToken);

                await WriteJsonAsync(
                    archive,
                    "decoder-capabilities.json",
                    await decoders.InspectAsync(
                        cancellationToken: cancellationToken),
                    cancellationToken);

                var devices = new List<object>();
                try
                {
                    foreach (var device in await audio.GetOutputDevicesAsync(
                                 cancellationToken))
                    {
                        object? capabilities = null;
                        try
                        {
                            var value =
                                await audio.GetDeviceCapabilitiesAsync(
                                    device.Id,
                                    cancellationToken);
                            capabilities = new
                            {
                                value.MixFormat,
                                value.SupportedExclusiveFormats,
                                value.SupportsEventDrivenExclusive
                            };
                        }
                        catch (Exception exception)
                        {
                            capabilities = new
                            {
                                error = exception.GetBaseException().Message
                            };
                        }
                        devices.Add(new
                        {
                            id = DiagnosticsRedactor.HashIdentifier(device.Id),
                            device.Name,
                            device.IsDefault,
                            device.State,
                            device.MixFormat,
                            capabilities
                        });
                    }
                }
                catch (Exception exception)
                {
                    devices.Add(new
                    {
                        error = exception.GetBaseException().Message
                    });
                }
                await WriteJsonAsync(
                    archive,
                    "audio-devices.json",
                    devices,
                    cancellationToken);
                await WriteJsonAsync(
                    archive,
                    "audio-pipeline.json",
                    audio.Diagnostics ?? (object)new
                    {
                        state = audio.Snapshot.State.ToString(),
                        message = "No active audio pipeline."
                    },
                    cancellationToken);

                if (Directory.Exists(paths.Logs))
                {
                    foreach (var log in Directory
                                 .EnumerateFiles(paths.Logs)
                                 .OrderBy(Path.GetFileName)
                                 .TakeLast(10))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string text;
                        try
                        {
                            text = await File.ReadAllTextAsync(
                                log,
                                cancellationToken);
                        }
                        catch (Exception exception) when (
                            exception is IOException
                                or UnauthorizedAccessException)
                        {
                            continue;
                        }
                        var entry = archive.CreateEntry(
                            "logs/" + Path.GetFileName(log),
                            CompressionLevel.Optimal);
                        await using var writer = new StreamWriter(
                            entry.Open(),
                            new UTF8Encoding(false));
                        await writer.WriteAsync(
                            redactor.Redact(text)
                                .AsMemory(),
                            cancellationToken);
                    }
                }
            }
            File.Move(temporary, fullDestination, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch { }
            throw;
        }
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
}

internal sealed partial class DiagnosticsRedactor
{
    private readonly IReadOnlyList<(string Value, string Replacement)>
        _replacements;

    public DiagnosticsRedactor(
        string appRoot,
        IReadOnlyList<string> libraryRoots)
    {
        var replacements = new List<(string, string)>
        {
            (Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile), "<user-profile>"),
            (appRoot, "<app-data>")
        };
        replacements.AddRange(
            libraryRoots.Select(
                (root, index) =>
                    (root, $"<library-root-{index + 1}>")));
        _replacements = replacements
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Item1))
            .OrderByDescending(pair => pair.Item1.Length)
            .ToArray();
    }

    public string Redact(string value)
    {
        var result = value;
        foreach (var (path, replacement) in _replacements)
            result = result.Replace(
                path,
                replacement,
                StringComparison.OrdinalIgnoreCase);
        result = DeviceIdRegex().Replace(
            result,
            match =>
                match.Groups[1].Value
                + HashIdentifier(match.Groups[2].Value)
                + match.Groups[3].Value);
        return result;
    }

    public static string HashIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "<none>";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return "id-" + Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    [GeneratedRegex(
        "(\"(?:deviceId|device|DeviceId)\"\\s*:\\s*\")([^\"]+)(\")",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeviceIdRegex();
}

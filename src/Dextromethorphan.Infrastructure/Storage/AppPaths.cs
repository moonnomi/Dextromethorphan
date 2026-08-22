namespace Dextromethorphan.Infrastructure.Storage;

public sealed class AppPaths
{
    public const string DataRootEnvironmentVariable = "DEXTROMETHORPHAN_DATA_ROOT";
    public const string PortableMarkerFileName = "portable.mode";

    public AppPaths(string? root = null)
    {
        var configuredRoot = string.IsNullOrWhiteSpace(root)
            ? Environment.GetEnvironmentVariable(DataRootEnvironmentVariable)
            : root;
        (Root, IsPortable) = ResolveRoot(
            configuredRoot,
            AppContext.BaseDirectory,
            Environment.GetCommandLineArgs(),
            File.Exists(Path.Combine(AppContext.BaseDirectory, PortableMarkerFileName)),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        SettingsFile = Path.Combine(Root, "settings.json");
        // Storage generation 2 uses a different SQLite basename. This lets an
        // upgraded app recover from a legacy process that is stuck holding the
        // old WAL shared-memory file without abandoning the catalog itself.
        LegacyDatabaseFile = Path.Combine(Root, "library.db");
        DatabaseFile = Path.Combine(Root, "library-v2.db");
        ScanCheckpointFile = Path.Combine(Root, "scan-checkpoint.json");
        StartupStateFile = Path.Combine(Root, "startup-state.json");
        DatabaseBackups = Path.Combine(Root, "backups");
        ArtworkCache = Path.Combine(Root, "artwork");
        LyricsCache = Path.Combine(Root, "lyrics");
        Logs = Path.Combine(Root, "logs");
    }

    public string Root { get; }
    public bool IsPortable { get; }
    public string SettingsFile { get; }
    public string LegacyDatabaseFile { get; }
    public string DatabaseFile { get; }
    public string ScanCheckpointFile { get; }
    public string StartupStateFile { get; }
    public string DatabaseBackups { get; }
    public string ArtworkCache { get; }
    public string LyricsCache { get; }
    public string Logs { get; }

    internal static (string Root, bool Portable) ResolveRoot(
        string? configuredRoot,
        string executableDirectory,
        IEnumerable<string> arguments,
        bool markerExists,
        string appDataDirectory)
    {
        var portable = string.IsNullOrWhiteSpace(configuredRoot)
            && (markerExists || arguments.Any(argument => argument.Equals("--portable", StringComparison.OrdinalIgnoreCase)));
        var root = portable
            ? Path.Combine(executableDirectory, "data")
            : string.IsNullOrWhiteSpace(configuredRoot)
                ? Path.Combine(appDataDirectory, "Dextromethorphan")
                : configuredRoot;
        return (Path.GetFullPath(root), portable);
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ArtworkCache);
        Directory.CreateDirectory(LyricsCache);
        Directory.CreateDirectory(DatabaseBackups);
        Directory.CreateDirectory(Logs);
    }
}

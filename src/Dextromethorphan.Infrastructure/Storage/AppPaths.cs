namespace Dextromethorphan.Infrastructure.Storage;

public sealed class AppPaths
{
    public const string DataRootEnvironmentVariable = "DEXTROMETHORPHAN_DATA_ROOT";

    public AppPaths(string? root = null)
    {
        var configuredRoot = string.IsNullOrWhiteSpace(root)
            ? Environment.GetEnvironmentVariable(DataRootEnvironmentVariable)
            : root;
        Root = Path.GetFullPath(string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dextromethorphan")
            : configuredRoot);
        SettingsFile = Path.Combine(Root, "settings.json");
        DatabaseFile = Path.Combine(Root, "library.db");
        ScanCheckpointFile = Path.Combine(Root, "scan-checkpoint.json");
        StartupStateFile = Path.Combine(Root, "startup-state.json");
        DatabaseBackups = Path.Combine(Root, "backups");
        ArtworkCache = Path.Combine(Root, "artwork");
        Logs = Path.Combine(Root, "logs");
    }

    public string Root { get; }
    public string SettingsFile { get; }
    public string DatabaseFile { get; }
    public string ScanCheckpointFile { get; }
    public string StartupStateFile { get; }
    public string DatabaseBackups { get; }
    public string ArtworkCache { get; }
    public string Logs { get; }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ArtworkCache);
        Directory.CreateDirectory(DatabaseBackups);
        Directory.CreateDirectory(Logs);
    }
}

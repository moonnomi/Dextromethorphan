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
        ArtworkCache = Path.Combine(Root, "artwork");
        Logs = Path.Combine(Root, "logs");
    }

    public string Root { get; }
    public string SettingsFile { get; }
    public string DatabaseFile { get; }
    public string ArtworkCache { get; }
    public string Logs { get; }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ArtworkCache);
        Directory.CreateDirectory(Logs);
    }
}

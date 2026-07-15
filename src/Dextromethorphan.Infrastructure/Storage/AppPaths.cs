namespace Dextromethorphan.Infrastructure.Storage;

public sealed class AppPaths
{
    public AppPaths(string? root = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dextromethorphan");
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

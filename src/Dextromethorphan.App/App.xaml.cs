using System.IO;
using System.Windows;
using Dextromethorphan.App.ViewModels;
using Dextromethorphan.App.WindowsIntegration;
using Dextromethorphan.Core.Abstractions;
using Dextromethorphan.Core.Playback;
using Dextromethorphan.Infrastructure.Audio;
using Dextromethorphan.Infrastructure.Library;
using Dextromethorphan.Infrastructure.Settings;
using Dextromethorphan.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Dextromethorphan.App;

public partial class App : Application
{
    private readonly IHost _host = Host.CreateDefaultBuilder()
        .ConfigureServices(services =>
        {
            services.AddSingleton<AppPaths>();
            services.AddSingleton<ISettingsService, JsonSettingsService>();
            services.AddSingleton<SqliteLibraryRepository>();
            services.AddSingleton<ILibraryRepository>(x => x.GetRequiredService<SqliteLibraryRepository>());
            services.AddSingleton<IPlaylistRepository, SqlitePlaylistRepository>();
            services.AddSingleton<IPlaylistInterchangeService, PlaylistInterchangeService>();
            services.AddSingleton<IPlaylistFileService, PlaylistFileService>();
            services.AddSingleton<ITrackMetadataReader, TagLibMetadataReader>();
            services.AddSingleton<IArtworkCache, ArtworkCache>();
            services.AddSingleton<ILibraryScanner, LibraryScanner>();
            services.AddSingleton<IAudioEngine, WasapiAudioEngine>();
            services.AddSingleton<IPlaybackQueue, PlaybackQueue>();
            services.AddSingleton<ISleepTimerService, SleepTimerService>();
            services.AddSingleton<IShortcutService, WindowsShortcutService>();
            services.AddSingleton<ISystemMediaTransportService, SystemMediaTransportService>();
            services.AddSingleton<MainViewModel>();
            services.AddSingleton<MainWindow>();
        })
        .Build();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                var paths = _host.Services.GetRequiredService<AppPaths>();
                paths.EnsureCreated();
                File.AppendAllText(Path.Combine(paths.Logs, "errors.log"), $"[{DateTimeOffset.Now:O}] {args.Exception}\n\n");
            }
            catch { }
            MessageBox.Show(args.Exception.GetBaseException().Message + "\n\nDetails were written to the application log.", "Dextromethorphan", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        await _host.StartAsync();
        var window = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();
        window.BeginStartupPresentation();
        try { await window.ViewModel.InitializeAsync(); }
        finally { await window.CompleteStartupPresentationAsync(); }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        await _host.StopAsync(TimeSpan.FromSeconds(3));
        _host.Dispose();
        base.OnExit(e);
    }
}

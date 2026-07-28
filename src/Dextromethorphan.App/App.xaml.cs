using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Dextromethorphan.App.Diagnostics;
using Dextromethorphan.App.Performance;
using Dextromethorphan.App.UI;
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
            services.AddSingleton<DeveloperDiagnostics>();
            services.AddSingleton<ArtworkPropertyUpdateBatcher>();
            services.AddSingleton<ArtworkImageService>();
            services.AddSingleton<PerformanceOverlayViewModel>();
            services.AddSingleton<AppPaths>();
            services.AddSingleton<ISettingsService, JsonSettingsService>();
            services.AddSingleton<SqliteLibraryRepository>();
            services.AddSingleton<ILibraryRepository>(x => new DiagnosticLibraryRepository(
                x.GetRequiredService<SqliteLibraryRepository>(),
                x.GetRequiredService<DeveloperDiagnostics>()));
            services.AddSingleton<SqlitePlaylistRepository>();
            services.AddSingleton<IPlaylistRepository>(x => new DiagnosticPlaylistRepository(
                x.GetRequiredService<SqlitePlaylistRepository>(),
                x.GetRequiredService<DeveloperDiagnostics>()));
            services.AddSingleton<IPlaylistInterchangeService, PlaylistInterchangeService>();
            services.AddSingleton<IPlaylistFileService, PlaylistFileService>();
            services.AddSingleton<ITrackMetadataReader, TagLibMetadataReader>();
            services.AddSingleton<ArtworkCache>();
            services.AddSingleton<IArtworkCache>(x => new DiagnosticArtworkCache(
                x.GetRequiredService<ArtworkCache>(),
                x.GetRequiredService<DeveloperDiagnostics>()));
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
        var benchmark = PerformanceBenchmarkOptions.Parse(e.Args);
        var paths = _host.Services.GetRequiredService<AppPaths>();
        var diagnostics = _host.Services.GetRequiredService<DeveloperDiagnostics>();
        diagnostics.Configure(DeveloperDiagnosticsOptions.Parse(e.Args, benchmark), paths);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
                diagnostics.Error("runtime", "appdomain.unhandled", exception, new Dictionary<string, object?> { ["terminating"] = args.IsTerminating });
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            diagnostics.Error("runtime", "task.unobserved", args.Exception);
            args.SetObserved();
        };
        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                diagnostics.Error("runtime", "dispatcher.unhandled", args.Exception,
                    MainWindow is MainWindow main ? new Dictionary<string, object?> { ["view"] = main.ViewModel.CurrentView } : null);
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
        var firstContentRendered = new TaskCompletionSource<DateTimeOffset>(TaskCreationOptions.RunContinuationsAsynchronously);
        window.ContentRendered += (_, _) =>
        {
            var renderedAt = DateTimeOffset.UtcNow;
            if (firstContentRendered.TrySetResult(renderedAt))
                diagnostics.RecordDuration("startup", "process-to-first-render", renderedAt - new DateTimeOffset(Process.GetCurrentProcess().StartTime.ToUniversalTime(), TimeSpan.Zero));
        };
        window.Show();
        if (PerformanceOverlayViewModel.IsRequested(e.Args))
            window.PerformanceOverlay.IsVisible = true;
        var windowShownAt = DateTimeOffset.UtcNow;
        window.BeginStartupPresentation();
        var processStartedAt = new DateTimeOffset(Process.GetCurrentProcess().StartTime.ToUniversalTime(), TimeSpan.Zero);
        var libraryReadyAt = windowShownAt;
        try { await window.ViewModel.InitializeAsync(); }
        finally
        {
            libraryReadyAt = DateTimeOffset.UtcNow;
            diagnostics.RecordDuration("startup", "process-to-library-ready", libraryReadyAt - processStartedAt);
            await window.CompleteStartupPresentationAsync();
        }

        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        var interactiveAt = DateTimeOffset.UtcNow;
        diagnostics.RecordDuration("startup", "process-to-interactive", interactiveAt - processStartedAt);
        if (benchmark is null) return;

        try
        {
            var firstRenderedAt = firstContentRendered.Task.IsCompletedSuccessfully
                ? firstContentRendered.Task.Result
                : interactiveAt;
            await PerformanceBenchmarkRunner.RunAsync(
                window,
                benchmark,
                new StartupPerformanceTimestamps(processStartedAt, windowShownAt, firstRenderedAt, libraryReadyAt, interactiveAt),
                _host.Services.GetRequiredService<IAudioEngine>(),
                _host.Services.GetRequiredService<ISettingsService>());
        }
        catch (Exception exception)
        {
            var directory = Path.GetDirectoryName(benchmark.OutputPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(benchmark.OutputPath, JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                error = exception.GetBaseException().Message,
                exception = exception.ToString()
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            await window.CloseAfterBenchmarkAsync();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _host.Services.GetRequiredService<DeveloperDiagnostics>().CompleteAsync().GetAwaiter().GetResult();
            _host.StopAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
        }
        finally
        {
            _host.Dispose();
            base.OnExit(e);
        }
    }
}

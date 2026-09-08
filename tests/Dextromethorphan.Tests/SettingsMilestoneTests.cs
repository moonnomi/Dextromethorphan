using System.Reflection;
using Dextromethorphan.App.ViewModels;
using Dextromethorphan.Core.Models;
using Dextromethorphan.Infrastructure.Settings;
using Dextromethorphan.Infrastructure.Storage;

namespace Dextromethorphan.Tests;

public sealed class SettingsMilestoneTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Dextromethorphan.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void MilestoneSixDefaultsAreStable()
    {
        var settings = new AppSettings();

        Assert.Equal(10, AppSettings.CurrentSchemaVersion);
        Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.Equal("Dark", settings.Theme);
        Assert.Equal("#8290FF", settings.AccentColor);
        Assert.Equal(1, settings.BackgroundOpacity);
        Assert.Equal(320, settings.QueuePanelWidth);
        Assert.False(settings.QueuePanelCompact);
        Assert.Equal(PanelDockSide.Right, settings.QueuePanelDockSide);
        Assert.True(settings.FullscreenHideNavigation);
        Assert.Equal(
            MetadataWriteMode.DatabaseOnly,
            settings.DefaultMetadataWriteMode);
        Assert.Contains(SettingsResetScope.Metadata, Enum.GetValues<SettingsResetScope>());
    }

    [Fact]
    public async Task CrossfadeAndGlowOptionsSurviveReloadAndReset()
    {
        var service = new JsonSettingsService(new AppPaths(_root));
        await service.InitializeAsync(TestContext.Current.CancellationToken);
        var shape = new CrossfadeShape { Curve = CrossfadeCurve.Custom, IncomingPower = 2.5, OutgoingPower = .5 };
        await service.UpdateAsync(x => { x.CrossfadeShape = shape; x.PlayerArtworkGlow = true; x.NowPlayingArtworkGlow = true; }, TestContext.Current.CancellationToken);
        var reloaded = new JsonSettingsService(new AppPaths(_root));
        await reloaded.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Equal(shape, reloaded.Current.CrossfadeShape);
        Assert.True(reloaded.Current.PlayerArtworkGlow);
        Assert.True(reloaded.Current.NowPlayingArtworkGlow);
        await reloaded.ResetAsync(SettingsResetScope.Playback, TestContext.Current.CancellationToken);
        Assert.Equal(new CrossfadeShape(), reloaded.Current.CrossfadeShape);
        await reloaded.ResetAsync(SettingsResetScope.Appearance, TestContext.Current.CancellationToken);
        Assert.False(reloaded.Current.PlayerArtworkGlow);
        Assert.False(reloaded.Current.NowPlayingArtworkGlow);
    }

    [Fact]
    public void NormalizeMigratesLegacyDefaultsAndRepairsNewFields()
    {
        var settings = new AppSettings
        {
            SchemaVersion = 8,
            AccentColor = "#FF8A3D",
            BackgroundOpacity = double.NaN,
            QueuePanelWidth = int.MaxValue,
            QueuePanelDockSide = (PanelDockSide)999,
            DefaultMetadataWriteMode = (MetadataWriteMode)999
        };

        JsonSettingsService.Normalize(settings);

        Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.Equal("#8290FF", settings.AccentColor);
        Assert.Equal(1, settings.BackgroundOpacity);
        Assert.Equal(520, settings.QueuePanelWidth);
        Assert.Equal(PanelDockSide.Right, settings.QueuePanelDockSide);
        Assert.Equal(
            MetadataWriteMode.DatabaseOnly,
            settings.DefaultMetadataWriteMode);
    }

    [Fact]
    public void NormalizeClampsNewFieldsAndCanonicalizesOpaqueAccent()
    {
        var settings = new AppSettings
        {
            AccentColor = "#80123456",
            BackgroundOpacity = 0.1,
            QueuePanelWidth = int.MinValue,
            DefaultMetadataWriteMode = MetadataWriteMode.WriteToFile,
            FullscreenHideNavigation = false
        };

        JsonSettingsService.Normalize(settings);

        Assert.Equal("#123456", settings.AccentColor);
        Assert.Equal(0.72, settings.BackgroundOpacity);
        Assert.Equal(280, settings.QueuePanelWidth);
        Assert.Equal(
            MetadataWriteMode.WriteToFile,
            settings.DefaultMetadataWriteMode);
        Assert.False(settings.FullscreenHideNavigation);
    }

    [Fact]
    public async Task NewFieldsRoundTripThroughJsonPersistence()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var paths = new AppPaths(_root);
        var service = new JsonSettingsService(paths);
        await service.InitializeAsync(cancellationToken);
        await service.UpdateAsync(
            settings =>
            {
                settings.Theme = "Amoled";
                settings.AccentColor = "#ABCDEF";
                settings.BackgroundOpacity = 0.84;
                settings.QueuePanelWidth = 444;
                settings.QueuePanelCompact = true;
                settings.QueuePanelDockSide = PanelDockSide.Left;
                settings.FullscreenHideNavigation = false;
                settings.DefaultMetadataWriteMode =
                    MetadataWriteMode.WriteToFile;
            },
            cancellationToken);

        var reloaded = new JsonSettingsService(paths);
        await reloaded.InitializeAsync(cancellationToken);

        Assert.Equal("Amoled", reloaded.Current.Theme);
        Assert.Equal("#ABCDEF", reloaded.Current.AccentColor);
        Assert.Equal(0.84, reloaded.Current.BackgroundOpacity);
        Assert.Equal(444, reloaded.Current.QueuePanelWidth);
        Assert.True(reloaded.Current.QueuePanelCompact);
        Assert.Equal(PanelDockSide.Left, reloaded.Current.QueuePanelDockSide);
        Assert.False(reloaded.Current.FullscreenHideNavigation);
        Assert.Equal(
            MetadataWriteMode.WriteToFile,
            reloaded.Current.DefaultMetadataWriteMode);
    }

    [Fact]
    public async Task AppearanceResetIncludesEveryNewAppearanceFieldOnly()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var service = new JsonSettingsService(new AppPaths(_root));
        await service.InitializeAsync(cancellationToken);
        await service.UpdateAsync(
            settings =>
            {
                settings.Theme = "Amoled";
                settings.AccentColor = "#123456";
                settings.FontFamily = "Arial";
                settings.FontSize = 20;
                settings.BackgroundOpacity = 0.8;
                settings.AnimationsEnabled = false;
                settings.VisualizerEnabled = true;
                settings.AlbumTileSize = 300;
                settings.DashboardModules = ["Lyrics"];
                settings.QueuePanelVisible = false;
                settings.QueuePanelWidth = 480;
                settings.QueuePanelCompact = true;
                settings.QueuePanelDockSide = PanelDockSide.Left;
                settings.FullscreenHideNavigation = false;
                settings.Volume = 0.33;
                settings.DefaultMetadataWriteMode =
                    MetadataWriteMode.WriteToFile;
            },
            cancellationToken);

        await service.ResetAsync(
            SettingsResetScope.Appearance,
            cancellationToken);

        var defaults = new AppSettings();
        Assert.Equal(defaults.Theme, service.Current.Theme);
        Assert.Equal(defaults.AccentColor, service.Current.AccentColor);
        Assert.Equal(defaults.FontFamily, service.Current.FontFamily);
        Assert.Equal(defaults.FontSize, service.Current.FontSize);
        Assert.Equal(
            defaults.BackgroundOpacity,
            service.Current.BackgroundOpacity);
        Assert.Equal(
            defaults.AnimationsEnabled,
            service.Current.AnimationsEnabled);
        Assert.Equal(
            defaults.VisualizerEnabled,
            service.Current.VisualizerEnabled);
        Assert.Equal(defaults.AlbumTileSize, service.Current.AlbumTileSize);
        Assert.Equal(defaults.DashboardModules, service.Current.DashboardModules);
        Assert.Equal(
            defaults.QueuePanelVisible,
            service.Current.QueuePanelVisible);
        Assert.Equal(defaults.QueuePanelWidth, service.Current.QueuePanelWidth);
        Assert.Equal(
            defaults.QueuePanelCompact,
            service.Current.QueuePanelCompact);
        Assert.Equal(
            defaults.QueuePanelDockSide,
            service.Current.QueuePanelDockSide);
        Assert.Equal(
            defaults.FullscreenHideNavigation,
            service.Current.FullscreenHideNavigation);

        Assert.Equal(0.33, service.Current.Volume);
        Assert.Equal(
            MetadataWriteMode.WriteToFile,
            service.Current.DefaultMetadataWriteMode);
    }

    [Fact]
    public async Task MetadataResetIsIndependentFromLibraryAndAppearance()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var libraryRoot = Path.Combine(_root, "music");
        var service = new JsonSettingsService(new AppPaths(_root));
        await service.InitializeAsync(cancellationToken);
        await service.UpdateAsync(
            settings =>
            {
                settings.Theme = "Amoled";
                settings.LibraryFolders = [libraryRoot];
                settings.MultiValueSeparators = ",";
                settings.DefaultMetadataWriteMode =
                    MetadataWriteMode.WriteToFile;
                settings.MetadataLookupEnabled = true;
                settings.MusicBrainzLookupEnabled = true;
                settings.DiscogsLookupEnabled = true;
                settings.DiscogsUserToken = "secret";
                settings.MetadataCacheDays = 365;
            },
            cancellationToken);

        await service.ResetAsync(
            SettingsResetScope.Metadata,
            cancellationToken);

        var defaults = new AppSettings();
        Assert.Equal(
            defaults.MultiValueSeparators,
            service.Current.MultiValueSeparators);
        Assert.Equal(
            defaults.DefaultMetadataWriteMode,
            service.Current.DefaultMetadataWriteMode);
        Assert.Equal(
            defaults.MetadataLookupEnabled,
            service.Current.MetadataLookupEnabled);
        Assert.Equal(
            defaults.MusicBrainzLookupEnabled,
            service.Current.MusicBrainzLookupEnabled);
        Assert.Equal(
            defaults.DiscogsLookupEnabled,
            service.Current.DiscogsLookupEnabled);
        Assert.Equal(defaults.DiscogsUserToken, service.Current.DiscogsUserToken);
        Assert.Equal(defaults.MetadataCacheDays, service.Current.MetadataCacheDays);

        Assert.Equal("Amoled", service.Current.Theme);
        Assert.Equal(
            Path.GetFullPath(libraryRoot),
            Assert.Single(service.Current.LibraryFolders));
    }

    [Fact]
    public async Task LibraryResetDoesNotResetMetadataPolicyOrProviders()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var service = new JsonSettingsService(new AppPaths(_root));
        await service.InitializeAsync(cancellationToken);
        await service.UpdateAsync(
            settings =>
            {
                settings.LibraryFolders = [Path.Combine(_root, "music")];
                settings.MultiValueSeparators = "|";
                settings.DefaultMetadataWriteMode =
                    MetadataWriteMode.WriteToFile;
                settings.MetadataLookupEnabled = true;
                settings.MusicBrainzLookupEnabled = true;
                settings.MetadataCacheDays = 90;
            },
            cancellationToken);

        await service.ResetAsync(
            SettingsResetScope.Library,
            cancellationToken);

        Assert.Empty(service.Current.LibraryFolders);
        Assert.Empty(service.Current.LibrarySources);
        Assert.Equal("|", service.Current.MultiValueSeparators);
        Assert.Equal(
            MetadataWriteMode.WriteToFile,
            service.Current.DefaultMetadataWriteMode);
        Assert.True(service.Current.MetadataLookupEnabled);
        Assert.True(service.Current.MusicBrainzLookupEnabled);
        Assert.Equal(90, service.Current.MetadataCacheDays);
    }

    [Fact]
    public void EveryAppSettingHasExactlyOneExplicitCoverageClassification()
    {
        var expectedInternal = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(AppSettings.SchemaVersion),
            nameof(AppSettings.LibraryFolders),
            nameof(AppSettings.SearchHistory),
            nameof(AppSettings.TrackPlaybackOverrides),
            nameof(AppSettings.LyricOffsetsMilliseconds),
            nameof(AppSettings.SelectedLyricFiles),
            nameof(AppSettings.SelectedOnlineLyrics),
            nameof(AppSettings.KeyBindings),
            nameof(AppSettings.PlaybackSession)
        };
        var expectedUserFacing = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(AppSettings.Theme),
            nameof(AppSettings.AccentColor),
            nameof(AppSettings.FontFamily),
            nameof(AppSettings.FontSize),
            nameof(AppSettings.BackgroundOpacity),
            nameof(AppSettings.AnimationsEnabled),
            nameof(AppSettings.VisualizerEnabled),
            nameof(AppSettings.AmbienceEnabled),
            nameof(AppSettings.PlayerArtworkGlow),
            nameof(AppSettings.NowPlayingArtworkGlow),
            nameof(AppSettings.CrossfadeShape),
            nameof(AppSettings.ResumeOnStartup),
            nameof(AppSettings.ResumeTrackBookmarks),
            nameof(AppSettings.StopAfterCurrent),
            nameof(AppSettings.StopAfterQueue),
            nameof(AppSettings.ReplayGainMode),
            nameof(AppSettings.ReplayGainPreampDb),
            nameof(AppSettings.PreventClipping),
            nameof(AppSettings.TransitionMode),
            nameof(AppSettings.CrossfadeSeconds),
            nameof(AppSettings.FadeInSeconds),
            nameof(AppSettings.FadeOutSeconds),
            nameof(AppSettings.PlaybackSpeed),
            nameof(AppSettings.PitchSemitones),
            nameof(AppSettings.PreservePitch),
            nameof(AppSettings.Volume),
            nameof(AppSettings.AlbumTileSize),
            nameof(AppSettings.ViewSettings),
            nameof(AppSettings.DashboardModules),
            nameof(AppSettings.ArtworkCacheMegabytes),
            nameof(AppSettings.QueuePanelVisible),
            nameof(AppSettings.QueuePanelWidth),
            nameof(AppSettings.QueuePanelCompact),
            nameof(AppSettings.QueuePanelDockSide),
            nameof(AppSettings.FullscreenHideNavigation),
            nameof(AppSettings.ExcludedFolders),
            nameof(AppSettings.LibrarySources),
            nameof(AppSettings.ScheduledLibraryScanEnabled),
            nameof(AppSettings.ScheduledLibraryScanIntervalMinutes),
            nameof(AppSettings.AllowScheduledScanOnBattery),
            nameof(AppSettings.AllowScheduledScanOnMeteredNetwork),
            nameof(AppSettings.MultiValueSeparators),
            nameof(AppSettings.DefaultMetadataWriteMode),
            nameof(AppSettings.MetadataLookupEnabled),
            nameof(AppSettings.MusicBrainzLookupEnabled),
            nameof(AppSettings.DiscogsLookupEnabled),
            nameof(AppSettings.DiscogsUserToken),
            nameof(AppSettings.MetadataCacheDays),
            nameof(AppSettings.LyricsDisplayMode),
            nameof(AppSettings.LyricsFontSize),
            nameof(AppSettings.LyricsAlignment),
            nameof(AppSettings.LyricsLineSpacing),
            nameof(AppSettings.LyricsBlurStrength),
            nameof(AppSettings.KaraokeWordAnimation),
            nameof(AppSettings.OnlineLyricsEnabled),
            nameof(AppSettings.OutputProfiles),
            nameof(AppSettings.ActiveOutputDeviceId),
            nameof(AppSettings.SeekStepSeconds),
            nameof(AppSettings.VolumeStep),
            nameof(AppSettings.Shortcuts)
        };

        AssertSetEqual(
            expectedInternal,
            SettingsOptionCoverage.InternalProperties,
            "internal settings inventory");
        AssertSetEqual(
            expectedUserFacing,
            SettingsOptionCoverage.UserFacingProperties,
            "user-facing settings inventory");

        var publicProperties = typeof(AppSettings)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        var classified = expectedInternal
            .Concat(expectedUserFacing)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Empty(expectedInternal.Intersect(expectedUserFacing));
        AssertSetEqual(publicProperties, classified, "complete settings inventory");
    }

    [Fact]
    public void ViewAndColumnInventoriesAreExact()
    {
        Assert.Equal(
            [
                "Albums", "Artists", "Genres", "Songs", "Folders",
                "Playlists", "Favorites", "Missing", "Recently Added",
                "Recently Played", "Most Played", "Never Played", "History",
                "Now Playing"
            ],
            SettingsOptionCoverage.ConfigurableViews);
        Assert.Equal(
            [
                "Track", "Title", "Artist", "Album", "Quality", "Rating",
                "Duration", "Year", "Codec", "Source"
            ],
            SettingsOptionCoverage.TrackColumns);
    }

    private static void AssertSetEqual(
        IEnumerable<string> expected,
        IEnumerable<string> actual,
        string inventory)
    {
        var expectedSet = expected.ToHashSet(StringComparer.Ordinal);
        var actualSet = actual.ToHashSet(StringComparer.Ordinal);
        var missing = expectedSet.Except(actualSet).Order().ToArray();
        var unexpected = actualSet.Except(expectedSet).Order().ToArray();

        Assert.True(
            expectedSet.SetEquals(actualSet),
            $"Mismatch in {inventory}. Missing: [{string.Join(", ", missing)}]. "
            + $"Unexpected: [{string.Join(", ", unexpected)}].");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}

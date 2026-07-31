using System.Security.Cryptography;
using Dextromethorphan.Core.Models;
using Dextromethorphan.Infrastructure.Audio;
using Dextromethorphan.Infrastructure.Library;
using Dextromethorphan.Infrastructure.Settings;
using Dextromethorphan.Infrastructure.Storage;

namespace Dextromethorphan.Tests;

public sealed class CueSheetTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Dextromethorphan.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SingleImageCueScansAsBoundedTracksWithoutChangingMedia()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var media = Path.Combine(_root, "music");
        Directory.CreateDirectory(media);
        var image = Path.Combine(media, "album image.wav");
        File.Copy(
            Path.Combine(
                AppContext.BaseDirectory,
                "Fixtures",
                "AudioFormats",
                "reference.wav"),
            image);
        var hashBefore = Hash(image);
        var cuePath = Path.Combine(media, "album.cue");
        await File.WriteAllTextAsync(
            cuePath,
            """
            REM GENRE "Electronic"
            REM DATE 2026
            PERFORMER "Fixture Artist"
            TITLE "Single Image Fixture"
            FILE "album image.wav" WAVE
              TRACK 01 AUDIO
                TITLE "Opening"
                INDEX 01 00:00:00
              TRACK 02 AUDIO
                TITLE "Finale"
                PERFORMER "Guest Artist"
                INDEX 00 00:00:48
                INDEX 01 00:00:50
            """,
            cancellationToken);

        var parsed = CueSheetParser.Parse(cuePath);
        Assert.Equal("Single Image Fixture", parsed.Title);
        Assert.Equal("Fixture Artist", parsed.Performer);
        Assert.Equal(2, parsed.Tracks.Count);
        Assert.Equal(TimeSpan.FromSeconds(50d / 75d), parsed.Tracks[1].Start);

        var paths = new AppPaths(Path.Combine(_root, "data"));
        var settings = new JsonSettingsService(paths);
        await settings.InitializeAsync(cancellationToken);
        var repository = new SqliteLibraryRepository(paths);
        await repository.InitializeAsync(cancellationToken);
        await using var scanner = new LibraryScanner(
            repository,
            new TagLibMetadataReader(),
            new ArtworkCache(paths, settings),
            paths);

        await scanner.ScanAsync(
            [media],
            cancellationToken: cancellationToken);

        var tracks = (await repository.GetAllAsync(cancellationToken))
            .Where(track => !track.IsMissing)
            .OrderBy(track => track.TrackNumber)
            .ToArray();
        Assert.Equal(2, tracks.Length);
        Assert.All(tracks, track =>
        {
            Assert.True(track.IsCueTrack);
            Assert.Equal(image, track.MediaPath);
            Assert.Equal(cuePath, track.CueSheetPath);
            Assert.NotEqual(image, track.Path);
            Assert.Equal("Single Image Fixture", track.Album);
        });
        Assert.Equal("Opening", tracks[0].Title);
        Assert.Equal("Fixture Artist", tracks[0].Artist);
        Assert.Equal("Finale", tracks[1].Title);
        Assert.Equal("Guest Artist", tracks[1].Artist);
        Assert.InRange(
            tracks[0].Duration,
            TimeSpan.FromSeconds(50d / 75d)
            - TimeSpan.FromMilliseconds(1),
            TimeSpan.FromSeconds(50d / 75d)
            + TimeSpan.FromMilliseconds(1));

        using var full = AudioDecoderFactory.Open(
            new Track
            {
                Path = image,
                Title = "Full image"
            });
        using var segment = AudioDecoderFactory.Open(tracks[1]);
        var expectedStart = (long)Math.Round(
            tracks[1].SegmentStart.TotalSeconds
            * full.Reader.WaveFormat.AverageBytesPerSecond);
        expectedStart -= expectedStart % full.Reader.WaveFormat.BlockAlign;
        full.Reader.Position = expectedStart;
        var expected = new byte[4096];
        var actual = new byte[4096];
        var expectedRead = full.Reader.Read(
            expected,
            0,
            expected.Length);
        var actualRead = segment.Reader.Read(
            actual,
            0,
            actual.Length);
        Assert.Equal(expectedRead, actualRead);
        Assert.Equal(
            expected.AsSpan(0, expectedRead).ToArray(),
            actual.AsSpan(0, actualRead).ToArray());
        Assert.InRange(
            segment.Reader.TotalTime,
            tracks[1].Duration - TimeSpan.FromMilliseconds(1),
            tracks[1].Duration + TimeSpan.FromMilliseconds(1));
        segment.Reader.CurrentTime = TimeSpan.FromMilliseconds(250);
        Assert.InRange(
            segment.Reader.CurrentTime,
            TimeSpan.FromMilliseconds(249),
            TimeSpan.FromMilliseconds(251));
        var drain = new byte[8192];
        while (segment.Reader.Read(drain, 0, drain.Length) > 0) { }
        Assert.Equal(0, segment.Reader.Read(drain, 0, drain.Length));

        Assert.Equal(hashBefore, Hash(image));
    }

    [Fact]
    public async Task CueReconciliationMarksRemovedVirtualTracksMissing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var paths = new AppPaths(Path.Combine(_root, "data"));
        var repository = new SqliteLibraryRepository(paths);
        await repository.InitializeAsync(cancellationToken);
        var cue = Path.Combine(_root, "album.cue");
        var media = Path.Combine(_root, "album.wav");
        var now = DateTimeOffset.UtcNow;
        var first = CueTrack(
            cue + ".dextromethorphan-cue-0001",
            cue,
            media,
            1,
            now);
        var second = CueTrack(
            cue + ".dextromethorphan-cue-0002",
            cue,
            media,
            2,
            now);

        await repository.ReconcileCueSheetAsync(
            cue,
            [first, second],
            cancellationToken);
        await repository.ReconcileCueSheetAsync(
            cue,
            [first],
            cancellationToken);

        var tracks = await repository.GetAllAsync(cancellationToken);
        Assert.False(Assert.Single(tracks, track => track.TrackNumber == 1).IsMissing);
        Assert.True(Assert.Single(tracks, track => track.TrackNumber == 2).IsMissing);
    }

    private static Track CueTrack(
        string path,
        string cue,
        string media,
        int number,
        DateTimeOffset modified) =>
        new()
        {
            Path = path,
            MediaPath = media,
            CueSheetPath = cue,
            SegmentStart = TimeSpan.FromSeconds(number - 1),
            SegmentEnd = TimeSpan.FromSeconds(number),
            Title = $"Track {number}",
            TrackNumber = number,
            Duration = TimeSpan.FromSeconds(1),
            FileModifiedAt = modified,
            FileSize = 1
        };

    private static string Hash(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}

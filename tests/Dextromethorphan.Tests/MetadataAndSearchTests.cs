using System.Security.Cryptography;
using Dextromethorphan.App.ViewModels;
using Dextromethorphan.Core.Library;
using Dextromethorphan.Core.Models;
using Dextromethorphan.Infrastructure.Library;

namespace Dextromethorphan.Tests;

public sealed class MetadataAndSearchTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "AudioFormats", name);

    [Fact]
    public async Task MetadataReaderCoversSupportedCorpus()
    {
        var reader = new TagLibMetadataReader();
        var paths = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures", "AudioFormats"))
            .Where(path => new[] { ".flac", ".mp3", ".m4a", ".wav", ".aiff", ".ogg", ".opus", ".aac", ".wma", ".dsf", ".dff" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Where(path => !Path.GetFileName(path).StartsWith("malformed", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(path).StartsWith("truncated", StringComparison.OrdinalIgnoreCase));
        var tracks = await Task.WhenAll(paths.Select(path => reader.ReadAsync(path)));
        Assert.NotEmpty(tracks);
        Assert.All(tracks, track => Assert.False(string.IsNullOrWhiteSpace(track.Title)));
        Assert.Contains(tracks, track => track.Path.Contains("unicode", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DatabaseOnlyEditDoesNotTouchFileAndWriteBackCanUndoExactly()
    {
        var source = Fixture("metadata-heavy.flac");
        var temp = Path.Combine(Path.GetTempPath(), "dextromethorphan-meta-" + Guid.NewGuid().ToString("N") + ".flac");
        File.Copy(source, temp);
        try
        {
            var reader = new TagLibMetadataReader();
            var service = new TagLibMetadataEditService(reader);
            var track = await reader.ReadAsync(temp);
            var originalHash = Hash(temp);
            var patch = new MetadataEditPatch { Fields = MetadataEditFields.Title | MetadataEditFields.Artists, Title = "Edited title", Artists = ["Editor", "Guest"] };
            var databaseOnly = await service.ApplyAsync(track, patch, MetadataWriteMode.DatabaseOnly);
            Assert.Equal(originalHash, Hash(temp));
            Assert.Equal("Edited title", databaseOnly.After.Title);
            Assert.Contains("Guest", databaseOnly.After.Artist);

            var written = await service.ApplyAsync(track, patch, MetadataWriteMode.WriteToFile);
            Assert.NotEqual(originalHash, Hash(temp));
            var reread = await reader.ReadAsync(temp);
            Assert.Equal("Edited title", reread.Title);
            await service.RestoreAsync(written);
            Assert.Equal(originalHash, Hash(temp));
            Assert.Equal(track.Title, (await reader.ReadAsync(temp)).Title);
        }
        finally { try { File.Delete(temp); } catch { } }
    }

    [Fact]
    public void StructuredSearchSupportsFieldsPhrasesExclusionAndDiacritics()
    {
        var tracks = new[]
        {
            new Track { Path = @"C:\Music\Beyoncé\Halo.flac", Title = "Halo", Artist = "Beyoncé", Album = "Live", Year = 2020, Rating = 5, IsLoved = true, Codec = "FLAC" },
            new Track { Path = @"C:\Music\Adele\Hello.mp3", Title = "Hello", Artist = "Adele", Album = "25", Year = 2015, Rating = 4, Codec = "MP3" }
        };
        var query = LibrarySearchQuery.Parse("artist:beyonce year:>=2020 -title:hello");
        Assert.Single(tracks.Where(query.Matches));
        Assert.Equal("Beyoncé", tracks.Single(query.Matches).Artist);
        Assert.Contains("Halo", LibrarySearchQuery.Parse("\"Halo\"").FreeTerms);
    }

    [Fact]
    public void StructuredSearchRetainsCjkRtlPunctuationAndScalesToLargeResults()
    {
        var tracks = Enumerable.Range(0, 20_000).Select(index => new Track
        {
            Id = index + 1,
            Path = $@"C:\Music\{index}\音楽 — שלום!.flac",
            Title = index == 19_999 ? "音楽 — שלום!" : $"Track {index}",
            Artist = "アーティスト; فنان",
            Album = "Collection",
            Codec = "FLAC"
        }).ToArray();
        var query = LibrarySearchQuery.Parse("title:\"音楽 — שלום!\" artist:アーティスト codec:flac");
        var result = tracks.Where(query.Matches).ToArray();
        Assert.Single(result);
        Assert.Equal(20_000, tracks.Length);
    }

    [Fact]
    public void MultiValueSeparatorsAreConfigurableWithoutSplittingNames()
    {
        var index = new LibraryGroupingIndex();
        index.ConfigureSeparators("|");
        var track = new Track { Path = @"C:\Music\song.flac", Title = "Song", Artist = "AC/DC|The Beatles", AlbumArtist = "AC/DC", Album = "Album", Genre = "Rock|Pop" };
        var snapshot = index.Reset([track]);
        Assert.Contains(snapshot.Artists, card => card.Title == "AC/DC");
        Assert.Contains(snapshot.Artists, card => card.Title == "The Beatles");
        Assert.DoesNotContain(snapshot.Artists, card => card.Title == "AC");
    }

    [Fact]
    public void SmartPlaylistEvaluatorSupportsNestedGroupsAndRecentDates()
    {
        var now = DateTimeOffset.UtcNow;
        var track = new Track
        {
            Path = @"C:\Music\track.flac",
            Title = "Night Drive",
            Artist = "Various Artists",
            Album = "Roads",
            Genre = "Electronic",
            Year = 2024,
            Rating = 5,
            IsLoved = true,
            LastPlayedAt = now.AddDays(-2),
            AddedAt = now.AddDays(-10),
            Duration = TimeSpan.FromMinutes(4),
            Codec = "FLAC"
        };
        var definition = new SmartPlaylistDefinition
        {
            Root = new SmartRuleGroup
            {
                Match = SmartRuleMatch.All,
                Conditions = [new SmartRuleCondition { Field = SmartField.Loved, Operator = SmartOperator.IsTrue }],
                Groups = [new SmartRuleGroup
                {
                    Match = SmartRuleMatch.Any,
                    Conditions =
                    [
                        new SmartRuleCondition { Field = SmartField.Genre, Operator = SmartOperator.Equals, Value = "Jazz" },
                        new SmartRuleCondition { Field = SmartField.LastPlayed, Operator = SmartOperator.InLastDays, Value = "7" }
                    ]
                }]
            }
        };
        Assert.True(SmartPlaylistEvaluator.Matches(track, definition, now));
        Assert.False(SmartPlaylistEvaluator.Matches(track with { IsLoved = false }, definition, now));
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}

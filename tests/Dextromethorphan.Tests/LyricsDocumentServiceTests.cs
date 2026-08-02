using System.Text;
using Dextromethorphan.App.Lyrics;
using Dextromethorphan.Core.Models;
using Dextromethorphan.Infrastructure.Settings;
using Dextromethorphan.Infrastructure.Storage;

namespace Dextromethorphan.Tests;

public sealed class LyricsDocumentServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Dextromethorphan.Lyrics", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ExactSidecarWinsBeforeVariantsAndEmbeddedLyrics()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(_root);
        var media = Path.Combine(_root, "song.flac");
        await File.WriteAllBytesAsync(media, [], cancellationToken);
        await File.WriteAllTextAsync(Path.ChangeExtension(media, ".lrc"), "[00:01]exact", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_root, "song.ja.lrc"), "[00:01]variant", cancellationToken);
        var service = await CreateServiceAsync(cancellationToken);

        var documents = await service.DiscoverAsync(TrackAt(media) with { Lyrics = "embedded" }, cancellationToken);

        Assert.Equal("[00:01]exact", documents[0].Content);
        Assert.Contains(documents, document => document.Content == "[00:01]variant");
        Assert.Contains(documents, document => document.Kind == LyricsSourceKind.Embedded);
    }

    [Fact]
    public async Task SelectedAlternatePersistsAndMovesToFirstPriority()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(_root);
        var media = Path.Combine(_root, "song.flac");
        await File.WriteAllBytesAsync(media, [], cancellationToken);
        var alternate = Path.Combine(_root, "alternate.lrc");
        await File.WriteAllTextAsync(alternate, "alternate", cancellationToken);
        var service = await CreateServiceAsync(cancellationToken);
        await service.ChooseAsync(TrackAt(media), alternate, cancellationToken);

        var documents = await service.DiscoverAsync(TrackAt(media), cancellationToken);

        Assert.Equal(Path.GetFullPath(alternate), documents[0].FilePath);
    }

    [Fact]
    public async Task SaveAndRemoveOnlyOperateOnSidecar()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(_root);
        var media = Path.Combine(_root, "song.flac");
        await File.WriteAllBytesAsync(media, [1, 2, 3], cancellationToken);
        var service = await CreateServiceAsync(cancellationToken);
        var track = TrackAt(media);

        var document = await service.SaveAsync(track, "[00:01]hello", null, cancellationToken);
        Assert.True(File.Exists(document.FilePath));
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(media, cancellationToken));

        await service.RemoveAsync(track, document, cancellationToken);
        Assert.False(File.Exists(document.FilePath));
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(media, cancellationToken));
    }

    [Fact]
    public void DecoderHandlesUtf8AndUtf16Bom()
    {
        const string latin = "h\u00E9llo";
        const string cjk = "\u4E16\u754C";
        Assert.Equal(latin, LyricTextDecoder.Decode(new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(latin)).ToArray()));
        Assert.Equal(cjk, LyricTextDecoder.Decode(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(cjk)).ToArray()));
    }

    private async Task<LyricsDocumentService> CreateServiceAsync(CancellationToken cancellationToken)
    {
        var settings = new JsonSettingsService(new AppPaths(Path.Combine(_root, "state")));
        await settings.InitializeAsync(cancellationToken);
        return new LyricsDocumentService(settings);
    }

    private static Track TrackAt(string path) => new()
    {
        Path = path,
        Title = "song",
        Artist = "artist",
        Album = "album"
    };

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}

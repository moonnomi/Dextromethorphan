using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Dextromethorphan.App.Lyrics;
using Dextromethorphan.Core.Models;
using Dextromethorphan.Infrastructure.Settings;
using Dextromethorphan.Infrastructure.Storage;

namespace Dextromethorphan.Tests;

public sealed class LrclibLyricsProviderTests : IDisposable
{
    private const string ResultJson = """
        {
          "id": 3396226,
          "trackName": "A Song",
          "artistName": "An Artist",
          "albumName": "An Album",
          "duration": 123,
          "instrumental": false,
          "plainLyrics": "hello",
          "syncedLyrics": "[00:01.00]hello"
        }
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "Dextromethorphan.Lrclib", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task DisabledProviderNeverSendsTrackMetadata()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (provider, handler, _) = await CreateProviderAsync(false, cancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SearchAsync(TrackAt(), cancellationToken));

        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task SearchIdentifiesClientAndUsesThirtyDayCache()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (provider, handler, _) = await CreateProviderAsync(true, cancellationToken);

        var first = await provider.SearchAsync(TrackAt(), cancellationToken);
        var second = await provider.SearchAsync(TrackAt(), cancellationToken);

        Assert.Single(first);
        Assert.Equal(first, second);
        Assert.Equal(1, handler.RequestCount);
        Assert.Contains("Dextromethorphan/", handler.LastUserAgent);
        Assert.Equal(LyricsSourceKind.OnlineCache, first[0].Kind);
        Assert.Equal("LRCLIB · lrclib.net", first[0].Attribution);
        Assert.Equal(3396226, first[0].SourceId);
    }

    [Fact]
    public async Task ConfirmedSelectionCanBeRestoredFromLocalRecordCache()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (provider, handler, settings) = await CreateProviderAsync(true, cancellationToken);
        var track = TrackAt();
        var document = Assert.Single(await provider.SearchAsync(track, cancellationToken));
        await provider.RememberSelectionAsync(track, document, cancellationToken);
        var offlineProvider = new LrclibLyricsProvider(
            new HttpClient(new RecordingHandler(_ => throw new InvalidOperationException("Network must not be used."))),
            new AppPaths(_root),
            settings,
            TimeSpan.Zero);

        var restored = await offlineProvider.LoadSelectedAsync(track, cancellationToken);

        Assert.NotNull(restored);
        Assert.Equal(document.Content, restored.Content);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task RateLimitResponseIsRetriedAfterRetryAfterDelay()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var responses = new Queue<HttpResponseMessage>(
        [
            new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero) }
            },
            JsonResponse(HttpStatusCode.OK, ResultJson)
        ]);
        var (provider, handler, _) = await CreateProviderAsync(
            true,
            cancellationToken,
            _ => responses.Dequeue());

        var result = await provider.SearchAsync(TrackAt(), cancellationToken);

        Assert.Single(result);
        Assert.Equal(2, handler.RequestCount);
    }

    private async Task<(LrclibLyricsProvider Provider, RecordingHandler Handler, JsonSettingsService Settings)> CreateProviderAsync(
        bool enabled,
        CancellationToken cancellationToken,
        Func<HttpRequestMessage, HttpResponseMessage>? response = null)
    {
        var paths = new AppPaths(_root);
        var settings = new JsonSettingsService(paths);
        await settings.InitializeAsync(cancellationToken);
        await settings.UpdateAsync(value => value.OnlineLyricsEnabled = enabled, cancellationToken);
        var handler = new RecordingHandler(response ?? (_ => JsonResponse(HttpStatusCode.OK, ResultJson)));
        var provider = new LrclibLyricsProvider(new HttpClient(handler), paths, settings, TimeSpan.Zero);
        return (provider, handler, settings);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string content) => new(status)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private static Track TrackAt() => new()
    {
        Path = Path.Combine(Path.GetTempPath(), "song.flac"),
        Title = "A Song",
        Artist = "An Artist",
        Album = "An Album",
        Duration = TimeSpan.FromSeconds(123)
    };

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public string LastUserAgent { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            LastUserAgent = string.Join(" ", request.Headers.GetValues("User-Agent"));
            return Task.FromResult(response(request));
        }
    }
}

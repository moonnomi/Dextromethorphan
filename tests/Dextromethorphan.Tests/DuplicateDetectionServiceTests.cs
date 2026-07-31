using Dextromethorphan.Core.Models;
using Dextromethorphan.Infrastructure.Library;
using Dextromethorphan.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace Dextromethorphan.Tests;

public sealed class DuplicateDetectionServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Dextromethorphan.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ReportsOnlyContentIdenticalSameSizeFiles()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(_root);
        var first = Path.Combine(_root, "one.flac");
        var second = Path.Combine(_root, "two.flac");
        var different = Path.Combine(_root, "different.flac");
        await File.WriteAllBytesAsync(
            first,
            [1, 2, 3, 4, 5],
            cancellationToken);
        await File.WriteAllBytesAsync(
            second,
            [1, 2, 3, 4, 5],
            cancellationToken);
        await File.WriteAllBytesAsync(
            different,
            [5, 4, 3, 2, 1],
            cancellationToken);
        var repository = new SqliteLibraryRepository(
            new AppPaths(_root));
        await repository.InitializeAsync(cancellationToken);
        await repository.UpsertBatchAsync(
        [
            Track(first),
            Track(second),
            Track(different)
        ], cancellationToken);

        var groups = await new DuplicateDetectionService(repository)
            .FindContentDuplicatesAsync(cancellationToken);

        var group = Assert.Single(groups);
        Assert.Equal(2, group.Tracks.Count);
        Assert.Equal(5, group.ReclaimableBytes);
        Assert.DoesNotContain(
            group.Tracks,
            track => track.Path == different);
    }

    private static Track Track(string path) => new()
    {
        Path = path,
        Title = Path.GetFileNameWithoutExtension(path),
        FileModifiedAt = File.GetLastWriteTimeUtc(path),
        FileSize = new FileInfo(path).Length
    };

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}

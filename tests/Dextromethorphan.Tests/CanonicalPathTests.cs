using System.Text;
using Dextromethorphan.Core.Models;
using Dextromethorphan.Infrastructure.Library;
using Dextromethorphan.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace Dextromethorphan.Tests;

public sealed class CanonicalPathTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Dextromethorphan.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void CanonicalPathNormalizesUnicodeSeparatorsAndCase()
    {
        var decomposed = "Cafe\u0301";
        var composed = decomposed.Normalize(NormalizationForm.FormC);
        var left = Path.Combine(_root, decomposed, ".", "song.flac");
        var right = Path.Combine(
            _root.ToUpperInvariant(),
            composed,
            "song.flac");

        Assert.True(CanonicalPath.Equals(left, right));
        Assert.True(
            CanonicalPath.Normalize(left).IsNormalized(
                NormalizationForm.FormC));
    }

    [Fact]
    public async Task RepositoryCollapsesCaseInsensitivePathDuplicates()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = new SqliteLibraryRepository(
            new AppPaths(_root));
        await repository.InitializeAsync(cancellationToken);
        var path = Path.Combine(_root, "Music", "Song.flac");

        await repository.UpsertAsync(
            Track(path, "First"),
            cancellationToken);
        await repository.UpsertAsync(
            Track(path.ToUpperInvariant(), "Updated"),
            cancellationToken);

        var persisted = Assert.Single(
            await repository.GetAllAsync(cancellationToken));
        Assert.Equal("Updated", persisted.Title);
    }

    [Fact]
    public void CanonicalPathAcceptsLongWindowsPaths()
    {
        var path = Enumerable.Range(0, 32)
            .Aggregate(
                _root,
                (current, index) => Path.Combine(
                    current,
                    $"directory-{index:D2}"));
        path = Path.Combine(path, "song.flac");

        var normalized = CanonicalPath.Normalize(path);

        Assert.True(normalized.Length > 260);
        Assert.EndsWith(
            "song.flac",
            normalized,
            StringComparison.OrdinalIgnoreCase);
    }

    private static Track Track(string path, string title) => new()
    {
        Path = path,
        Title = title,
        FileModifiedAt = DateTimeOffset.UtcNow,
        FileSize = 1
    };

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}

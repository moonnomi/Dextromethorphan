using System.Security.Cryptography;
using Dextromethorphan.Core.Abstractions;
using Dextromethorphan.Core.Models;

namespace Dextromethorphan.Infrastructure.Library;

public sealed class DuplicateDetectionService(
    ILibraryRepository repository)
{
    public async Task<IReadOnlyList<DuplicateTrackGroup>>
        FindContentDuplicatesAsync(
            CancellationToken cancellationToken = default)
    {
        var candidates = (await repository.GetAllAsync(
                cancellationToken))
            .Where(track => !track.IsMissing
                            && track.FileSize > 0)
            .GroupBy(track => track.FileSize)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group)
            .ToArray();
        var fingerprints =
            new Dictionary<long, List<(Track Track, string Hash)>>();
        await Parallel.ForEachAsync(
            candidates,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = 2,
                CancellationToken = cancellationToken
            },
            async (track, token) =>
            {
                string hash;
                try
                {
                    hash = await HashFileAsync(track.Path, token);
                }
                catch (Exception exception) when (
                    exception is IOException
                        or UnauthorizedAccessException)
                {
                    return;
                }
                lock (fingerprints)
                {
                    if (!fingerprints.TryGetValue(
                            track.FileSize,
                            out var entries))
                    {
                        entries = [];
                        fingerprints.Add(track.FileSize, entries);
                    }
                    entries.Add((track, hash));
                }
            });

        return fingerprints
            .SelectMany(pair => pair.Value
                .GroupBy(entry => entry.Hash, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => new DuplicateTrackGroup(
                    pair.Key,
                    group.Key,
                    group.Select(entry => entry.Track)
                        .OrderBy(track => track.Path, StringComparer.OrdinalIgnoreCase)
                        .ToArray())))
            .OrderByDescending(group => group.ReclaimableBytes)
            .ToArray();
    }

    private static async Task<string> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            128 * 1024,
            FileOptions.Asynchronous
            | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(
                buffer,
                cancellationToken);
            if (read == 0) break;
            hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset())
            .ToLowerInvariant();
    }
}

public sealed record DuplicateTrackGroup(
    long FileSize,
    string ContentSha256,
    IReadOnlyList<Track> Tracks)
{
    public long ReclaimableBytes =>
        Math.Max(0, Tracks.Count - 1) * FileSize;
}

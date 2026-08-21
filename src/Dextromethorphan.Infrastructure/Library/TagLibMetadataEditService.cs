using Dextromethorphan.Core.Abstractions;
using Dextromethorphan.Core.Models;

namespace Dextromethorphan.Infrastructure.Library;

public sealed class TagLibMetadataEditService(ITrackMetadataReader metadataReader) : IMetadataEditService
{
    private const int MaximumTextLength = 4_096;
    private const int MaximumArtworkBytes = 20 * 1024 * 1024;

    public async Task<MetadataEditResult> ApplyAsync(
        Track track,
        MetadataEditPatch patch,
        MetadataWriteMode mode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(patch);
        Validate(patch);
        var updated = ApplyToTrack(track, patch);
        if (mode == MetadataWriteMode.DatabaseOnly)
            return new MetadataEditResult(track, updated, mode, null);

        var path = track.EffectiveMediaPath;
        if (track.IsMissing || !File.Exists(path))
            throw new FileNotFoundException("The audio file is not available for tag writing.", path);
        var original = await File.ReadAllBytesAsync(path, cancellationToken);
        var temporary = path + ".dextromethorphan-edit-" + Guid.NewGuid().ToString("N") + Path.GetExtension(path);
        try
        {
            await File.WriteAllBytesAsync(temporary, original, cancellationToken);
            using (var file = TagLib.File.Create(temporary))
            {
                ApplyToTag(file, patch);
                file.Save();
            }
            ReplaceAtomically(temporary, path);
            var reread = await metadataReader.ReadAsync(path, cancellationToken);
            return new MetadataEditResult(track, reread, mode, original);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    public async Task RestoreAsync(MetadataEditResult result, CancellationToken cancellationToken = default)
    {
        if (result.Mode != MetadataWriteMode.WriteToFile || result.OriginalFileBytes is null)
            return;
        var path = result.Before.EffectiveMediaPath;
        var temporary = path + ".dextromethorphan-undo-" + Guid.NewGuid().ToString("N") + Path.GetExtension(path);
        try
        {
            await File.WriteAllBytesAsync(temporary, result.OriginalFileBytes, cancellationToken);
            ReplaceAtomically(temporary, path);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private static Track ApplyToTrack(Track track, MetadataEditPatch patch) => track with
    {
        Title = patch.Fields.HasFlag(MetadataEditFields.Title) ? CleanText(patch.Title, track.Title, required: true) : track.Title,
        Artist = patch.Fields.HasFlag(MetadataEditFields.Artists) ? Join(patch.Artists, track.Artist) : track.Artist,
        ArtistSort = patch.Fields.HasFlag(MetadataEditFields.ArtistSort) ? CleanText(patch.ArtistSort, track.ArtistSort, required: false) : track.ArtistSort,
        AlbumArtist = patch.Fields.HasFlag(MetadataEditFields.AlbumArtists) ? Join(patch.AlbumArtists, track.AlbumArtist) : track.AlbumArtist,
        AlbumArtistSort = patch.Fields.HasFlag(MetadataEditFields.AlbumArtistSort) ? CleanText(patch.AlbumArtistSort, track.AlbumArtistSort, required: false) : track.AlbumArtistSort,
        Album = patch.Fields.HasFlag(MetadataEditFields.Album) ? CleanText(patch.Album, track.Album, required: false) : track.Album,
        AlbumSort = patch.Fields.HasFlag(MetadataEditFields.AlbumSort) ? CleanText(patch.AlbumSort, track.AlbumSort, required: false) : track.AlbumSort,
        Genre = patch.Fields.HasFlag(MetadataEditFields.Genres) ? Join(patch.Genres, track.Genre) : track.Genre,
        Comment = patch.Fields.HasFlag(MetadataEditFields.Comment) ? CleanText(patch.Comment, track.Comment, required: false) : track.Comment,
        Grouping = patch.Fields.HasFlag(MetadataEditFields.Grouping) ? CleanText(patch.Grouping, track.Grouping, required: false) : track.Grouping,
        Composer = patch.Fields.HasFlag(MetadataEditFields.Composer) ? Join(patch.Composers, track.Composer) : track.Composer,
        Conductor = patch.Fields.HasFlag(MetadataEditFields.Conductor) ? CleanText(patch.Conductor, track.Conductor, required: false) : track.Conductor,
        ReleaseType = patch.Fields.HasFlag(MetadataEditFields.ReleaseType) ? CleanText(patch.ReleaseType, track.ReleaseType, required: false) : track.ReleaseType,
        IsCompilation = patch.Fields.HasFlag(MetadataEditFields.Compilation) ? patch.IsCompilation ?? false : track.IsCompilation,
        Year = patch.Fields.HasFlag(MetadataEditFields.Year) ? Math.Clamp(patch.Year ?? 0, 0, 9999) : track.Year,
        TrackNumber = patch.Fields.HasFlag(MetadataEditFields.TrackNumber) ? Math.Clamp(patch.TrackNumber ?? 0, 0, 9999) : track.TrackNumber,
        DiscNumber = patch.Fields.HasFlag(MetadataEditFields.DiscNumber) ? Math.Clamp(patch.DiscNumber ?? 0, 0, 9999) : track.DiscNumber,
        Artwork = patch.Fields.HasFlag(MetadataEditFields.Artwork)
            ? patch.RemoveArtwork ? null : patch.Artwork
            : track.Artwork
    };

    private static void ApplyToTag(TagLib.File file, MetadataEditPatch patch)
    {
        var tag = file.Tag;
        if (patch.Fields.HasFlag(MetadataEditFields.Title)) tag.Title = CleanText(patch.Title, tag.Title, required: true);
        if (patch.Fields.HasFlag(MetadataEditFields.Artists)) tag.Performers = Values(patch.Artists);
        if (patch.Fields.HasFlag(MetadataEditFields.ArtistSort)) tag.PerformersSort = Values(patch.ArtistSort is null ? null : [patch.ArtistSort]);
        if (patch.Fields.HasFlag(MetadataEditFields.AlbumArtists)) tag.AlbumArtists = Values(patch.AlbumArtists);
        if (patch.Fields.HasFlag(MetadataEditFields.AlbumArtistSort)) tag.AlbumArtistsSort = Values(patch.AlbumArtistSort is null ? null : [patch.AlbumArtistSort]);
        if (patch.Fields.HasFlag(MetadataEditFields.Album)) tag.Album = CleanText(patch.Album, tag.Album, required: false);
        if (patch.Fields.HasFlag(MetadataEditFields.AlbumSort)) tag.AlbumSort = CleanText(patch.AlbumSort, tag.AlbumSort, required: false);
        if (patch.Fields.HasFlag(MetadataEditFields.Genres)) tag.Genres = Values(patch.Genres);
        if (patch.Fields.HasFlag(MetadataEditFields.Comment)) tag.Comment = CleanText(patch.Comment, tag.Comment, required: false);
        if (patch.Fields.HasFlag(MetadataEditFields.Grouping)) tag.Grouping = CleanText(patch.Grouping, tag.Grouping, required: false);
        if (patch.Fields.HasFlag(MetadataEditFields.Composer)) tag.Composers = Values(patch.Composers);
        if (patch.Fields.HasFlag(MetadataEditFields.Conductor)) tag.Conductor = CleanText(patch.Conductor, tag.Conductor, required: false);
        if (patch.Fields.HasFlag(MetadataEditFields.Compilation)) SetCompilation(tag, patch.IsCompilation ?? false);
        if (patch.Fields.HasFlag(MetadataEditFields.Year)) tag.Year = (uint)Math.Clamp(patch.Year ?? 0, 0, 9999);
        if (patch.Fields.HasFlag(MetadataEditFields.TrackNumber)) tag.Track = (uint)Math.Clamp(patch.TrackNumber ?? 0, 0, 9999);
        if (patch.Fields.HasFlag(MetadataEditFields.DiscNumber)) tag.Disc = (uint)Math.Clamp(patch.DiscNumber ?? 0, 0, 9999);
        if (patch.Fields.HasFlag(MetadataEditFields.Artwork))
            tag.Pictures = patch.RemoveArtwork || patch.Artwork is null
                ? []
                : [new TagLib.Picture(new TagLib.ByteVector(patch.Artwork))];
        if (patch.Fields.HasFlag(MetadataEditFields.ReleaseType))
        {
            var releaseType = CleanText(patch.ReleaseType, string.Empty, required: false);
            if (file.GetTag(TagLib.TagTypes.Xiph, false) is TagLib.Ogg.XiphComment xiph)
            {
                if (releaseType.Length == 0) xiph.RemoveField("RELEASETYPE");
                else xiph.SetField("RELEASETYPE", [releaseType]);
            }
            if (file.GetTag(TagLib.TagTypes.Id3v2, false) is TagLib.Id3v2.Tag id3)
            {
                if (releaseType.Length == 0) foreach (var frame in id3.GetFrames<TagLib.Id3v2.UserTextInformationFrame>().Where(frame => frame.Description.Equals("RELEASETYPE", StringComparison.OrdinalIgnoreCase)).ToArray()) id3.RemoveFrame(frame);
                else id3.SetUserTextAsString("RELEASETYPE", releaseType);
            }
        }
    }

    private static void Validate(MetadataEditPatch patch)
    {
        foreach (var value in new[] { patch.Title, patch.Album, patch.Comment })
            if (value is not null && value.Length > MaximumTextLength)
                throw new ArgumentException("Metadata text is too long.");
        ValidateValues(patch.Artists);
        ValidateValues(patch.AlbumArtists);
        ValidateValues(patch.Genres);
        ValidateValues(patch.Composers);
        foreach (var value in new[] { patch.ArtistSort, patch.AlbumArtistSort, patch.AlbumSort, patch.Grouping, patch.Conductor, patch.ReleaseType })
            if (value is not null && value.Length > MaximumTextLength)
                throw new ArgumentException("Metadata text is too long.");
        if (patch.Year is < 0 or > 9999 || patch.TrackNumber is < 0 or > 9999 || patch.DiscNumber is < 0 or > 9999)
            throw new ArgumentOutOfRangeException(nameof(patch), "Numeric metadata values must be between 0 and 9999.");
        if (patch.Artwork is { Length: > MaximumArtworkBytes })
            throw new InvalidDataException("Artwork exceeds the 20 MB safety limit.");
        if (patch.RemoveArtwork && !patch.Fields.HasFlag(MetadataEditFields.Artwork))
            throw new ArgumentException("RemoveArtwork requires the Artwork field.");
    }

    private static void SetCompilation(TagLib.Tag tag, bool value)
    {
        switch (tag)
        {
            case TagLib.Mpeg4.AppleTag apple:
                apple.IsCompilation = value;
                break;
            case TagLib.Id3v2.Tag id3:
                id3.IsCompilation = value;
                break;
        }
    }

    private static void ValidateValues(IReadOnlyList<string>? values)
    {
        if (values is null) return;
        if (values.Count > 64) throw new ArgumentException("A metadata field cannot contain more than 64 values.");
        if (values.Any(value => value.Length > MaximumTextLength)) throw new ArgumentException("Metadata text is too long.");
    }

    private static string CleanText(string? value, string fallback, bool required) {
        var clean = (value ?? (required ? fallback : string.Empty)).Trim();
        if (required && clean.Length == 0) throw new ArgumentException("Title cannot be empty.");
        return clean;
    }

    private static string Join(IReadOnlyList<string>? values, string fallback) =>
        values is null ? fallback : string.Join("; ", Values(values));

    private static string[] Values(IReadOnlyList<string>? values) =>
        (values ?? []).Select(value => value.Trim()).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static void ReplaceAtomically(string temporary, string destination)
    {
        try { File.Replace(temporary, destination, null, true); }
        catch (PlatformNotSupportedException) { File.Move(temporary, destination, true); }
        catch (IOException) { File.Move(temporary, destination, true); }
    }
}

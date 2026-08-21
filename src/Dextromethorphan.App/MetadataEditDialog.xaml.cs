using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Dextromethorphan.Core.Abstractions;
using Dextromethorphan.Core.Models;
using Microsoft.Win32;

namespace Dextromethorphan.App;

public sealed record MetadataEditRequest(MetadataEditPatch Patch, MetadataWriteMode Mode);

public partial class MetadataEditDialog : Window
{
    private readonly IReadOnlyList<Track> _tracks;
    private readonly IMetadataMatchService? _matcher;
    private byte[]? _artwork;

    private MetadataEditDialog(IReadOnlyList<Track> tracks, IMetadataMatchService? matcher)
    {
        _tracks = tracks; _matcher = matcher;
        InitializeComponent();
        var first = tracks[0];
        TrackSummary.Text = tracks.Count == 1
            ? first.Title + " · " + first.DisplayArtist
            : $"{tracks.Count:N0} tracks selected";
        TitleBox.Text = Common(tracks.Select(track => track.Title));
        ArtistsBox.Text = Common(tracks.Select(track => track.Artist));
        AlbumArtistBox.Text = Common(tracks.Select(track => track.AlbumArtist));
        AlbumBox.Text = Common(tracks.Select(track => track.Album));
        GenresBox.Text = Common(tracks.Select(track => track.Genre));
        YearBox.Text = Common(tracks.Select(track => track.Year == 0 ? string.Empty : track.Year.ToString()));
        TrackNumberBox.Text = Common(tracks.Select(track => track.TrackNumber == 0 ? string.Empty : track.TrackNumber.ToString()));
        DiscNumberBox.Text = Common(tracks.Select(track => track.DiscNumber == 0 ? string.Empty : track.DiscNumber.ToString()));
        CommentBox.Text = Common(tracks.Select(track => track.Comment));
        WriteModeBox.SelectedIndex = 0;
    }

    public static MetadataEditRequest? Show(Window owner, IReadOnlyList<Track> tracks, IMetadataMatchService? matcher = null)
    {
        if (tracks.Count == 0) return null;
        var dialog = new MetadataEditDialog(tracks, matcher) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.Request : null;
    }

    private MetadataEditRequest? Request { get; set; }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var fields = MetadataEditFields.None;
            var multiple = _tracks.Count > 1;
            var patch = new MetadataEditPatch
            {
                Fields = fields,
                Title = Field(TitleBox.Text, MetadataEditFields.Title, ref fields, multiple),
                Artists = Values(ArtistsBox.Text, MetadataEditFields.Artists, ref fields, multiple),
                AlbumArtists = Values(AlbumArtistBox.Text, MetadataEditFields.AlbumArtists, ref fields, multiple),
                Album = Field(AlbumBox.Text, MetadataEditFields.Album, ref fields, multiple),
                Genres = Values(GenresBox.Text, MetadataEditFields.Genres, ref fields, multiple),
                Comment = Field(CommentBox.Text, MetadataEditFields.Comment, ref fields, multiple),
                Year = Number(YearBox.Text, MetadataEditFields.Year, ref fields, multiple),
                TrackNumber = Number(TrackNumberBox.Text, MetadataEditFields.TrackNumber, ref fields, multiple),
                DiscNumber = Number(DiscNumberBox.Text, MetadataEditFields.DiscNumber, ref fields, multiple),
                Artwork = _artwork,
                RemoveArtwork = RemoveArtworkCheck.IsChecked == true
            } with { Fields = fields | (_artwork is not null || RemoveArtworkCheck.IsChecked == true ? MetadataEditFields.Artwork : MetadataEditFields.None) };
            if (fields == MetadataEditFields.None && !patch.Fields.HasFlag(MetadataEditFields.Artwork))
                throw new ArgumentException("Choose at least one field to apply.");
            if (patch.Fields.HasFlag(MetadataEditFields.Title) && string.IsNullOrWhiteSpace(patch.Title))
                throw new ArgumentException("Title cannot be empty.");
            Request = new MetadataEditRequest(patch, (WriteModeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() == nameof(MetadataWriteMode.WriteToFile) ? MetadataWriteMode.WriteToFile : MetadataWriteMode.DatabaseOnly);
            DialogResult = true;
        }
        catch (Exception exception) { MessageBox.Show(this, exception.Message, "Metadata", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void ChooseArtwork_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choose artwork", Filter = "Images|*.png;*.jpg;*.jpeg;*.webp;*.bmp|All files|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            _artwork = await File.ReadAllBytesAsync(dialog.FileName);
            if (SquareCropCheck.IsChecked == true) _artwork = CropSquare(_artwork);
            ArtworkStatus.Text = Path.GetFileName(dialog.FileName);
            RemoveArtworkCheck.IsChecked = false;
        }
        catch (Exception exception) { MessageBox.Show(this, exception.Message, "Artwork", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void ClearArtwork_Click(object sender, RoutedEventArgs e) { _artwork = null; ArtworkStatus.Text = "Keep current artwork"; RemoveArtworkCheck.IsChecked = false; }

    private void RemoveArtworkCheck_Click(object sender, RoutedEventArgs e) { }
    private async void Lookup_Click(object sender, RoutedEventArgs e)
    {
        if (_matcher is null) return;
        try
        {
            var results = await _matcher.SearchAsync(_tracks[0], MetadataMatchProvider.MusicBrainz);
            if (results.Count == 0) { MessageBox.Show(this, "No opt-in metadata matches were found. Enable MusicBrainz lookup in settings first.", "Metadata lookup", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            var match = results[0];
            if (MessageBox.Show(this, $"Use this {match.Attribution} match?\n\n{match.Title}\n{match.Artist}\n{match.Year?.ToString() ?? "Year unknown"}\n\nNothing is written until you press Apply.", "Confirm metadata", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            TitleBox.Text = match.Title; ArtistsBox.Text = match.Artist; AlbumBox.Text = match.Album; if (match.Year is { } year) YearBox.Text = year.ToString();
        }
        catch (Exception exception) { MessageBox.Show(this, exception.Message, "Metadata lookup", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.ChangedButton == MouseButton.Left) DragMove(); }

    private byte[] CropSquare(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        var source = new BitmapImage(); source.BeginInit(); source.CacheOption = BitmapCacheOption.OnLoad; source.StreamSource = input; source.EndInit(); source.Freeze();
        var size = Math.Min(source.PixelWidth, source.PixelHeight);
        var crop = new CroppedBitmap(source, new Int32Rect((source.PixelWidth - size) / 2, (source.PixelHeight - size) / 2, size, size)); crop.Freeze();
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(crop)); using var output = new MemoryStream(); encoder.Save(output); return output.ToArray();
    }

    private static string Common(IEnumerable<string> values)
    {
        var normalized = values.ToArray();
        return normalized.Distinct(StringComparer.Ordinal).Count() == 1 ? normalized[0] : string.Empty;
    }

    private static string? Field(string text, MetadataEditFields field, ref MetadataEditFields fields, bool multiple)
    {
        if (text.Length == 0 && multiple) return null;
        fields |= field; return text;
    }

    private static IReadOnlyList<string>? Values(string text, MetadataEditFields field, ref MetadataEditFields fields, bool multiple)
    {
        if (text.Length == 0 && multiple) return null;
        fields |= field;
        return text.Split([';', '/', '|', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToArray();
    }

    private static int? Number(string text, MetadataEditFields field, ref MetadataEditFields fields, bool multiple)
    {
        if (text.Length == 0 && multiple) return null;
        if (!int.TryParse(text, out var value)) throw new ArgumentException("Numeric fields must contain whole numbers.");
        fields |= field; return value;
    }
}

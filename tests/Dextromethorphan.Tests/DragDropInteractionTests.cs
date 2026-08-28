using System.Xml.Linq;
using Dextromethorphan.App.ViewModels;
using Dextromethorphan.Core.Models;

namespace Dextromethorphan.Tests;

public sealed class DragDropInteractionTests
{
    [Fact]
    public void AlbumCardsExposeCollectionAndQueueContextActions()
    {
        var document = XDocument.Load(Path.Combine(
            RepositoryRoot(),
            "src/Dextromethorphan.App/MainWindow.xaml"));
        var template = Assert.Single(
            document.Descendants(),
            element => Attribute(element, "Key") == "GroupCardTemplate");
        var headers = template
            .Descendants()
            .Where(element => element.Name.LocalName == "MenuItem")
            .Select(element => Attribute(element, "Header"))
            .ToArray();

        Assert.Contains("_Open collection", headers);
        Assert.Contains("_Play collection", headers);
        Assert.Contains("Play _next", headers);
        Assert.Contains("Add to _queue", headers);
        Assert.Contains(
            template.Descendants(),
            element => element.Name.LocalName == "Grid.ContextMenu");
    }

    [Fact]
    public void QueueDropMatchingPreservesPayloadOrderAndRemovesDuplicatePaths()
    {
        var first = new Track { Path = @"C:\Music\first.flac", Title = "First" };
        var duplicate = new Track { Path = @"c:\music\FIRST.flac", Title = "Duplicate record" };
        var second = new Track { Path = @"C:\Music\second.flac", Title = "Second" };
        var missing = new Track { Path = @"C:\Music\missing.flac", Title = "Missing", IsMissing = true };

        var result = MainViewModel.MatchUniqueQueueTracks(
            [first, duplicate, second, missing],
            [second.Path, first.Path, first.Path.ToUpperInvariant(), missing.Path]);

        Assert.Collection(
            result,
            track => Assert.Same(second, track),
            track => Assert.Same(first, track));
    }

    [Fact]
    public void SongDragsHaveGuardedCardPreviewLifecycle()
    {
        var document = XDocument.Load(Path.Combine(
            RepositoryRoot(),
            "src/Dextromethorphan.App/UI/Views/TrackListView.xaml"));
        var list = Assert.Single(
            document.Descendants(),
            element => Attribute(element, "Name") == "TrackList");

        Assert.Equal(
            "TrackList_PreviewMouseLeftButtonDown",
            Attribute(list, "PreviewMouseLeftButtonDown"));
        Assert.Equal(
            "TrackList_PreviewMouseLeftButtonUp",
            Attribute(list, "PreviewMouseLeftButtonUp"));

        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src/Dextromethorphan.App/UI/Views/TrackListView.xaml.cs"));
        Assert.Contains("_dragStarted", source);
        Assert.Contains("BeginTrackDragPreview", source);
        Assert.Contains("EndTrackDragPreview", source);

        var windowSource = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src/Dextromethorphan.App/MainWindow.xaml.cs"));
        Assert.Contains("AdornerLayer.GetAdornerLayer(ShellRoot)", windowSource);
        Assert.Contains("e.GetPosition(ShellRoot)", windowSource);

        var previewSource = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src/Dextromethorphan.App/UI/QueueDragAdorner.cs"));
        Assert.Contains("CreateMusicCard", previewSource);
        Assert.Contains("MusicCardWidth = 292", previewSource);
        Assert.Contains("MusicCardHeight = 68", previewSource);
    }

    private static string? Attribute(XElement element, string localName) =>
        element.Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName == localName)
            ?.Value;

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Dextromethorphan.slnx")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the Dextromethorphan repository root.");
    }
}

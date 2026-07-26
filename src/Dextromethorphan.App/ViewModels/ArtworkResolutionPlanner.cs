namespace Dextromethorphan.App.ViewModels;

internal static class ArtworkResolutionPlanner
{
    public static IReadOnlyList<LibraryCardViewModel> ForActiveView(
        string currentView,
        bool isCollectionDetailOpen,
        LibraryCardViewModel? selectedCard,
        IReadOnlyList<LibraryCardViewModel> galleryCards,
        IReadOnlyList<LibraryCardViewModel> sidebarCards)
    {
        if (isCollectionDetailOpen)
            return selectedCard is null ? [] : [selectedCard];

        return currentView switch
        {
            "Albums" or "Artists" or "Genres" => galleryCards,
            "Folders" or "Playlists" => sidebarCards,
            _ => []
        };
    }
}

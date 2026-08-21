using Dextromethorphan.App.ViewModels;

namespace Dextromethorphan.Tests;

public sealed class LibrarySourceScopeTests
{
    [Fact]
    public void EmptySourceListKeepsAnExistingIndexedLibraryVisible()
    {
        var path = Path.Combine(Path.GetTempPath(), "indexed", "song.flac");

        Assert.True(MainViewModel.IsWithinEnabledSources(path, []));
    }

    [Fact]
    public void EnabledSourcesUseDirectoryBoundaries()
    {
        var root = Path.Combine(Path.GetTempPath(), "Music");
        var included = Path.Combine(root, "Album", "song.flac");
        var sibling = root + "-backup";
        var excluded = Path.Combine(sibling, "song.flac");

        Assert.True(MainViewModel.IsWithinEnabledSources(included, [root]));
        Assert.False(MainViewModel.IsWithinEnabledSources(excluded, [root]));
    }
}

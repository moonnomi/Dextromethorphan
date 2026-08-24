using Dextromethorphan.App.ViewModels;
using Dextromethorphan.Core.Models;

namespace Dextromethorphan.Tests;

public sealed class SettingsWorkspaceComponentTests
{
    [Fact]
    public void ShortcutCatalogContainsEverySupportedActionExactlyOnce()
    {
        var expected = new[]
        {
            ShortcutActions.TogglePlayback, ShortcutActions.Play,
            ShortcutActions.Pause, ShortcutActions.Stop, ShortcutActions.Next,
            ShortcutActions.Previous, ShortcutActions.SeekForward,
            ShortcutActions.SeekBackward, ShortcutActions.VolumeUp,
            ShortcutActions.VolumeDown, ShortcutActions.RatingUp,
            ShortcutActions.RatingDown, ShortcutActions.Love,
            ShortcutActions.Search, ShortcutActions.UndoQueue
        };

        Assert.Equal(expected.Order(), ShortcutActionOption.All.Select(x => x.Action).Order());
        Assert.Equal(
            ShortcutActionOption.All.Count,
            ShortcutActionOption.All.Select(x => x.Action).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ShortcutEditorRoundTripsAllEditableFields()
    {
        var editor = new ShortcutBindingEditorViewModel(new ShortcutBinding
        {
            Action = ShortcutActions.Search,
            Gesture = "Ctrl+F",
            Global = true,
            Enabled = false
        });

        var result = editor.ToBinding();

        Assert.Equal(ShortcutActions.Search, result.Action);
        Assert.Equal("Ctrl+F", result.Gesture);
        Assert.True(result.Global);
        Assert.False(result.Enabled);
    }

    [Fact]
    public void ViewProfileRoundTripsOrderVisibilityAndWidths()
    {
        var editor = new ViewProfileEditorViewModel("Songs");
        editor.Load(new ViewSettings
        {
            SortBy = "Year",
            SortDescending = true,
            Density = LibraryDensity.Compact,
            CoverSize = 220,
            QuickFilter = "codec:flac",
            VisibleColumns = ["Title", "Year"],
            ColumnOrder = ["Year", "Title"],
            ColumnWidths = new Dictionary<string, double>
            {
                ["Year"] = 72,
                ["Title"] = 340
            }
        });

        var result = editor.ToSettings();

        Assert.Equal("Year", result.SortBy);
        Assert.True(result.SortDescending);
        Assert.Equal(LibraryDensity.Compact, result.Density);
        Assert.Equal(220, result.CoverSize);
        Assert.Equal("codec:flac", result.QuickFilter);
        Assert.Equal(["Year", "Title"], result.ColumnOrder.Take(2));
        Assert.Equal(["Year", "Title"], result.VisibleColumns);
        Assert.Equal(72, result.ColumnWidths["Year"]);
        Assert.Equal(340, result.ColumnWidths["Title"]);
        Assert.False(editor.IsDirty);
    }

    [Fact]
    public void ResetViewDefaultsRemainsAnUnappliedDraft()
    {
        var editor = new ViewProfileEditorViewModel("Albums");
        editor.Load(new ViewSettings
        {
            SortBy = "Year",
            CoverSize = 280,
            Density = LibraryDensity.Compact
        });

        editor.ResetToDefaultsDraft();

        Assert.True(editor.IsDirty);
        Assert.Equal("Title", editor.SortBy);
        Assert.Equal(172, editor.CoverSize);
        Assert.Equal(LibraryDensity.Comfortable, editor.Density);
    }
}

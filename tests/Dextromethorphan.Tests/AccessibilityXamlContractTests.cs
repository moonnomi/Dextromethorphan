using System.Xml.Linq;

namespace Dextromethorphan.Tests;

public sealed class AccessibilityXamlContractTests
{
    [Fact]
    public void SettingsValueEditorsHaveMeaningfulAutomationNames()
    {
        var document = Load("src/Dextromethorphan.App/SettingsWindow.xaml");
        var editors = document
            .Descendants()
            .Where(element => element.Name.LocalName is
                "Slider" or "ComboBox" or "TextBox" or "ListBox" or "TabControl")
            .ToArray();

        Assert.NotEmpty(editors);
        foreach (var editor in editors)
            AssertAttribute(editor, "AutomationProperties.Name");
    }

    [Fact]
    public void EverySliderPublishesValueAndFineCoarseSteps()
    {
        foreach (var path in new[]
                 {
                     "src/Dextromethorphan.App/MainWindow.xaml",
                     "src/Dextromethorphan.App/SettingsWindow.xaml"
                 })
        {
            var sliders = Load(path)
                .Descendants()
                .Where(element => element.Name.LocalName == "Slider")
                .ToArray();
            Assert.NotEmpty(sliders);
            foreach (var slider in sliders)
            {
                AssertAttribute(slider, "AutomationProperties.Name");
                AssertAttribute(slider, "AutomationProperties.ItemStatus");
                AssertPositiveNumber(slider, "SmallChange");
                AssertPositiveNumber(slider, "LargeChange");
            }
        }
    }

    [Fact]
    public void QueueAndTrackListsExposeKeyboardMultiSelectionAndContextMenus()
    {
        var main = Load("src/Dextromethorphan.App/MainWindow.xaml");
        var queue = NamedElement(main, "QueueList");
        Assert.Equal("Extended", Attribute(queue, "SelectionMode"));
        Assert.Contains(
            queue.Elements(),
            element => element.Name.LocalName == "ListBox.ContextMenu");
        AssertAttribute(queue, "AutomationProperties.HelpText");

        var trackView = Load(
            "src/Dextromethorphan.App/UI/Views/TrackListView.xaml");
        var tracks = NamedElement(trackView, "TrackList");
        Assert.Equal("Extended", Attribute(tracks, "SelectionMode"));
        Assert.Contains(
            tracks.Elements(),
            element => element.Name.LocalName == "ListBox.ContextMenu");
        AssertAttribute(tracks, "AutomationProperties.HelpText");
        var row = NamedElement(trackView, "TrackRow");
        Assert.Equal(
            "{Binding MetadataAutomationText}",
            Attribute(row, "ToolTip"));
        AssertAttribute(row, "ToolTipService.InitialShowDelay");
        Assert.DoesNotContain(
            trackView.Descendants(),
            element => element.Name.LocalName == "TrackMetadataToolTip");
    }

    [Fact]
    public void DialogValueEditorsHaveAutomationNames()
    {
        var paths = new[]
        {
            "src/Dextromethorphan.App/ErrorDialog.xaml",
            "src/Dextromethorphan.App/MetadataEditDialog.xaml",
            "src/Dextromethorphan.App/PlaylistEditDialog.xaml"
        };
        foreach (var path in paths)
        {
            var editors = Load(path)
                .Descendants()
                .Where(element => element.Name.LocalName is
                    "Slider" or "ComboBox" or "TextBox")
                .ToArray();
            foreach (var editor in editors)
                AssertAttribute(editor, "AutomationProperties.Name");
        }
    }

    [Fact]
    public void DynamicStatusSurfacesRaiseAutomationLiveRegionEvents()
    {
        var main = Load("src/Dextromethorphan.App/MainWindow.xaml");
        Assert.Contains(
            main.Descendants(),
            element => AttachedAttribute(element, "Announcement")?.Contains(
                "ActiveToast",
                StringComparison.Ordinal) == true);

        var statePresenter = Load(
            "src/Dextromethorphan.App/UI/Controls/StatePresenter.xaml");
        Assert.Contains(
            statePresenter.Descendants(),
            element => AttachedAttribute(element, "Announcement")?.Contains(
                "AccessibleName",
                StringComparison.Ordinal) == true);

        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src/Dextromethorphan.App/UI/LiveRegionBehavior.cs"));
        Assert.Contains("AutomationEvents.LiveRegionChanged", source);
        Assert.Contains("RaiseAutomationEvent", source);
    }

    private static XDocument Load(string relativePath) =>
        XDocument.Load(Path.Combine(RepositoryRoot(), relativePath));

    private static XElement NamedElement(XDocument document, string name) =>
        Assert.Single(
            document.Descendants(),
            element => Attribute(element, "Name") == name);

    private static void AssertAttribute(XElement element, string name) =>
        Assert.False(
            string.IsNullOrWhiteSpace(Attribute(element, name)),
            $"<{element.Name.LocalName}> is missing {name}.");

    private static void AssertPositiveNumber(XElement element, string name)
    {
        var text = Attribute(element, name);
        Assert.True(
            double.TryParse(
                text,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value)
            && value > 0,
            $"<{element.Name.LocalName}> requires a positive {name}; found '{text}'.");
    }

    private static string? Attribute(XElement element, string localName) =>
        element.Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName == localName)
            ?.Value;

    private static string? AttachedAttribute(XElement element, string property) =>
        element.Attributes()
            .FirstOrDefault(attribute =>
                attribute.Name.LocalName.Equals(property, StringComparison.Ordinal)
                || attribute.Name.LocalName.EndsWith(
                    "." + property,
                    StringComparison.Ordinal))
            ?.Value;

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(
                    directory.FullName,
                    "Dextromethorphan.slnx")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the Dextromethorphan repository root.");
    }
}

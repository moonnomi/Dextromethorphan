using System.Globalization;
using System.Windows;
using System.Xml.Linq;
using Dextromethorphan.App.UI;

namespace Dextromethorphan.Tests;

public sealed class MilestoneSevenShellContractTests
{
    [Fact]
    public void QueuePanelSupportsDockCollapseAndSettingsControl()
    {
        var main = Load("src/Dextromethorphan.App/MainWindow.xaml");
        var queue = NamedElement(main, "QueueInspectorPanel");

        Assert.Contains("QueuePanelColumn", AttachedAttribute(queue, "Column"));
        Assert.Contains("QueuePanelExpanded", Attribute(queue, "Visibility"));
        Assert.Contains(
            main.Descendants(),
            element => Attribute(element, "Visibility")?.Contains(
                "QueuePanelCollapsed",
                StringComparison.Ordinal) == true);
        Assert.Contains(
            main.Descendants(),
            element => Attribute(element, "Command")?.Contains(
                "SetQueueDockSideCommand",
                StringComparison.Ordinal) == true);

        var settings = Load("src/Dextromethorphan.App/SettingsWindow.xaml");
        Assert.Contains(
            settings.Descendants(),
            element => Attribute(element, "SelectedItem")?.Contains(
                "QueuePanelDockSide",
                StringComparison.Ordinal) == true);
        Assert.Contains(
            settings.Descendants(),
            element => Attribute(element, "IsChecked")?.Contains(
                "QueuePanelCompact",
                StringComparison.Ordinal) == true);
    }

    [Fact]
    public void NowPlayingInspectorHasMutuallySelectableLyricsAndDetails()
    {
        var main = Load("src/Dextromethorphan.App/MainWindow.xaml");
        var parameters = main.Descendants()
            .Where(element => element.Name.LocalName == "RadioButton")
            .Select(element => Attribute(element, "CommandParameter"))
            .Where(value => value is not null)
            .ToArray();

        Assert.Contains("Lyrics", parameters);
        Assert.Contains("Metadata", parameters);
        Assert.Contains(
            main.Descendants(),
            element => Attribute(element, "Visibility")?.Contains(
                "IsLyricsInspectorSelected",
                StringComparison.Ordinal) == true);
        Assert.Contains(
            main.Descendants(),
            element => Attribute(element, "Visibility")?.Contains(
                "IsMetadataInspectorSelected",
                StringComparison.Ordinal) == true);

        var bindings = main.Descendants()
            .SelectMany(element => element.Attributes())
            .Select(attribute => attribute.Value)
            .ToArray();
        Assert.Contains(bindings, value => value.Contains("CurrentTrack.QualityText", StringComparison.Ordinal));
        Assert.Contains(bindings, value => value.Contains("CurrentTrack.ReplayGainDisplayText", StringComparison.Ordinal));
        Assert.Contains(bindings, value => value.Contains("CurrentTrack.Path", StringComparison.Ordinal));
    }

    [Fact]
    public void BooleanGridLengthConverterCollapsesAndRestoresStarColumns()
    {
        var converter = new BooleanToGridLengthConverter();

        var collapsed = Assert.IsType<GridLength>(converter.Convert(
            false,
            typeof(GridLength),
            "0.95*",
            CultureInfo.InvariantCulture));
        var visible = Assert.IsType<GridLength>(converter.Convert(
            true,
            typeof(GridLength),
            "0.95*",
            CultureInfo.InvariantCulture));

        Assert.Equal(0, collapsed.Value);
        Assert.True(visible.IsStar);
        Assert.Equal(0.95, visible.Value, precision: 3);
    }

    private static XDocument Load(string relativePath) =>
        XDocument.Load(Path.Combine(RepositoryRoot(), relativePath));

    private static XElement NamedElement(XDocument document, string name) =>
        Assert.Single(
            document.Descendants(),
            element => Attribute(element, "Name") == name);

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
            if (File.Exists(Path.Combine(directory.FullName, "Dextromethorphan.slnx")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the Dextromethorphan repository root.");
    }
}

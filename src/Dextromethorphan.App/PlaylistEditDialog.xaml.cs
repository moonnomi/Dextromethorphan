using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dextromethorphan.Core.Library;
using Dextromethorphan.Core.Models;
using Microsoft.Win32;

namespace Dextromethorphan.App;

public partial class PlaylistEditDialog : Window
{
    private readonly Playlist? _existing;
    private readonly IReadOnlyList<Track> _initialTracks;
    private readonly IReadOnlyList<Track> _previewTracks;
    private string? _coverPath;
    private readonly List<(ComboBox Field, ComboBox Operator, TextBox Value)> _rows = [];

    public IReadOnlyList<SmartRuleMatch> MatchOptions { get; } = Enum.GetValues<SmartRuleMatch>();
    public IReadOnlyList<SmartField> SortOptions { get; } = Enum.GetValues<SmartField>();
    public IReadOnlyList<SmartField> Fields { get; } = Enum.GetValues<SmartField>();
    public IReadOnlyList<SmartOperator> Operators { get; } = Enum.GetValues<SmartOperator>();

    private PlaylistEditDialog(Playlist? existing, IReadOnlyList<Track>? initialTracks, IReadOnlyList<Track>? previewTracks)
    {
        _existing = existing; _initialTracks = initialTracks ?? []; _previewTracks = previewTracks ?? [];
        InitializeComponent(); DataContext = this;
        Heading.Text = existing is null ? "New playlist" : "Edit playlist";
        NameBox.Text = existing?.Name ?? string.Empty;
        DescriptionBox.Text = existing?.Description ?? string.Empty;
        _coverPath = existing?.CoverPath;
        CoverStatus.Text = string.IsNullOrWhiteSpace(_coverPath) ? "No custom cover" : System.IO.Path.GetFileName(_coverPath);
        MatchBox.SelectedItem = existing?.Rules?.Root.Match ?? SmartRuleMatch.All;
        SortBox.SelectedItem = existing?.Rules?.SortBy ?? SmartField.Title;
        DescendingCheck.IsChecked = existing?.Rules?.SortDescending == true;
        LimitBox.Text = existing?.Rules?.Limit?.ToString() ?? string.Empty;
        if (existing?.Rules is { } rules)
        {
            foreach (var condition in rules.Root.Conditions) AddRule(condition);
            if (rules.Root.Groups.Count > 0)
                NestedRulesBox.Text = JsonSerializer.Serialize(rules.Root, JsonOptions);
        }
        if (_rows.Count == 0 && existing?.Kind == PlaylistKind.Smart) AddRule(null);
        ManualRadio.IsChecked = existing?.Kind != PlaylistKind.Smart;
        SmartRadio.IsChecked = existing?.Kind == PlaylistKind.Smart;
        ManualRadio.IsEnabled = existing is null;
        SmartRadio.IsEnabled = existing is null;
        UpdateKindVisibility();
    }

    public static (PlaylistEditRequest Request, IReadOnlyList<Track> InitialTracks)? Show(Window owner, Playlist? existing, IReadOnlyList<Track>? initialTracks = null, IReadOnlyList<Track>? previewTracks = null)
    {
        var dialog = new PlaylistEditDialog(existing, initialTracks, previewTracks) { Owner = owner };
        return dialog.ShowDialog() == true ? (dialog.BuildRequest(), dialog._initialTracks) : null;
    }

    private void Kind_Changed(object sender, RoutedEventArgs e) => UpdateKindVisibility();
    private void UpdateKindVisibility() => SmartPanel.Visibility = SmartRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    private void AddRule_Click(object sender, RoutedEventArgs e) => AddRule(null);

    private void ChooseCover_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choose playlist cover", Filter = "Images|*.png;*.jpg;*.jpeg;*.webp;*.bmp", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        _coverPath = dialog.FileName; CoverStatus.Text = System.IO.Path.GetFileName(_coverPath);
    }

    private void AddRule(SmartRuleCondition? condition)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        var field = new ComboBox { Width = 125, ItemsSource = Fields, SelectedItem = condition?.Field ?? SmartField.Title, Margin = new Thickness(0, 0, 6, 0) };
        var op = new ComboBox { Width = 130, ItemsSource = Operators, SelectedItem = condition?.Operator ?? SmartOperator.Contains, Margin = new Thickness(0, 0, 6, 0) };
        var value = new TextBox { Width = 165, Text = condition?.Value ?? string.Empty, Margin = new Thickness(0, 0, 6, 0) };
        var remove = new Button { Content = "×", Width = 28, Padding = new Thickness(0) };
        AutomationProperties.SetName(field, "Smart rule field");
        AutomationProperties.SetName(op, "Smart rule operator");
        AutomationProperties.SetName(value, "Smart rule comparison value");
        AutomationProperties.SetName(remove, "Remove smart playlist rule");
        remove.Click += (_, _) => { ConditionsHost.Children.Remove(row); _rows.RemoveAll(item => ReferenceEquals(item.Field, field)); };
        row.Children.Add(field); row.Children.Add(op); row.Children.Add(value); row.Children.Add(remove); ConditionsHost.Children.Add(row); _rows.Add((field, op, value));
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(NameBox.Text)) throw new ArgumentException("Enter a playlist name.");
            if (NameBox.Text.Trim().Length > 200) throw new ArgumentException("Playlist names must be 200 characters or fewer.");
            if (SmartRadio.IsChecked == true && LimitBox.Text.Length > 0 && (!int.TryParse(LimitBox.Text, out var limit) || limit is < 1 or > 100_000)) throw new ArgumentException("Limit must be a whole number between 1 and 100000.");
            _ = BuildRequest();
            DialogResult = true;
        }
        catch (Exception exception) { MessageBox.Show(this, exception.Message, "Playlist", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private PlaylistEditRequest BuildRequest()
    {
        SmartPlaylistDefinition? rules = null;
        if (SmartRadio.IsChecked == true)
        {
            var conditions = _rows.Select(item => new SmartRuleCondition { Field = (SmartField)item.Field.SelectedItem!, Operator = (SmartOperator)item.Operator.SelectedItem!, Value = string.IsNullOrWhiteSpace(item.Value.Text) ? null : item.Value.Text.Trim() }).ToArray();
            int? limit = int.TryParse(LimitBox.Text, out var parsedLimit) ? parsedLimit : null;
            var root = new SmartRuleGroup { Match = (SmartRuleMatch)(MatchBox.SelectedItem ?? SmartRuleMatch.All), Conditions = conditions };
            if (!string.IsNullOrWhiteSpace(NestedRulesBox.Text))
                root = JsonSerializer.Deserialize<SmartRuleGroup>(NestedRulesBox.Text, JsonOptions) ?? throw new ArgumentException("Nested rule JSON is empty.");
            rules = new SmartPlaylistDefinition { Root = root, SortBy = (SmartField)(SortBox.SelectedItem ?? SmartField.Title), SortDescending = DescendingCheck.IsChecked == true, Limit = limit };
        }
        return new PlaylistEditRequest(NameBox.Text.Trim(), DescriptionBox.Text.Trim(), _coverPath, SmartRadio.IsChecked == true ? PlaylistKind.Smart : PlaylistKind.Manual, rules);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var request = BuildRequest();
            if (request.Rules is null) { PreviewText.Text = "Manual playlist: tracks are added when you save."; return; }
            var count = _previewTracks.Count(track => SmartPlaylistEvaluator.Matches(track, request.Rules));
            if (request.Rules.Limit is { } limit) count = Math.Min(count, limit);
            PreviewText.Text = $"Preview: {count:N0} matching track{(count == 1 ? "" : "s")}";
        }
        catch (Exception exception) { PreviewText.Text = exception.Message; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
}

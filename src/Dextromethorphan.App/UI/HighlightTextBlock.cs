using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Dextromethorphan.App.UI;

public sealed class HighlightTextBlock : TextBlock
{
    public static readonly DependencyProperty SourceTextProperty = DependencyProperty.Register(nameof(SourceText), typeof(string), typeof(HighlightTextBlock), new FrameworkPropertyMetadata(string.Empty, OnHighlightChanged));
    public static readonly DependencyProperty QueryProperty = DependencyProperty.Register(nameof(Query), typeof(string), typeof(HighlightTextBlock), new FrameworkPropertyMetadata(string.Empty, OnHighlightChanged));
    public string SourceText { get => (string)GetValue(SourceTextProperty); set => SetValue(SourceTextProperty, value); }
    public string Query { get => (string)GetValue(QueryProperty); set => SetValue(QueryProperty, value); }

    private static void OnHighlightChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not HighlightTextBlock block) return;
        block.Inlines.Clear();
        var source = block.SourceText ?? string.Empty; var query = block.Query?.Trim() ?? string.Empty;
        if (query.Length == 0) { block.Inlines.Add(new Run(source)); return; }
        var start = 0;
        while (start < source.Length)
        {
            var index = source.IndexOf(query, start, StringComparison.CurrentCultureIgnoreCase);
            if (index < 0) { block.Inlines.Add(new Run(source[start..])); break; }
            if (index > start) block.Inlines.Add(new Run(source[start..index]));
            block.Inlines.Add(new Run(source.Substring(index, query.Length)) { Background = new SolidColorBrush(Color.FromArgb(90, 138, 61, 255)), Foreground = Brushes.White });
            start = index + query.Length;
        }
    }
}

using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Dextromethorphan.App.UI;

internal static class DependencyObjectTree
{
    public static DependencyObject? GetParent(DependencyObject current) =>
        current switch
        {
            ContentElement content =>
                ContentOperations.GetParent(content)
                ?? (content as FrameworkContentElement)?.Parent,
            Visual or Visual3D => VisualTreeHelper.GetParent(current),
            _ => LogicalTreeHelper.GetParent(current)
        };

    public static T? FindAncestor<T>(DependencyObject? current)
        where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = GetParent(current);
        }

        return null;
    }
}

using System.Windows.Controls;
using System.Windows.Documents;
using Dextromethorphan.App.UI;

namespace Dextromethorphan.Tests;

public sealed class DependencyObjectTreeTests
{
    [Fact]
    public void InlineRunCanReachItsTextBlockWithoutVisualTreeException()
    {
        Exception? failure = null;
        TextBlock? expected = null;
        TextBlock? actual = null;
        var thread = new Thread(() =>
        {
            try
            {
                expected = new TextBlock();
                var run = new Run("Track title");
                expected.Inlines.Add(run);

                actual = DependencyObjectTree.FindAncestor<TextBlock>(run);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
        Assert.Same(expected, actual);
    }
}

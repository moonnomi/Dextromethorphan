using Dextromethorphan.App.UI;

namespace Dextromethorphan.Tests;

public sealed class IndexedReadOnlyListTests
{
    [Fact]
    public void ProjectsMembershipWithoutCopyingSourceObjects()
    {
        var first = new object();
        var second = new object();
        var third = new object();
        IReadOnlyList<object> source = [first, second, third];
        var projection = new IndexedReadOnlyList<object>(source, [2, 0]);

        Assert.Equal(2, projection.Count);
        Assert.Same(third, projection[0]);
        Assert.Same(first, projection[1]);
        Assert.Equal([third, first], projection);
    }

    [Fact]
    public void ReflectsUpdatedSourceSlotsByIndex()
    {
        object first = new();
        object replacement = new();
        var source = new[] { first };
        var projection = new IndexedReadOnlyList<object>(source, [0]);

        source[0] = replacement;

        Assert.Same(replacement, projection[0]);
    }
}

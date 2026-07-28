using System.Collections;

namespace Dextromethorphan.App.UI;

internal sealed class IndexedReadOnlyList<T>(
    IReadOnlyList<T> source,
    IReadOnlyList<int> indexes) : IReadOnlyList<T>
{
    public int Count => indexes.Count;

    public T this[int index] => source[indexes[index]];

    public IEnumerator<T> GetEnumerator()
    {
        for (var index = 0; index < indexes.Count; index++)
            yield return source[indexes[index]];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

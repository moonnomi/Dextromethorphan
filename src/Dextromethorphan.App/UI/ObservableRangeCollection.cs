using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Dextromethorphan.App.UI;

internal sealed class ObservableRangeCollection<T> : ObservableCollection<T>
{
    public void ReplaceRange(IEnumerable<T> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var items = source as IReadOnlyCollection<T> ?? source.ToArray();

        CheckReentrancy();
        base.Items.Clear();
        foreach (var item in items)
            base.Items.Add(item);

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace SerialLog.App.Infrastructure;

public sealed class TrimmableObservableCollection<T> : ObservableCollection<T>
{
    public void RemoveFirst(int count)
    {
        var removeCount = Math.Min(Math.Max(count, 0), Count);
        if (removeCount == 0)
        {
            return;
        }

        CheckReentrancy();
        if (Items is List<T> list)
        {
            list.RemoveRange(0, removeCount);
        }
        else
        {
            for (var index = 0; index < removeCount; index++)
            {
                Items.RemoveAt(0);
            }
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

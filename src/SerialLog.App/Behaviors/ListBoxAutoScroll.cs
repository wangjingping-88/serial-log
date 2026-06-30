using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace SerialLog.App.Behaviors;

public static class ListBoxAutoScroll
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(ListBoxAutoScroll),
            new PropertyMetadata(false, OnIsEnabledChanged));

    private static readonly DependencyProperty SubscriptionProperty =
        DependencyProperty.RegisterAttached(
            "Subscription",
            typeof(Subscription),
            typeof(ListBoxAutoScroll),
            new PropertyMetadata(null));

    public static void SetIsEnabled(DependencyObject element, bool value)
    {
        element.SetValue(IsEnabledProperty, value);
    }

    public static bool GetIsEnabled(DependencyObject element)
    {
        return (bool)element.GetValue(IsEnabledProperty);
    }

    private static void OnIsEnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not ListBox listBox)
        {
            return;
        }

        if ((bool)args.NewValue)
        {
            listBox.Loaded += OnLoaded;
            listBox.Unloaded += OnUnloaded;
            Attach(listBox);
            return;
        }

        listBox.Loaded -= OnLoaded;
        listBox.Unloaded -= OnUnloaded;
        Detach(listBox);
    }

    private static void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is ListBox listBox)
        {
            Attach(listBox);
            ScrollToEnd(listBox);
        }
    }

    private static void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (sender is ListBox listBox)
        {
            Detach(listBox);
        }
    }

    private static void Attach(ListBox listBox)
    {
        Detach(listBox);
        if (listBox.ItemsSource is not INotifyCollectionChanged source)
        {
            return;
        }

        NotifyCollectionChangedEventHandler handler = (_, args) =>
        {
            if (args.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset)
            {
                listBox.Dispatcher.BeginInvoke(() => ScrollToEnd(listBox), DispatcherPriority.Background);
            }
        };

        source.CollectionChanged += handler;
        listBox.SetValue(SubscriptionProperty, new Subscription(source, handler));
    }

    private static void Detach(ListBox listBox)
    {
        if (listBox.GetValue(SubscriptionProperty) is not Subscription subscription)
        {
            return;
        }

        subscription.Source.CollectionChanged -= subscription.Handler;
        listBox.ClearValue(SubscriptionProperty);
    }

    private static void ScrollToEnd(ListBox listBox)
    {
        if (listBox.Items.Count == 0)
        {
            return;
        }

        listBox.ScrollIntoView(listBox.Items[^1]);
    }

    private sealed record Subscription(INotifyCollectionChanged Source, NotifyCollectionChangedEventHandler Handler);
}

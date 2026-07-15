using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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

    private static readonly DependencyProperty IsPausedProperty =
        DependencyProperty.RegisterAttached(
            "IsPaused",
            typeof(bool),
            typeof(ListBoxAutoScroll),
            new PropertyMetadata(false));

    public static void SetIsEnabled(DependencyObject element, bool value)
    {
        element.SetValue(IsEnabledProperty, value);
    }

    public static bool GetIsEnabled(DependencyObject element)
    {
        return (bool)element.GetValue(IsEnabledProperty);
    }

    public static void Resume(ListBox listBox)
    {
        SetIsPaused(listBox, false);
        ScrollToEnd(listBox);
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
                listBox.Dispatcher.BeginInvoke(() =>
                {
                    if (!GetIsPaused(listBox))
                    {
                        ScrollToEnd(listBox);
                    }
                }, DispatcherPriority.Background);
            }
        };

        MouseWheelEventHandler mouseWheelHandler = (_, _) =>
        {
            SetIsPaused(listBox, true);
        };
        listBox.PreviewMouseWheel += mouseWheelHandler;

        ScrollViewer? scrollViewer = null;
        ScrollChangedEventHandler? scrollChangedHandler = null;
        scrollViewer = FindDescendant<ScrollViewer>(listBox);
        if (scrollViewer is not null)
        {
            scrollChangedHandler = (_, _) =>
            {
                if (IsAtBottom(scrollViewer))
                {
                    SetIsPaused(listBox, false);
                }
            };
            scrollViewer.ScrollChanged += scrollChangedHandler;
        }

        source.CollectionChanged += handler;
        listBox.SetValue(SubscriptionProperty, new Subscription(
            source,
            handler,
            mouseWheelHandler,
            scrollViewer,
            scrollChangedHandler));
    }

    private static void Detach(ListBox listBox)
    {
        if (listBox.GetValue(SubscriptionProperty) is not Subscription subscription)
        {
            return;
        }

        subscription.Source.CollectionChanged -= subscription.Handler;
        listBox.PreviewMouseWheel -= subscription.MouseWheelHandler;
        if (subscription.ScrollViewer is not null && subscription.ScrollChangedHandler is not null)
        {
            subscription.ScrollViewer.ScrollChanged -= subscription.ScrollChangedHandler;
        }

        SetIsPaused(listBox, false);
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

    private static bool GetIsPaused(DependencyObject element)
    {
        return (bool)element.GetValue(IsPausedProperty);
    }

    private static void SetIsPaused(DependencyObject element, bool value)
    {
        element.SetValue(IsPausedProperty, value);
    }

    private static bool IsAtBottom(ScrollViewer scrollViewer)
    {
        return scrollViewer.ScrollableHeight <= 0 ||
            scrollViewer.VerticalOffset >= scrollViewer.ScrollableHeight - 0.5;
    }

    private static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                return match;
            }

            var descendant = FindDescendant<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private sealed record Subscription(
        INotifyCollectionChanged Source,
        NotifyCollectionChangedEventHandler Handler,
        MouseWheelEventHandler MouseWheelHandler,
        ScrollViewer? ScrollViewer,
        ScrollChangedEventHandler? ScrollChangedHandler);
}

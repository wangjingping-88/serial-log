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

        var subscription = new Subscription(source);
        NotifyCollectionChangedEventHandler handler = (_, args) =>
        {
            if (args.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset)
            {
                ScheduleScrollToEnd(listBox, subscription);
            }

            if (args.Action == NotifyCollectionChangedAction.Remove &&
                args.OldStartingIndex == 0 &&
                GetIsPaused(listBox))
            {
                ScheduleViewportCompensation(listBox, subscription, args.OldItems?.Count ?? 0);
            }
        };

        MouseWheelEventHandler mouseWheelHandler = (_, _) =>
        {
            SetIsPaused(listBox, true);
        };
        listBox.AddHandler(UIElement.PreviewMouseWheelEvent, mouseWheelHandler, handledEventsToo: true);

        source.CollectionChanged += handler;
        subscription.Handler = handler;
        subscription.MouseWheelHandler = mouseWheelHandler;
        listBox.SetValue(SubscriptionProperty, subscription);
    }

    private static void Detach(ListBox listBox)
    {
        if (listBox.GetValue(SubscriptionProperty) is not Subscription subscription)
        {
            return;
        }

        subscription.Source.CollectionChanged -= subscription.Handler;
        listBox.RemoveHandler(UIElement.PreviewMouseWheelEvent, subscription.MouseWheelHandler);

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

    private static void ScheduleScrollToEnd(ListBox listBox, Subscription subscription)
    {
        if (GetIsPaused(listBox) || subscription.IsScrollScheduled)
        {
            return;
        }

        subscription.IsScrollScheduled = true;
        listBox.Dispatcher.BeginInvoke(() =>
        {
            subscription.IsScrollScheduled = false;
            if (!GetIsPaused(listBox))
            {
                ScrollToEnd(listBox);
            }
        }, DispatcherPriority.Background);
    }

    private static void ScheduleViewportCompensation(
        ListBox listBox,
        Subscription subscription,
        int removedItemCount)
    {
        if (removedItemCount <= 0)
        {
            return;
        }

        subscription.PendingHeadRemovalCount += removedItemCount;
        if (subscription.IsViewportCompensationScheduled)
        {
            return;
        }

        subscription.IsViewportCompensationScheduled = true;
        listBox.Dispatcher.BeginInvoke(() =>
        {
            subscription.IsViewportCompensationScheduled = false;
            var pendingRemovalCount = subscription.PendingHeadRemovalCount;
            subscription.PendingHeadRemovalCount = 0;

            if (!GetIsPaused(listBox) || pendingRemovalCount <= 0)
            {
                return;
            }

            var scrollViewer = FindVisualChild<ScrollViewer>(listBox);
            if (scrollViewer is null)
            {
                return;
            }

            // With logical scrolling enabled, removing items from index zero leaves the
            // same numeric offset pointing at newer rows. Move the offset back by the
            // number of trimmed rows so the user's paused viewport stays stationary.
            scrollViewer.ScrollToVerticalOffset(
                Math.Max(0, scrollViewer.VerticalOffset - pendingRemovalCount));
        }, DispatcherPriority.Loaded);
    }

    private static T? FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            var descendant = FindVisualChild<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private static bool GetIsPaused(DependencyObject element)
    {
        return (bool)element.GetValue(IsPausedProperty);
    }

    private static void SetIsPaused(DependencyObject element, bool value)
    {
        element.SetValue(IsPausedProperty, value);
    }

    private sealed class Subscription(INotifyCollectionChanged source)
    {
        public INotifyCollectionChanged Source { get; } = source;

        public NotifyCollectionChangedEventHandler Handler { get; set; } = null!;

        public MouseWheelEventHandler MouseWheelHandler { get; set; } = null!;

        public bool IsScrollScheduled { get; set; }

        public bool IsViewportCompensationScheduled { get; set; }

        public int PendingHeadRemovalCount { get; set; }
    }
}

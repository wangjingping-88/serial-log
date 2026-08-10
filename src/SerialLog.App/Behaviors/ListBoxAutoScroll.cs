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

    public static readonly DependencyProperty IsPausedProperty =
        DependencyProperty.RegisterAttached(
            "IsPaused",
            typeof(bool),
            typeof(ListBoxAutoScroll),
            new FrameworkPropertyMetadata(
                false,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnIsPausedChanged));

    public static readonly DependencyProperty HorizontalOffsetProperty =
        DependencyProperty.RegisterAttached(
            "HorizontalOffset",
            typeof(double),
            typeof(ListBoxAutoScroll),
            new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty VerticalOffsetProperty =
        DependencyProperty.RegisterAttached(
            "VerticalOffset",
            typeof(double),
            typeof(ListBoxAutoScroll),
            new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty HasStoredPositionProperty =
        DependencyProperty.RegisterAttached(
            "HasStoredPosition",
            typeof(bool),
            typeof(ListBoxAutoScroll),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static void SetIsEnabled(DependencyObject element, bool value)
    {
        element.SetValue(IsEnabledProperty, value);
    }

    public static bool GetIsEnabled(DependencyObject element)
    {
        return (bool)element.GetValue(IsEnabledProperty);
    }

    public static double GetHorizontalOffset(DependencyObject element)
    {
        return (double)element.GetValue(HorizontalOffsetProperty);
    }

    public static void SetHorizontalOffset(DependencyObject element, double value)
    {
        element.SetCurrentValue(HorizontalOffsetProperty, value);
    }

    public static double GetVerticalOffset(DependencyObject element)
    {
        return (double)element.GetValue(VerticalOffsetProperty);
    }

    public static void SetVerticalOffset(DependencyObject element, double value)
    {
        element.SetCurrentValue(VerticalOffsetProperty, value);
    }

    public static bool GetHasStoredPosition(DependencyObject element)
    {
        return (bool)element.GetValue(HasStoredPositionProperty);
    }

    public static void SetHasStoredPosition(DependencyObject element, bool value)
    {
        element.SetCurrentValue(HasStoredPositionProperty, value);
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
        }
    }

    private static void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (sender is ListBox listBox)
        {
            StoreCurrentPosition(listBox);
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
            if (args.Action is NotifyCollectionChangedAction.Reset or
                NotifyCollectionChangedAction.Add)
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
        ScheduleInitialPosition(listBox, subscription);
    }

    private static void Detach(ListBox listBox)
    {
        if (listBox.GetValue(SubscriptionProperty) is not Subscription subscription)
        {
            return;
        }

        subscription.Source.CollectionChanged -= subscription.Handler;
        listBox.RemoveHandler(UIElement.PreviewMouseWheelEvent, subscription.MouseWheelHandler);
        if (subscription.ScrollViewer is not null)
        {
            subscription.ScrollViewer.ScrollChanged -= subscription.ScrollChangedHandler;
        }

        listBox.ClearValue(SubscriptionProperty);
    }

    private static void ScrollToEnd(ListBox listBox)
    {
        if (listBox.Items.Count == 0)
        {
            return;
        }

        var scrollViewer = FindVisualChild<ScrollViewer>(listBox);
        scrollViewer?.ScrollToBottom();
    }

    private static void ScheduleInitialPosition(ListBox listBox, Subscription subscription)
    {
        listBox.Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(listBox.GetValue(SubscriptionProperty), subscription))
            {
                return;
            }

            var scrollViewer = FindVisualChild<ScrollViewer>(listBox);
            if (scrollViewer is null)
            {
                return;
            }

            subscription.ScrollViewer = scrollViewer;
            subscription.IsRestoringPosition = true;
            subscription.ScrollChangedHandler = (_, args) =>
            {
                if (subscription.IsRestoringPosition)
                {
                    return;
                }

                if (args.HorizontalChange != 0)
                {
                    SetHorizontalOffset(listBox, scrollViewer.HorizontalOffset);
                }

                if (args.VerticalChange != 0)
                {
                    SetVerticalOffset(listBox, scrollViewer.VerticalOffset);
                }

                if (args.HorizontalChange != 0 || args.VerticalChange != 0)
                {
                    SetHasStoredPosition(listBox, true);
                }
            };
            scrollViewer.ScrollChanged += subscription.ScrollChangedHandler;
            RestorePosition(listBox, subscription);

            listBox.Dispatcher.BeginInvoke(() =>
            {
                if (ReferenceEquals(listBox.GetValue(SubscriptionProperty), subscription))
                {
                    RestorePosition(listBox, subscription);
                }
            }, DispatcherPriority.ContextIdle);
        }, DispatcherPriority.Loaded);
    }

    private static void RestorePosition(ListBox listBox, Subscription subscription)
    {
        var scrollViewer = subscription.ScrollViewer;
        if (scrollViewer is null)
        {
            return;
        }

        subscription.IsRestoringPosition = true;
        if (GetIsPaused(listBox) && GetHasStoredPosition(listBox))
        {
            scrollViewer.ScrollToVerticalOffset(GetVerticalOffset(listBox));
        }
        else if (!GetIsPaused(listBox))
        {
            scrollViewer.ScrollToBottom();
        }

        listBox.UpdateLayout();
        if (GetHasStoredPosition(listBox))
        {
            scrollViewer.ScrollToHorizontalOffset(GetHorizontalOffset(listBox));
        }

        subscription.IsRestoringPosition = false;
        StoreCurrentPosition(listBox);
    }

    private static void StoreCurrentPosition(ListBox listBox)
    {
        var scrollViewer = FindVisualChild<ScrollViewer>(listBox);
        if (scrollViewer is null)
        {
            return;
        }

        SetHorizontalOffset(listBox, scrollViewer.HorizontalOffset);
        SetVerticalOffset(listBox, scrollViewer.VerticalOffset);
        SetHasStoredPosition(listBox, true);
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

    private static void OnIsPausedChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not ListBox listBox ||
            (bool)args.NewValue ||
            !GetIsEnabled(listBox) ||
            !listBox.IsLoaded)
        {
            return;
        }

        listBox.Dispatcher.BeginInvoke(
            () => ScrollToEnd(listBox),
            DispatcherPriority.Background);
    }

    public static bool GetIsPaused(DependencyObject element)
    {
        return (bool)element.GetValue(IsPausedProperty);
    }

    public static void SetIsPaused(DependencyObject element, bool value)
    {
        element.SetCurrentValue(IsPausedProperty, value);
    }

    private sealed class Subscription(INotifyCollectionChanged source)
    {
        public INotifyCollectionChanged Source { get; } = source;

        public NotifyCollectionChangedEventHandler Handler { get; set; } = null!;

        public MouseWheelEventHandler MouseWheelHandler { get; set; } = null!;

        public ScrollViewer? ScrollViewer { get; set; }

        public ScrollChangedEventHandler ScrollChangedHandler { get; set; } = null!;

        public bool IsRestoringPosition { get; set; }

        public bool IsScrollScheduled { get; set; }

        public bool IsViewportCompensationScheduled { get; set; }

        public int PendingHeadRemovalCount { get; set; }
    }
}

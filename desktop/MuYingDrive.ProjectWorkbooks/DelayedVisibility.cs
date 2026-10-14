using System.Windows;
using System.Windows.Threading;

namespace MuYingDrive.ProjectWorkbooks;

/// <summary>
/// Shows an element when a flag turns true, but only if it is still true shortly afterwards.
/// </summary>
/// <remarks>
/// For progress indicators. A busy bar bound straight to the flag draws for every operation,
/// including the ones that finish before the window has repainted — which is what a bar that
/// flashes for a frame looks like: an artifact, or a glitch, rather than information. Waiting
/// first means the bar means "this is taking a moment", which is the only thing it is for.
///
/// Deliberately not a converter: a converter answers a question when asked and cannot wait.
/// The timer lives on the element, so two indicators on screen do not share one.
///
/// Lives in this library rather than in the host because the host references the library
/// and not the other way round, so the library cannot see the host's controls — and this is
/// used in the library's own view.
/// </remarks>
public static class DelayedVisibility
{
    /// <summary>How long the flag must stay true before anything is drawn.</summary>
    /// <remarks>
    /// Around a fifth of a second: below the threshold where a wait starts to feel like one,
    /// above the time a local operation needs to complete. On the LAN this client's reads are
    /// tens of milliseconds, so the bar appears only when something is genuinely slow.
    /// </remarks>
    public static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(220);

    public static readonly DependencyProperty IsBusyProperty =
        DependencyProperty.RegisterAttached(
            "IsBusy", typeof(bool), typeof(DelayedVisibility),
            new PropertyMetadata(false, OnIsBusyChanged));

    private static readonly DependencyProperty TimerProperty =
        DependencyProperty.RegisterAttached(
            "Timer", typeof(DispatcherTimer), typeof(DelayedVisibility), new PropertyMetadata(null));

    public static bool GetIsBusy(DependencyObject element) => (bool)element.GetValue(IsBusyProperty);

    public static void SetIsBusy(DependencyObject element, bool value) =>
        element.SetValue(IsBusyProperty, value);

    private static void OnIsBusyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not UIElement element) return;

        Cancel(element);
        if (args.NewValue is not true)
        {
            element.Visibility = Visibility.Collapsed;
            return;
        }

        // Defaults to collapsed, so an element that is busy from the very first frame is
        // hidden until the delay says otherwise rather than showing and then hiding.
        element.Visibility = Visibility.Collapsed;
        var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = Delay };
        timer.Tick += (_, _) =>
        {
            Cancel(element);
            if (GetIsBusy(element)) element.Visibility = Visibility.Visible;
        };
        element.SetValue(TimerProperty, timer);
        timer.Start();
    }

    private static void Cancel(UIElement element)
    {
        if (element.GetValue(TimerProperty) is not DispatcherTimer timer) return;
        timer.Stop();
        element.ClearValue(TimerProperty);
    }
}

using System.Windows;
using System.Windows.Input;

namespace RunnerManager;

/// <summary>
/// Keeps a list still when a click focuses something in it. WPF brings an element that gets keyboard focus into view, also
/// when a click gave it the focus, so a click on a PC card header that was partly out of view scrolled the card list under
/// the mouse. Focus that comes from the keyboard (Tab) still brings the element into view.
/// </summary>
/// <remarks>
/// Set it on the panel inside the ScrollViewer, not on the ScrollViewer: the ScrollViewer acts on the request before
/// handlers set on it run. A request that is not for the focused element (code that shows a part of the list) is kept.
/// </remarks>
public static class StillOnClick
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(StillOnClick), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    /// <summary>The input device that was used last; tests replace it.</summary>
    internal static Func<InputDevice?> LastInput = () => InputManager.Current.MostRecentInputDevice;

    /// <summary>The element with keyboard focus; tests replace it.</summary>
    internal static Func<IInputElement?> Focused = () => Keyboard.FocusedElement;

    private static readonly RequestBringIntoViewEventHandler Handler = OnRequestBringIntoView;

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;
        element.RemoveHandler(FrameworkElement.RequestBringIntoViewEvent, Handler);
        if ((bool)e.NewValue) element.AddHandler(FrameworkElement.RequestBringIntoViewEvent, Handler);
    }

    private static void OnRequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        if (KeepsStill(e.TargetObject, Focused(), LastInput())) e.Handled = true;
    }

    /// <summary>
    /// Whether a request to bring <paramref name="target"/> into view is left out: it is for the focused element, and
    /// the mouse, a pen or a touch was used last, so a click gave it the focus.
    /// </summary>
    internal static bool KeepsStill(DependencyObject? target, IInputElement? focused, InputDevice? lastInput)
        => target != null && ReferenceEquals(target, focused) && lastInput is (MouseDevice or StylusDevice or TouchDevice);
}

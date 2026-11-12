using System.Windows;
using System.Windows.Input;

namespace RunnerManager;

/// <summary>
/// Keeps a list still when a click focuses something in it. WPF brings an element that gets keyboard focus into view, also
/// when a click gave it the focus, so a click on a PC card header that was partly out of view scrolled the card list under
/// the mouse. Focus that comes from the keyboard (Tab) or from a screen reader still brings the element into view: the
/// list keeps still only while a mouse button is down or a pen or a touch is in contact.
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

    /// <summary>Whether a mouse button is down or a pen or a touch is in contact with the screen; tests replace it.</summary>
    internal static Func<bool> PointerDown = () =>
        Mouse.LeftButton == MouseButtonState.Pressed || Mouse.RightButton == MouseButtonState.Pressed
        || Mouse.MiddleButton == MouseButtonState.Pressed || Mouse.XButton1 == MouseButtonState.Pressed
        || Mouse.XButton2 == MouseButtonState.Pressed || Stylus.CurrentStylusDevice is { InAir: false };

    private static readonly RequestBringIntoViewEventHandler Handler = OnRequestBringIntoView;

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;
        element.RemoveHandler(FrameworkElement.RequestBringIntoViewEvent, Handler);
        if ((bool)e.NewValue) element.AddHandler(FrameworkElement.RequestBringIntoViewEvent, Handler);
    }

    private static void OnRequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        if (KeepsStill(e.TargetObject, Focused(), LastInput(), PointerDown())) e.Handled = true;
    }

    /// <summary>
    /// Whether a request to bring <paramref name="target"/> into view is left out: it is for the focused element, the
    /// mouse, a pen or a touch was used last, and it is still pressed, so the click is giving it the focus now. Focus that
    /// a screen reader moves after a click comes when nothing is pressed, so it is brought into view.
    /// </summary>
    internal static bool KeepsStill(DependencyObject? target, IInputElement? focused, InputDevice? lastInput, bool pointerDown)
        => target != null && ReferenceEquals(target, focused) && pointerDown && lastInput is (MouseDevice or StylusDevice or TouchDevice);
}

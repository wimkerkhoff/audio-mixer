using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AudioMixer.Controls;

/// <summary>
/// A closed ComboBox that has focus changes its selection on the mouse wheel. In Settings, which
/// scrolls, that turned "scroll down the page" into "walk the priority picker": on 2026-10-07 it
/// went LAPEL → A1 → A2 → B2 → B1 → C1 in one second and stayed on C1 for three days, so the
/// presenter's lapel was no longer the priority mic. A closed picker now passes the wheel to
/// whatever scrolls around it; an open drop-down still scrolls its own list.
/// </summary>
public static class WheelGuard
{
    public static void Install()
    {
        EventManager.RegisterClassHandler(typeof(ComboBox), UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(OnPreviewMouseWheel));
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ComboBox combo || combo.IsDropDownOpen || e.Handled) return;
        e.Handled = true;
        if (VisualTreeHelper.GetParent(combo) is UIElement parent)
        {
            parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = combo,
            });
        }
    }
}

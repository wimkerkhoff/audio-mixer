using System.Windows;
using AudioMixer.ViewModels;

namespace AudioMixer.Views;

/// <summary>
/// Simple mode. Binds the SAME MainViewModel instance as the Advanced window rather than a copy, so
/// the two views can never disagree about what the mixer is doing — which is also what makes running
/// both side by side a useful comparison while the operator UI is being built.
/// </summary>
public partial class SimpleWindow : Window
{
    private readonly MainViewModel _vm;
    private DiagnosticsWindow? _diagnostics;
    private SettingsWindow? _settings;

    public SimpleWindow(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;
        Audio.AudioLog.Write($"Simple mode opened (scene={vm.Scenes.CurrentName}, {vm.Channels.Count} mics).");
    }

    /// <summary>The Advanced (full mixer) window, handed in so this panel can toggle it.</summary>
    public Window? AdvancedWindow { get; set; }

    private void Pin_Changed(object sender, RoutedEventArgs e) => Topmost = PinButton.IsChecked == true;

    /// <summary>Opens Diagnostics and Settings — used by the --open-all binding smoke test.</summary>
    public void OpenAuxiliaryWindows()
    {
        Diagnostics_Click(this, new RoutedEventArgs());
        Settings_Click(this, new RoutedEventArgs());
    }

    private void Advanced_Click(object sender, RoutedEventArgs e)
    {
        if (AdvancedWindow == null) return;
        if (AdvancedWindow.IsVisible)
        {
            AdvancedWindow.Hide();
            return;
        }
        AdvancedWindow.Show();
        if (AdvancedWindow.WindowState == WindowState.Minimized) AdvancedWindow.WindowState = WindowState.Normal;
        AdvancedWindow.Activate();
    }

    private void Diagnostics_Click(object sender, RoutedEventArgs e) =>
        Show(ref _diagnostics, () => new DiagnosticsWindow(_vm) { Owner = this });

    private void Settings_Click(object sender, RoutedEventArgs e) =>
        Show(ref _settings, () => new SettingsWindow(_vm) { Owner = this });

    // Reuse the existing window rather than stacking duplicates when the button is clicked twice.
    private static void Show<T>(ref T? window, Func<T> create) where T : Window
    {
        if (window == null || !window.IsLoaded)
        {
            window = create();
            window.Show();
        }
        else
        {
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
        }
    }
}

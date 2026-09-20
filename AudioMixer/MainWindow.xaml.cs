using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AudioMixer.ViewModels;

namespace AudioMixer;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    /// <summary>Shared with Simple mode, which binds this exact instance rather than a copy.</summary>
    public MainViewModel ViewModel => _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;
        Width = _viewModel.WindowWidth;
        Height = _viewModel.WindowHeight;

        // The window is resizable now, so the computed size is a sensible STARTING size rather than a
        // cage. It still follows the input count — ten strips need a wider window than three — but it
        // never shrinks below what the operator has chosen, or changing the count would undo a manual
        // resize. Vertical is left alone entirely: the ScrollViewer handles growth now, which is what
        // retires the silent-clipping class (the leveler row, the 10-input width).
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.WindowWidth))
                Width = Math.Max(Width, _viewModel.WindowWidth);
        };
        Closed += (_, _) => _viewModel.Dispose();
    }

    // Diagnostics used to be reachable only from Simple mode, so an operator running --advanced had no
    // way to open it — and therefore no way to reach "Reset calibration", which has to be pressed after
    // every transmitter gain change or the pre-change buffers keep dragging the median.
    private Views.DiagnosticsWindow? _diagnostics;
    private Views.SettingsWindow? _settings;

    private void Diagnostics_Click(object sender, RoutedEventArgs e) =>
        ShowSingle(ref _diagnostics, () => new Views.DiagnosticsWindow(_viewModel) { Owner = this });

    private void Settings_Click(object sender, RoutedEventArgs e) =>
        ShowSingle(ref _settings, () => new Views.SettingsWindow(_viewModel) { Owner = this });

    private static void ShowSingle<T>(ref T? window, Func<T> create) where T : Window
    {
        if (window == null || !window.IsLoaded) window = create();
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private void PopupList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox lb && lb.Tag is ToggleButton tb && lb.SelectedItem != null)
        {
            tb.IsChecked = false;
        }
    }

    private void ClosePopup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is ToggleButton tb)
        {
            tb.IsChecked = false;
        }
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AudioMixer.ViewModels;

namespace AudioMixer;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;
        Width = _viewModel.WindowWidth;
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.WindowWidth))
                Width = _viewModel.WindowWidth;
        };
        Closed += (_, _) => _viewModel.Dispose();
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

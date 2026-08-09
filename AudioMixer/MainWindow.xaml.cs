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
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.WindowWidth))
                Width = _viewModel.WindowWidth;
            else if (e.PropertyName == nameof(MainViewModel.WindowHeight))
                Height = _viewModel.WindowHeight;
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

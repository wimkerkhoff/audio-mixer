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
        Closed += (_, _) => _viewModel.Dispose();
    }

    private void PopupList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox lb && lb.Tag is ToggleButton tb && lb.SelectedItem != null)
        {
            tb.IsChecked = false;
        }
    }
}

using System.Windows;
using AudioMixer.ViewModels;

namespace AudioMixer.Views;

public partial class ChecksWindow : Window
{
    private readonly MainViewModel _vm;

    public ChecksWindow(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;
        Refresh();
    }

    private void Refresh()
    {
        CheckedAt.Text = $"checked {System.DateTime.Now:HH:mm:ss}";
        AllClear.Visibility = _vm.AlertCount == 0
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
    }

    private void Recheck_Click(object sender, RoutedEventArgs e) => Refresh();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

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
        PassList.ItemsSource = _vm.PassingChecks();
    }

    private void Recheck_Click(object sender, RoutedEventArgs e) => Refresh();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

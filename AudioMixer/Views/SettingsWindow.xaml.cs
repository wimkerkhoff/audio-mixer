using System.Windows;
using AudioMixer.ViewModels;

namespace AudioMixer.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Audio.AudioLog.Write($"Settings window opened ({vm.Channels.Count} channels, {vm.Outputs.Length} outputs).");
    }

    private void ClearDevice_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ChannelViewModel ch) return;
        string? device = ch.SelectedDevice?.FriendlyName ?? ch.DesiredDeviceName;
        if (device == null) return;
        var answer = MessageBox.Show(this,
            $"Forget that {ch.DisplayName} uses \"{device}{ch.SourceSuffix}\"?\n\n" +
            "It will NOT reconnect when plugged in again; you would have to choose it here.\n\n" +
            "To pack up, just unplug: the strip remembers its device.",
            "Forget device", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes) ch.ClearDeviceCommand.Execute(null);
    }
}

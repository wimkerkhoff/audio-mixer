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
}

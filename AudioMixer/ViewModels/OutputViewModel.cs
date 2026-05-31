using System.Collections.ObjectModel;
using AudioMixer.Audio;

namespace AudioMixer.ViewModels;

public sealed class OutputViewModel : ViewModelBase
{
    private readonly OutputBus _bus;
    private readonly Action<int, AudioDeviceInfo?> _onDeviceChanged;

    public int Index { get; }
    public string Label => Index == 0 ? "Output A (Headset)" : "Output B (Zoom / VB-CABLE)";
    public string ShortLabel => Index == 0 ? "A — Headset" : "B — Zoom";

    private string _customLabel = "";
    public string CustomLabel
    {
        get => _customLabel;
        set => SetField(ref _customLabel, value ?? "");
    }

    public ObservableCollection<AudioDeviceInfo> AvailableDevices { get; }

    private AudioDeviceInfo? _selectedDevice;
    public AudioDeviceInfo? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (SetField(ref _selectedDevice, value))
            {
                _onDeviceChanged(Index, value);
            }
        }
    }

    public float OutputPeakDb => _bus.OutputPeak.CurrentDb;
    public float OutputPeakHoldDb => _bus.OutputPeak.HoldDb;

    public OutputViewModel(
        int index,
        OutputBus bus,
        IEnumerable<AudioDeviceInfo> availableDevices,
        Action<int, AudioDeviceInfo?> onDeviceChanged)
    {
        Index = index;
        _bus = bus;
        _onDeviceChanged = onDeviceChanged;
        _customLabel = index == 0 ? "A — Headset" : "B — Zoom";
        AvailableDevices = new ObservableCollection<AudioDeviceInfo>(availableDevices);
    }

    public void RefreshMeters()
    {
        RaisePropertyChanged(nameof(OutputPeakDb));
        RaisePropertyChanged(nameof(OutputPeakHoldDb));
    }

    public void RefreshDevices(IEnumerable<AudioDeviceInfo> devices)
    {
        var newList = devices.ToList();
        var newIds = new HashSet<string>(newList.Select(d => d.Id));
        for (int i = AvailableDevices.Count - 1; i >= 0; i--)
        {
            if (!newIds.Contains(AvailableDevices[i].Id)) AvailableDevices.RemoveAt(i);
        }
        var existingIds = new HashSet<string>(AvailableDevices.Select(d => d.Id));
        foreach (var d in newList)
        {
            if (!existingIds.Contains(d.Id)) AvailableDevices.Add(d);
        }
        var currentId = SelectedDevice?.Id;
        if (currentId != null && !newIds.Contains(currentId))
        {
            SelectedDevice = null;
        }
    }
}

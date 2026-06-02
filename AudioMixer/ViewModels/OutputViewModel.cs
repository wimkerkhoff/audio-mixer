using System.Collections.ObjectModel;
using AudioMixer.Audio;

namespace AudioMixer.ViewModels;

public sealed class OutputViewModel : ViewModelBase
{
    private readonly OutputBus _bus;
    private readonly Action<int, AudioDeviceInfo?> _onDeviceChanged;
    private readonly Action<int, AutoMixMode> _onAutoMixModeChanged;
    private readonly Action<int, float> _onAutoMixStrengthChanged;
    private readonly Action<int> _onToggleRecord;

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

    private float _volumePercent = 100f;
    public float VolumePercent
    {
        get => _volumePercent;
        set
        {
            if (SetField(ref _volumePercent, Math.Clamp(value, 0f, 100f)))
                _bus.Volume = _volumePercent / 100f;
        }
    }

    public RelayCommand ToggleRecordCommand { get; }

    private bool _isRecording;
    public bool IsRecording => _isRecording;
    public string RecordIcon => _isRecording ? "■" : "●";
    public string RecordTooltip => _isRecording ? "Stop recording this output" : "Record this output to WAV";

    public void SetRecording(bool value)
    {
        if (_isRecording == value) return;
        _isRecording = value;
        RaisePropertyChanged(nameof(IsRecording));
        RaisePropertyChanged(nameof(RecordIcon));
        RaisePropertyChanged(nameof(RecordTooltip));
    }

    public string[] AutoMixModeOptions { get; } = { "Off", "Share", "Gate" };

    private int _autoMixModeIndex;
    public int AutoMixModeIndex
    {
        get => _autoMixModeIndex;
        set
        {
            if (SetField(ref _autoMixModeIndex, value))
            {
                _onAutoMixModeChanged(Index, (AutoMixMode)value);
                RaisePropertyChanged(nameof(AutoMixEnabled));
                RaisePropertyChanged(nameof(CurrentAutoMixLabel));
            }
        }
    }

    public bool AutoMixEnabled => _autoMixModeIndex != (int)AutoMixMode.Off;
    public string CurrentAutoMixLabel =>
        AutoMixModeOptions[Math.Clamp(_autoMixModeIndex, 0, AutoMixModeOptions.Length - 1)];

    private float _strengthPercent = 50f;
    public float StrengthPercent
    {
        get => _strengthPercent;
        set
        {
            if (SetField(ref _strengthPercent, value))
                _onAutoMixStrengthChanged(Index, (float)(value / 100.0));
        }
    }

    public OutputViewModel(
        int index,
        OutputBus bus,
        IEnumerable<AudioDeviceInfo> availableDevices,
        Action<int, AudioDeviceInfo?> onDeviceChanged,
        Action<int, AutoMixMode> onAutoMixModeChanged,
        Action<int, float> onAutoMixStrengthChanged,
        Action<int> onToggleRecord)
    {
        Index = index;
        _bus = bus;
        _onDeviceChanged = onDeviceChanged;
        _onAutoMixModeChanged = onAutoMixModeChanged;
        _onAutoMixStrengthChanged = onAutoMixStrengthChanged;
        _onToggleRecord = onToggleRecord;
        _customLabel = index == 0 ? "A — Headset" : "B — Zoom";
        AvailableDevices = new ObservableCollection<AudioDeviceInfo>(availableDevices);
        _bus.Volume = _volumePercent / 100f;
        ToggleRecordCommand = new RelayCommand(() => _onToggleRecord(Index));
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

using System.Collections.ObjectModel;
using AudioMixer.Audio;
using NAudio.CoreAudioApi;

namespace AudioMixer.ViewModels;

public sealed class ChannelViewModel : ViewModelBase
{
    private readonly InputChannel _channel;
    private readonly Action<int, AudioDeviceInfo?> _onDeviceChanged;

    public int Index { get; }
    public string Label => $"Input {Index + 1}";

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
            bool wasNull = _selectedDevice == null;
            if (SetField(ref _selectedDevice, value))
            {
                _onDeviceChanged(Index, value);
                if (wasNull && value != null && Routes != null && Routes.Length > 0
                    && Routes.All(r => !r.IsOn))
                {
                    Routes[0].IsOn = true;
                }
            }
        }
    }

    private float _volumePercent = 75f;
    public float VolumePercent
    {
        get => _volumePercent;
        set
        {
            if (SetField(ref _volumePercent, Math.Clamp(value, 0f, 100f)))
            {
                _channel.GainLinear = PercentToLinear(_volumePercent);
            }
        }
    }

    private bool _muted;
    public bool Muted
    {
        get => _muted;
        set
        {
            if (SetField(ref _muted, value)) _channel.Muted = value;
        }
    }

    private int _delayMs;
    public int DelayMs
    {
        get => _delayMs;
        set
        {
            int clamped = Math.Clamp(value, 0, 1000);
            if (SetField(ref _delayMs, clamped))
            {
                _channel.DelayMs = clamped;
                RaisePropertyChanged(nameof(HasAdvancedSettings));
            }
        }
    }

    private bool _isPriority;
    public bool IsPriority
    {
        get => _isPriority;
        set
        {
            if (SetField(ref _isPriority, value))
            {
                _channel.IsPriority = value;
                RaisePropertyChanged(nameof(HasAdvancedSettings));
            }
        }
    }

    // Drives the gear icon's "customized" highlight.
    public bool HasAdvancedSettings => _delayMs != 0 || _isPriority;

    public RelayCommand ClearDeviceCommand { get; }

    public RouteToggleViewModel[] Routes { get; }

    public float InputPeakDb => _channel.InputPeak.CurrentDb;
    public float PostPeakDb => _channel.PostPeak.CurrentDb;
    public float InputPeakHoldDb => _channel.InputPeak.HoldDb;
    public float PostPeakHoldDb => _channel.PostPeak.HoldDb;
    public bool IsDucking => _channel.IsDucking;
    public bool IsAutoMixActive => _channel.IsAutoMixActive;

    // Crest-derived clarity (0..1, higher = closer/cleaner). NaN when the mic hears no speech.
    public bool HasClarity => !float.IsNaN(_channel.Clarity);
    public double ClarityBar => float.IsNaN(_channel.Clarity) ? 0 : _channel.Clarity;
    public string ClarityText => float.IsNaN(_channel.Clarity) ? "—" : $"{_channel.Clarity * 100:F0}%";

    public ChannelViewModel(
        int index,
        InputChannel channel,
        IEnumerable<AudioDeviceInfo> availableDevices,
        int outputCount,
        Action<int, AudioDeviceInfo?> onDeviceChanged)
    {
        Index = index;
        _channel = channel;
        _onDeviceChanged = onDeviceChanged;
        _customLabel = $"Input {index + 1}";
        AvailableDevices = new ObservableCollection<AudioDeviceInfo>(availableDevices);

        Routes = new RouteToggleViewModel[outputCount];
        for (int o = 0; o < outputCount; o++)
        {
            Routes[o] = new RouteToggleViewModel(o, channel);
        }
        _channel.GainLinear = PercentToLinear(_volumePercent);
        ClearDeviceCommand = new RelayCommand(() => SelectedDevice = null);
    }

    public void RefreshMeters()
    {
        RaisePropertyChanged(nameof(InputPeakDb));
        RaisePropertyChanged(nameof(PostPeakDb));
        RaisePropertyChanged(nameof(InputPeakHoldDb));
        RaisePropertyChanged(nameof(PostPeakHoldDb));
        RaisePropertyChanged(nameof(IsDucking));
        RaisePropertyChanged(nameof(IsAutoMixActive));
        RaisePropertyChanged(nameof(HasClarity));
        RaisePropertyChanged(nameof(ClarityBar));
        RaisePropertyChanged(nameof(ClarityText));
    }

    public void RefreshDevices(IEnumerable<AudioDeviceInfo> devices)
    {
        if (!DeviceList.Sync(AvailableDevices, devices, SelectedDevice?.Id)) SelectedDevice = null;
    }

    private static float PercentToLinear(float percent)
    {
        if (percent <= 0f) return 0f;
        return percent / 100f;
    }
}

public sealed class RouteToggleViewModel : ViewModelBase
{
    private readonly InputChannel _channel;
    private readonly int _outputIndex;

    public int OutputIndex => _outputIndex;
    public string Label => _outputIndex == 0 ? "→ A" : "→ B";
    public string ShortLabel => _outputIndex == 0 ? "A" : "B";
    public string Tooltip => _outputIndex == 0 ? "Route to Output A (Headset)" : "Route to Output B (Zoom)";

    public bool IsOn
    {
        get => _channel.GetRoute(_outputIndex);
        set
        {
            _channel.SetRoute(_outputIndex, value);
            RaisePropertyChanged();
        }
    }

    public RouteToggleViewModel(int outputIndex, InputChannel channel)
    {
        _outputIndex = outputIndex;
        _channel = channel;
    }
}

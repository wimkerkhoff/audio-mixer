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

    // Per-bus LED state. A bus LED is green when the input is routed there and passing (not ducked),
    // amber when routed but ducked by the automixer, dim when not routed to that bus.
    public bool RoutedA => Routes.Length > 0 && Routes[0].IsOn;
    public bool RoutedB => Routes.Length > 1 && Routes[1].IsOn;
    public bool DuckingA => _channel.IsDuckingOn(0);
    public bool DuckingB => _channel.IsDuckingOn(1);

    // Live output labels (e.g. "MON", "HEADSET") so the LED tooltips name the actual buses.
    private OutputViewModel? _outputA;
    private OutputViewModel? _outputB;
    public string OutputALabel => string.IsNullOrWhiteSpace(_outputA?.CustomLabel) ? "A" : _outputA!.CustomLabel;
    public string OutputBLabel => string.IsNullOrWhiteSpace(_outputB?.CustomLabel) ? "B" : _outputB!.CustomLabel;
    public string LedATooltip => $"{OutputALabel}: green = live, amber = ducked, dim = not routed";
    public string LedBTooltip => $"{OutputBLabel}: green = live, amber = ducked, dim = not routed";

    // Wires the output strips' (renameable) labels into this channel's route toggles and bus LEDs so
    // their tooltips track the real output names. Called after the outputs exist.
    public void AttachOutputs(OutputViewModel[] outputs)
    {
        _outputA = outputs.Length > 0 ? outputs[0] : null;
        _outputB = outputs.Length > 1 ? outputs[1] : null;
        for (int o = 0; o < Routes.Length && o < outputs.Length; o++) Routes[o].AttachOutput(outputs[o]);
        if (_outputA != null) _outputA.PropertyChanged += OnOutputLabelChanged;
        if (_outputB != null) _outputB.PropertyChanged += OnOutputLabelChanged;
        RaiseOutputLabels();
    }

    private void OnOutputLabelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OutputViewModel.CustomLabel)) RaiseOutputLabels();
    }

    private void RaiseOutputLabels()
    {
        RaisePropertyChanged(nameof(OutputALabel));
        RaisePropertyChanged(nameof(OutputBLabel));
        RaisePropertyChanged(nameof(LedATooltip));
        RaisePropertyChanged(nameof(LedBTooltip));
    }

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
        RaisePropertyChanged(nameof(RoutedA));
        RaisePropertyChanged(nameof(RoutedB));
        RaisePropertyChanged(nameof(DuckingA));
        RaisePropertyChanged(nameof(DuckingB));
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

    private OutputViewModel? _output;

    public int OutputIndex => _outputIndex;
    public string Label => _outputIndex == 0 ? "→ A" : "→ B";
    public string ShortLabel => _outputIndex == 0 ? "A" : "B";
    public string Tooltip => _output != null && !string.IsNullOrWhiteSpace(_output.CustomLabel)
        ? $"Route to {_output.CustomLabel}"
        : $"Route to Output {ShortLabel}";

    // Lets the toggle's tooltip follow the (renameable) output label.
    public void AttachOutput(OutputViewModel output)
    {
        if (_output != null) _output.PropertyChanged -= OnOutputChanged;
        _output = output;
        _output.PropertyChanged += OnOutputChanged;
        RaisePropertyChanged(nameof(Tooltip));
    }

    private void OnOutputChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OutputViewModel.CustomLabel)) RaisePropertyChanged(nameof(Tooltip));
    }

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

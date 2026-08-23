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
                RaisePropertyChanged(nameof(IsStereoCapture));
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

    // Which transmitter of a split stereo receiver this strip carries (see ChannelSource). Two
    // strips may share one endpoint as long as they take opposite sides.
    private ChannelSource _source = ChannelSource.Stereo;
    public ChannelSource Source
    {
        get => _source;
        set
        {
            if (SetField(ref _source, value))
            {
                _channel.Source = value;
                RaisePropertyChanged(nameof(SourceStereo));
                RaisePropertyChanged(nameof(SourceLeft));
                RaisePropertyChanged(nameof(SourceRight));
                RaisePropertyChanged(nameof(SourceSuffix));
                RaisePropertyChanged(nameof(HasAdvancedSettings));
            }
        }
    }

    // Radio-button backing. Bound directly rather than through a converter on the enum: a WPF
    // trigger/converter comparison against a non-string value is the failure mode documented in
    // CLAUDE.md, and three bools cost less than debugging that again.
    public bool SourceStereo
    {
        get => _source == ChannelSource.Stereo;
        set { if (value) Source = ChannelSource.Stereo; }
    }

    public bool SourceLeft
    {
        get => _source == ChannelSource.Left;
        set { if (value) Source = ChannelSource.Left; }
    }

    public bool SourceRight
    {
        get => _source == ChannelSource.Right;
        set { if (value) Source = ChannelSource.Right; }
    }

    // Disambiguates the two strips that share one endpoint, in the device button and the log.
    public string SourceSuffix => _source switch
    {
        ChannelSource.Left => " (L)",
        ChannelSource.Right => " (R)",
        _ => "",
    };

    // A side selection only means something on a stereo endpoint; on a mono mic it is ignored.
    public bool IsStereoCapture => _channel.CaptureChannels >= 2;

    // Fixed-band high-pass, 0 = off. Removes the rumble/HVAC/handling energy that dominates a
    // DSP-free mic's floor without making any level-dependent decision — see the gear popup's note.
    private int _highPassHz;
    public int HighPassHz
    {
        get => _highPassHz;
        set
        {
            int clamped = value <= 0 ? 0 : Math.Clamp(value, 20, 400);
            if (SetField(ref _highPassHz, clamped))
            {
                _channel.HighPassHz = clamped;
                RaisePropertyChanged(nameof(HighPassText));
                RaisePropertyChanged(nameof(HasAdvancedSettings));
            }
        }
    }

    public string HighPassText => _highPassHz <= 0 ? "off" : $"{_highPassHz} Hz";

    // What this channel IS, independent of how the current scene has configured it. Scenes need a
    // stable "which mic is the lapel" that survives Prayer clearing the priority flag, so this must
    // not be inferred from IsPriority at apply time.
    private Models.ChannelRole _role = Models.ChannelRole.Room;
    public Models.ChannelRole Role
    {
        get => _role;
        set
        {
            if (SetField(ref _role, value)) RaisePropertyChanged(nameof(IsLapel));
        }
    }

    public bool IsLapel
    {
        get => _role == Models.ChannelRole.Lapel;
        set => Role = value ? Models.ChannelRole.Lapel : Models.ChannelRole.Room;
    }

    // Simple-mode mic dots. Raised from RefreshMeters, so neither may ever be a persisted property
    // (see PersistedProperties) — that is what the allowlist test guards.
    public bool HasDevice => SelectedDevice != null;
    public bool IsRoutedAnywhere => Routes.Any(r => r.IsOn);

    // Drives the gear icon's "customized" highlight.
    public bool HasAdvancedSettings =>
        _delayMs != 0 || _isPriority || _source != ChannelSource.Stereo || _highPassHz != 0;

    public RelayCommand ClearDeviceCommand { get; }

    public RouteToggleViewModel[] Routes { get; }

    public float InputPeakDb => _channel.InputPeak.CurrentDb;
    public float PostPeakDb => _channel.PostPeak.CurrentDb;
    public float InputPeakHoldDb => _channel.InputPeak.HoldDb;
    public float PostPeakHoldDb => _channel.PostPeak.HoldDb;
    public bool IsDucking => _channel.IsDucking;
    public bool IsAutoMixActive => _channel.IsAutoMixActive;

    // Wires the output strips' (renameable) labels into this channel's route toggles, so the toggle
    // and bus-LED tooltips track the real output names. Called after the outputs exist.
    public void AttachOutputs(OutputViewModel[] outputs)
    {
        for (int o = 0; o < Routes.Length && o < outputs.Length; o++) Routes[o].AttachOutput(outputs[o]);
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
        foreach (var r in Routes) r.RefreshLed();
        RaisePropertyChanged(nameof(HasClarity));
        RaisePropertyChanged(nameof(ClarityBar));
        RaisePropertyChanged(nameof(ClarityText));
        RaisePropertyChanged(nameof(HasDevice));
        RaisePropertyChanged(nameof(IsRoutedAnywhere));
    }

    public void RefreshDevices(IEnumerable<AudioDeviceInfo> devices)
    {
        if (!DeviceList.Sync(AvailableDevices, devices, SelectedDevice?.Id)) SelectedDevice = null;
    }

    private static float PercentToLinear(float percent) => percent / 100f;
}

public sealed class RouteToggleViewModel : ViewModelBase
{
    private readonly InputChannel _channel;
    private readonly int _outputIndex;

    private OutputViewModel? _output;

    public int OutputIndex => _outputIndex;
    public string ShortLabel => OutputViewModel.Tag(_outputIndex);
    public string Tooltip => $"Route to {OutputLabel}";

    // The bus's live name (e.g. "MON") when the operator has renamed it, else its letter.
    private string OutputLabel => string.IsNullOrWhiteSpace(_output?.CustomLabel)
        ? $"Output {ShortLabel}"
        : _output!.CustomLabel;

    // Per-bus LED: green when routed and passing, amber when routed but ducked by the automixer,
    // dim when not routed. Polled from the meter tick via RefreshLed.
    public bool IsDucking => _channel.IsDuckingOn(_outputIndex);
    public string LedTooltip => $"{OutputLabel}: green = live, amber = ducked, dim = not routed";

    // Only IsDucking is polled. IsOn must NOT be raised here: it is a persisted property, so a
    // 30 Hz notification would reset the autosave debounce forever (see PersistedProperties). It
    // changes only via the toggle or ApplyPreset, both of which already raise it.
    public void RefreshLed() => RaisePropertyChanged(nameof(IsDucking));

    // Lets the toggle's tooltip follow the (renameable) output label.
    public void AttachOutput(OutputViewModel output)
    {
        if (_output != null) _output.PropertyChanged -= OnOutputChanged;
        _output = output;
        _output.PropertyChanged += OnOutputChanged;
        RaiseLabels();
    }

    private void OnOutputChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OutputViewModel.CustomLabel)) RaiseLabels();
    }

    private void RaiseLabels()
    {
        RaisePropertyChanged(nameof(Tooltip));
        RaisePropertyChanged(nameof(LedTooltip));
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

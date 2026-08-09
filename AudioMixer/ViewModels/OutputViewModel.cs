using System.Collections.ObjectModel;
using AudioMixer.Audio;

namespace AudioMixer.ViewModels;

public sealed class OutputViewModel : ViewModelBase
{
    private readonly OutputBus _bus;
    private readonly IAutoMixControl _autoMix;
    private readonly Action<int, AudioDeviceInfo?> _onDeviceChanged;
    private readonly Action<int> _onToggleRecord;

    public int Index { get; }

    // Output buses are named by letter (A, B, …) everywhere the user sees them.
    public static string Tag(int index) => ((char)('A' + index)).ToString();

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
            if (SetField(ref _volumePercent, Math.Clamp(value, 0f, 100f)) && !_muted)
                _bus.Volume = _volumePercent / 100f;
        }
    }

    // Bus mute, used by Standby and by the Simple-mode on-air cards. Deliberately NOT persisted: a
    // mute that survived a restart would put an operator on air-silent with no memory of why, and
    // Standby is a runtime state, not a configuration. It applies at the bus volume, which sits AFTER
    // the peak/recorder tap — so a muted output still meters, and you can see audio is arriving.
    private bool _muted;
    public bool Muted
    {
        get => _muted;
        set
        {
            if (!SetField(ref _muted, value)) return;
            _bus.Volume = value ? 0f : _volumePercent / 100f;
            RaisePropertyChanged(nameof(OnAirState));
        }
    }

    /// <summary>Muted / On air / Off air, for the Simple-mode pill.</summary>
    public string OnAirState => _muted ? "MUTED"
        : SelectedDevice == null ? "OFF AIR"
        : "ON AIR";

    // One-line plain-English answer to "why this mic?", for the Diagnostics window header.
    private string _selectionVerdict = "";
    public string SelectionVerdict
    {
        get => _selectionVerdict;
        private set => SetField(ref _selectionVerdict, value);
    }

    public void RefreshVerdict(AutoMixDiag diag, IReadOnlyList<ChannelViewModel> channels)
    {
        if (Index >= diag.Winner.Length) return;

        string Name(int i) => i < 0 || i >= channels.Count ? "—"
            : string.IsNullOrWhiteSpace(channels[i].CustomLabel) ? $"in{i + 1}" : channels[i].CustomLabel;

        var mode = Index < diag.Mode.Length ? diag.Mode[Index] : AutoMixMode.Off;
        if (mode == AutoMixMode.Off)
        {
            SelectionVerdict = "automix off — every routed mic passes at unity";
            return;
        }

        int winner = diag.Winner[Index];
        int active = Index < diag.ActiveInput.Length ? diag.ActiveInput[Index] : -1;

        if (winner < 0)
        {
            // -1 has three causes and they mean very different things; say which.
            bool priority = active >= 0 && active < channels.Count && channels[active].IsPriority;
            SelectionVerdict = priority
                ? $"{Name(active)} is priority — every room mic is ducked"
                : "room is silent — all routed mics open, nothing selected";
            return;
        }

        string rule = Index < diag.ReferenceGuided.Length && diag.ReferenceGuided[Index] ? "match-lapel (corr +0.05)"
            : Index < diag.PreferNatural.Length && diag.PreferNatural[Index] ? "prefer-natural (flux-cv x0.85)"
            : "level (+3 dB)";

        int hold = Index < diag.WinnerHold.Length ? diag.WinnerHold[Index] : 0;
        string holdText = hold > 0 ? $", held {hold * 10} ms more" : "";

        SelectionVerdict = $"{mode}: {Name(winner)} winning on {rule}{holdText}";
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
                _autoMix.SetAutoMixMode(Index, (AutoMixMode)value);
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
                _autoMix.SetAutoMixStrength(Index, (float)(value / 100.0));
        }
    }

    // Stable hand-off: hold the selected mic with hysteresis so a brief louder moment on another mic
    // (e.g. a distant speakerphone's AGC pumping up in a talker's pause) can't steal the selection.
    // On by default. Off = legacy instantaneous-loudest selection.
    private bool _stableHandoff = true;
    public bool StableHandoff
    {
        get => _stableHandoff;
        set
        {
            if (SetField(ref _stableHandoff, value))
                _autoMix.SetAutoMixStableHandoff(Index, value);
        }
    }

    // Reference-guided selection: pick the room mic whose envelope best matches the priority/lapel mic
    // instead of the loudest. Experimental, off by default. Needs an active priority mic as reference.
    private bool _referenceGuided;
    public bool ReferenceGuided
    {
        get => _referenceGuided;
        set
        {
            if (SetField(ref _referenceGuided, value))
                _autoMix.SetAutoMixReferenceGuided(Index, value);
        }
    }

    // Reference-free: among mics within a level floor of the loudest, prefer the most natural (lowest
    // spectral-flux instability). Experimental, off by default. Lower precedence than Match lapel.
    private bool _preferNatural;
    public bool PreferNatural
    {
        get => _preferNatural;
        set
        {
            if (SetField(ref _preferNatural, value))
                _autoMix.SetAutoMixPreferNatural(Index, value);
        }
    }

    public OutputViewModel(
        int index,
        OutputBus bus,
        IAutoMixControl autoMix,
        IEnumerable<AudioDeviceInfo> availableDevices,
        Action<int, AudioDeviceInfo?> onDeviceChanged,
        Action<int> onToggleRecord)
    {
        Index = index;
        _bus = bus;
        _autoMix = autoMix;
        _onDeviceChanged = onDeviceChanged;
        _onToggleRecord = onToggleRecord;
        _customLabel = index switch { 0 => "A — Headset", 1 => "B — Zoom", _ => $"{Tag(index)} — Output" };
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
        if (!DeviceList.Sync(AvailableDevices, devices, SelectedDevice?.Id)) SelectedDevice = null;
    }
}

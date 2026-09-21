using System.Collections.ObjectModel;
using AudioMixer.Audio;

namespace AudioMixer.ViewModels;

public sealed class OutputViewModel : ViewModelBase
{
    private readonly OutputBus _bus;
    private readonly IAutoMixControl _autoMix;
    private readonly Action<int, AudioDeviceInfo?> _onDeviceChanged;

    public int Index { get; }

    // Output buses are named by letter (A, B, …) everywhere the user sees them.
    public static string Tag(int index) => ((char)('A' + index)).ToString();

    /// <summary>"A: OBS/Zoom". The bus letter is what routing is spoken in, so it leads.</summary>
    public string TaggedLabel => $"{Tag(Index)}: {CustomLabel}";

    /// <summary>
    /// Whether the WASAPI stream is actually running. A stopped stream on a device that is still
    /// present (format renegotiation, another app taking the endpoint, a USB headset changing rate)
    /// looks identical to a healthy bus everywhere else: the meter has no decay, so it holds its last
    /// peak and the silent-bus rule never fires.
    /// </summary>
    public bool IsPlaying => _bus.IsPlaying;

    /// <summary>Which device this bus is playing to — the answer to "where does B actually go?".</summary>
    public string DeviceTooltip => SelectedDevice == null
        ? $"Bus {Tag(Index)} has no output device selected."
        : $"Bus {Tag(Index)} plays to {SelectedDevice.FriendlyName}";

    private string _customLabel = "";
    public string CustomLabel
    {
        get => _customLabel;
        set
        {
            if (SetField(ref _customLabel, value ?? "")) RaisePropertyChanged(nameof(TaggedLabel));
        }
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
                RaisePropertyChanged(nameof(DeviceTooltip));
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

        int hold = Index < diag.WinnerHold.Length ? diag.WinnerHold[Index] : 0;
        string holdText = hold > 0 ? $", held {hold * 10} ms more" : "";

        SelectionVerdict = $"{mode}: {Name(winner)} winning on level (+3 dB){holdText}";
    }

    private bool _isRecording;
    public bool IsRecording => _isRecording;

    public void SetRecording(bool value)
    {
        if (_isRecording == value) return;
        _isRecording = value;
        RaisePropertyChanged(nameof(IsRecording));
    }

    public string[] AutoMixModeOptions { get; } = { "Off", "Gate" };

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

    // Stable hand-off: hold the selected mic with hysteresis so a brief louder moment on another mic
    // (e.g. a distant speakerphone's AGC pumping up in a talker's pause) can't steal the selection.
    // On by default. Off = legacy instantaneous-loudest selection.

    // Reference-guided selection: pick the room mic whose envelope best matches the priority/lapel mic
    // instead of the loudest. Experimental, off by default. Needs an active priority mic as reference.

    // Reference-free: among mics within a level floor of the loudest, prefer the most natural (lowest
    // spectral-flux instability). Experimental, off by default. Lower precedence than Match lapel.

    public OutputViewModel(
        int index,
        OutputBus bus,
        IAutoMixControl autoMix,
        IEnumerable<AudioDeviceInfo> availableDevices,
        Action<int, AudioDeviceInfo?> onDeviceChanged)
    {
        Index = index;
        _bus = bus;
        _autoMix = autoMix;
        _onDeviceChanged = onDeviceChanged;
        _customLabel = index switch { 0 => "A — Headset", 1 => "B — Zoom", _ => $"{Tag(index)} — Output" };
        AvailableDevices = new ObservableCollection<AudioDeviceInfo>(availableDevices);
        _bus.Volume = _volumePercent / 100f;
    }

    // --- Bus leveler ---------------------------------------------------------------------------
    // Settings live on the OutputBus (they must survive an engine-driven restart), so these are thin
    // write-throughs like VolumePercent — no IAutoMixControl involvement: the leveler is a bus device,
    // not an automix decision.

    private bool _levelerEnabled;
    public bool LevelerEnabled
    {
        get => _levelerEnabled;
        set
        {
            if (!SetField(ref _levelerEnabled, value)) return;
            _bus.Leveler.Enabled = value;
            RaiseLevelerDisplay();
        }
    }

    public LevelerStrength[] LevelerStrengthOptions { get; } =
        Enum.GetValues<LevelerStrength>();

    private LevelerStrength _levelerStrength = LevelerStrength.Medium;
    public LevelerStrength LevelerStrength
    {
        get => _levelerStrength;
        set
        {
            if (!SetField(ref _levelerStrength, value)) return;
            _bus.Leveler.Strength = value;
            // The preset moves with it, so a strength choice is one persisted decision, not four.
            _levelerThresholdDb = _bus.Leveler.ThresholdDb;
            _levelerRatio = _bus.Leveler.Ratio;
            _levelerMaxGainDb = _bus.Leveler.MaxGainDb;
            RaisePropertyChanged(nameof(LevelerThresholdDb));
            RaisePropertyChanged(nameof(LevelerRatio));
            RaisePropertyChanged(nameof(LevelerMaxGainDb));
            RaiseLevelerDisplay();
        }
    }

    public int LevelerStrengthIndex
    {
        get => (int)_levelerStrength;
        set { if (value >= 0) LevelerStrength = (LevelerStrength)value; }
    }

    private float _levelerThresholdDb = -26f;
    public float LevelerThresholdDb
    {
        get => _levelerThresholdDb;
        set
        {
            if (!SetField(ref _levelerThresholdDb, value)) return;
            _bus.Leveler.ThresholdDb = value;
            _levelerThresholdDb = _bus.Leveler.ThresholdDb;
            RaiseLevelerDisplay();
        }
    }

    private float _levelerRatio = 3f;
    public float LevelerRatio
    {
        get => _levelerRatio;
        set
        {
            if (!SetField(ref _levelerRatio, value)) return;
            _bus.Leveler.Ratio = value;
            _levelerRatio = _bus.Leveler.Ratio;
            RaiseLevelerDisplay();
        }
    }

    private float _levelerAttackMs = 100f;
    public float LevelerAttackMs
    {
        get => _levelerAttackMs;
        set { if (SetField(ref _levelerAttackMs, value)) { _bus.Leveler.AttackMs = value; _levelerAttackMs = _bus.Leveler.AttackMs; } }
    }

    private float _levelerReleaseMs = 2000f;
    public float LevelerReleaseMs
    {
        get => _levelerReleaseMs;
        set { if (SetField(ref _levelerReleaseMs, value)) { _bus.Leveler.ReleaseMs = value; _levelerReleaseMs = _bus.Leveler.ReleaseMs; } }
    }

    private float _levelerMaxGainDb = 10f;
    public float LevelerMaxGainDb
    {
        get => _levelerMaxGainDb;
        set
        {
            if (!SetField(ref _levelerMaxGainDb, value)) return;
            _bus.Leveler.MaxGainDb = value;
            _levelerMaxGainDb = _bus.Leveler.MaxGainDb;
            RaiseLevelerDisplay();
        }
    }

    private float _levelerIdleFloorDb = -45f;
    public float LevelerIdleFloorDb
    {
        get => _levelerIdleFloorDb;
        set { if (SetField(ref _levelerIdleFloorDb, value)) { _bus.Leveler.IdleFloorDb = value; _levelerIdleFloorDb = _bus.Leveler.IdleFloorDb; } }
    }

    private float _limiterCeilingDb = -1f;
    public float LimiterCeilingDb
    {
        get => _limiterCeilingDb;
        set { if (SetField(ref _limiterCeilingDb, value)) { _bus.Leveler.CeilingDb = value; _limiterCeilingDb = _bus.Leveler.CeilingDb; } }
    }

    // Settings-derived display. Raised from the setters ONLY — never from RefreshMeters, or it would
    // restart the autosave debounce 30x/second and autosave would never fire.
    public string LevelerState => _levelerEnabled ? "on" : "off";
    public string LevelerSummary => _levelerEnabled ? $"{_levelerStrength} {_levelerRatio:F0}:1" : "Leveler off";

    private void RaiseLevelerDisplay()
    {
        RaisePropertyChanged(nameof(LevelerState));
        RaisePropertyChanged(nameof(LevelerSummary));
    }

    // Display-only, polled at 30 Hz. MUST NOT be added to PersistedProperties.
    public float LevelerGainDb => _bus.LevelerGainDb;
    public string LevelerGainText => !_levelerEnabled ? "—" : $"{_bus.LevelerGainDb:+0.0;-0.0;0.0} dB";
    public double LevelerLiftBar =>
        Math.Clamp(_bus.LevelerGainDb / Math.Max(1f, _levelerMaxGainDb), 0, 1);

    public void RefreshMeters()
    {
        RaisePropertyChanged(nameof(OutputPeakDb));
        RaisePropertyChanged(nameof(OutputPeakHoldDb));
        RaisePropertyChanged(nameof(LevelerGainDb));
        RaisePropertyChanged(nameof(LevelerGainText));
        RaisePropertyChanged(nameof(LevelerLiftBar));
    }

    public void RefreshDevices(IEnumerable<AudioDeviceInfo> devices)
    {
        if (!DeviceList.Sync(AvailableDevices, devices, SelectedDevice?.Id)) SelectedDevice = null;
    }
}

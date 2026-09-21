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

    // What this strip WANTS to be bound to, which outlives the endpoint going away. A hot-plug USB
    // audio device gets a fresh WASAPI GUID on every replug, so the preset self-heals by friendly
    // name — but only if a name is still there to match. Without these, unplugging nulled
    // SelectedDevice, the next autosave (500 ms later) wrote DeviceId=null DeviceName=null, and the
    // strip forgot what it was for: replugging brought back a device with a new GUID and nothing to
    // match it against, so the operator remapped by hand every single time.
    public string? DesiredDeviceId { get; private set; }
    public string? DesiredDeviceName { get; private set; }

    /// <summary>
    /// The serial-derived container id of the device this strip wants, when it has one. Outlives both
    /// of the above: the endpoint GUID is regenerated on every replug and the friendly name is shared
    /// by two identical receivers, so this is the only key that survives a port change AND tells two
    /// units of one model apart. Null for a device whose identity is the port it is in.
    /// </summary>
    public string? DesiredDeviceKey { get; private set; }

    /// <summary>Seeds what this strip wants from a saved preset, before any device is bound.</summary>
    public void RestoreDesiredDevice(string? id, string? name, string? key)
    {
        DesiredDeviceId = id;
        DesiredDeviceName = name;
        DesiredDeviceKey = key;
    }

    /// <summary>Forget the desired device — an explicit "None" from the operator, not a disappearance.</summary>
    public void ClearDesiredDevice()
    {
        DesiredDeviceId = null;
        DesiredDeviceName = null;
        DesiredDeviceKey = null;
    }

    private AudioDeviceInfo? _selectedDevice;
    public AudioDeviceInfo? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            bool wasNull = _selectedDevice == null;
            if (value != null)
            {
                DesiredDeviceId = value.Id;
                DesiredDeviceName = value.FriendlyName;
                // Only stored when it is actually durable — see Services.DeviceIdentity. Writing a
                // port-derived container id would look authoritative and be no better than the GUID.
                DesiredDeviceKey = Services.DeviceIdentity.StableKey(value.ContainerId, value.Bus);
            }
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
                RaisePropertyChanged(nameof(VolumeText));
            }
        }
    }

    /// <summary>
    /// Asked before a change that could take audio off a bus. Set by MainViewModel, which is the only
    /// thing that can see the sibling channels a routing decision depends on. Returning false vetoes
    /// the change and the control snaps back. Null in unit contexts, where nothing is vetoed.
    /// </summary>
    public Func<int, bool>? MuteGuard { get; set; }
    public Func<int, int, bool>? RouteGuard { get; set; }

    private bool _muted;
    public bool Muted
    {
        get => _muted;
        set
        {
            // Only muting can uncover a bus; unmuting always adds. Asking on both would mean a refusal
            // could trap the channel in the muted state it was refused out of.
            if (value && !_muted && MuteGuard != null && !MuteGuard(Index))
            {
                RaisePropertyChanged();
                return;
            }
            if (SetField(ref _muted, value)) _channel.Muted = value;
        }
    }

    // --- operator-panel display -----------------------------------------------------------------

    /// <summary>
    /// What the row's state stripe says, as a string because a WPF trigger Value is parsed as one —
    /// comparing it against anything else fails silently (see the DataTrigger gotcha).
    /// </summary>
    public string RowState
    {
        get
        {
            if (SelectedDevice == null || !IsRoutedAnywhere || Muted) return "off";
            if ((Environment.TickCount64 - _channel.LastDataTicks) > 2000) return "dead";
            return IsAutoMixActive ? "live" : "open";
        }
    }

    /// <summary>Meter scale: -60 dBFS at the left, 0 at the right.</summary>
    public const double MeterFloorDb = -60;

    /// <summary>Where speech should sit, and how wide the "right" zone is on that scale.</summary>
    public const double TargetDb = -24;
    public const double TargetHalfWidthDb = 6;

    public static double FractionFor(double db) =>
        Math.Clamp((db - MeterFloorDb) / -MeterFloorDb, 0, 1);

    /// <summary>0..1 across the meter, from the post-fader peak the operator is actually sending.</summary>
    public double MeterFraction => FractionFor(PostPeakDb);

    /// <summary>Left edge and width of the target band, as fractions of the meter.</summary>
    public static double TargetBandStart => FractionFor(TargetDb - TargetHalfWidthDb);
    public static double TargetBandWidth => FractionFor(TargetDb + TargetHalfWidthDb) - TargetBandStart;

    /// <summary>Settled medians plus how far speech is from target — the number that makes it actionable.</summary>
    public string CalibrationText
    {
        get
        {
            var cal = _channel.SnapshotCalibration();
            if (float.IsNaN(cal.SpeechDb)) return "speech —   floor —";
            double off = cal.SpeechDb - TargetDb;
            string floor = float.IsNaN(cal.FloorDb) ? "—" : $"{cal.FloorDb:F0}";
            return $"speech {cal.SpeechDb,4:F0}  floor {floor,4}  {off:+0;-0;0} dB";
        }
    }

    public double BandStart => TargetBandStart;
    public double BandWidth => TargetBandWidth;

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
                RaisePropertyChanged(nameof(SideIndex));
                RaisePropertyChanged(nameof(HasAdvancedSettings));
            }
        }
    }

    // Radio-button backing. Bound directly rather than through a converter on the enum: a WPF
    // trigger/converter comparison against a non-string value is the failure mode documented in
    // CLAUDE.md, and three bools cost less than debugging that again.
    /// <summary>Combo-box backing for the split side, now that Settings owns this control.</summary>
    public int SideIndex
    {
        get => (int)_source;
        set { if (value >= 0 && value <= 2) Source = (ChannelSource)value; }
    }

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
                RaisePropertyChanged(nameof(HighPassIndex));
                RaisePropertyChanged(nameof(HasAdvancedSettings));
            }
        }
    }

    /// <summary>Shown on the level slider's tooltip; unity is the normal setting, not a compromise.</summary>
    public string VolumeText => _volumePercent >= 99.5f
        ? "Level: full (normal)"
        : $"Level: {_volumePercent:F0}% — the fader can only attenuate, never boost";

    public string HighPassText => _highPassHz <= 0 ? "off" : $"{_highPassHz} Hz";

    // A discrete list, not a slider. The cutoff used to be a 0-200 Hz slider with 10 Hz snap ticks:
    // 21 positions in a ~115 px strip column is ~5 px per tick, so which values you could land on
    // depended on pixel rounding as you dragged, and operators reported cutoffs they simply could not
    // select. These are the cutoffs finding 5 actually measured, plus 90 because shipped presets use
    // it. A value outside the list (hand-edited preset) reports index -1 and stays visible in
    // HighPassText rather than being silently snapped to a neighbour.
    public static readonly int[] HighPassOptions = { 0, 60, 80, 90, 100, 120, 150 };

    public static int HighPassIndexOf(int hz) => Array.IndexOf(HighPassOptions, hz <= 0 ? 0 : hz);

    public string[] HighPassChoices { get; } =
        HighPassOptions.Select(hz => hz <= 0 ? "off" : $"{hz} Hz").ToArray();

    public int HighPassIndex
    {
        get => HighPassIndexOf(_highPassHz);
        set { if (value >= 0 && value < HighPassOptions.Length) HighPassHz = HighPassOptions[value]; }
    }

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
        _isPriority || _source != ChannelSource.Stereo || _highPassHz != 0;

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
        // An explicit clear is the operator saying "this strip has no mic", so it forgets the desired
        // device too — otherwise the reattach pass would helpfully undo them.
        ClearDeviceCommand = new RelayCommand(() => { ClearDesiredDevice(); SelectedDevice = null; });
    }

    public void RefreshMeters()
    {
        RaisePropertyChanged(nameof(InputPeakDb));
        RaisePropertyChanged(nameof(PostPeakDb));
        RaisePropertyChanged(nameof(InputPeakHoldDb));
        RaisePropertyChanged(nameof(PostPeakHoldDb));
        RaisePropertyChanged(nameof(IsDucking));
        RaisePropertyChanged(nameof(IsAutoMixActive));
        RaisePropertyChanged(nameof(RowState));
        foreach (var r in Routes) r.RefreshSelection();
        RaisePropertyChanged(nameof(MeterFraction));
        RaisePropertyChanged(nameof(CalibrationText));
        foreach (var r in Routes) r.RefreshLed();
        RaisePropertyChanged(nameof(HasDevice));
        RaisePropertyChanged(nameof(IsRoutedAnywhere));
    }

    public void RefreshDevices(IEnumerable<AudioDeviceInfo> devices)
    {
        // Deliberately does NOT clear DesiredDevice*: this path fires when an endpoint vanishes, and
        // that is exactly when the strip most needs to remember what it was bound to.
        if (!DeviceList.Sync(AvailableDevices, devices, SelectedDevice?.Id)) SelectedDevice = null;
    }

    private static float PercentToLinear(float percent) => percent / 100f;
}

public sealed class RouteToggleViewModel : ViewModelBase
{
    /// <summary>Set by the owning channel; vetoes a route being switched OFF. See ChannelViewModel.</summary>
    public Func<int, bool>? Guard { get; set; }

    /// <summary>
    /// Whether the automixer is currently sending THIS mic to THIS bus. Per-output on purpose: with
    /// Gate, one mic is live on A while a different one is live on B, and a single "selected" light
    /// on the row cannot say which. Only MainViewModel can see the engine, so it supplies this.
    /// </summary>
    public Func<int, bool>? IsLeaderOnOutput { get; set; }

    public bool IsSelected => IsLeaderOnOutput?.Invoke(_outputIndex) ?? false;

    public void RefreshSelection() => RaisePropertyChanged(nameof(IsSelected));

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
            // Only switching OFF can uncover a bus. Switching on always adds, and asking there could
            // refuse a change that was about to make things better.
            if (!value && IsOn && Guard != null && !Guard(_outputIndex))
            {
                RaisePropertyChanged();
                return;
            }
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

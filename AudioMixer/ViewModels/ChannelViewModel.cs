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
                RaisePropertyChanged(nameof(DeviceTooltip));
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

    private bool _muted;
    public bool Muted
    {
        get => _muted;
        set
        {
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

    /// <summary>Where speech should sit. VuMeter draws the band around it from its own copy.</summary>
    public const double TargetDb = -24;

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

    private bool _isPriority;
    public bool IsPriority
    {
        get => _isPriority;
        set
        {
            if (SetField(ref _isPriority, value))
            {
                _channel.IsPriority = value;
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
                RaisePropertyChanged(nameof(SourceSuffix));
                RaisePropertyChanged(nameof(SideIndex));
                RaisePropertyChanged(nameof(DeviceTooltip));
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

    // Disambiguates the two strips that share one endpoint, in the device button and the log.
    public string SourceSuffix => _source switch
    {
        ChannelSource.Left => " (L)",
        ChannelSource.Right => " (R)",
        _ => "",
    };

    // A side selection only means something on a stereo endpoint; on a mono mic it is ignored.
    public bool IsStereoCapture => _channel.CaptureChannels >= 2;

    // The strip label is renameable, so it can say "Rode B R" while the strip is bound to something
    // else entirely -- which is exactly how an unnoticed remap survives a whole service. The tooltip
    // names the endpoint actually feeding it, with the side, since two strips routinely share one.
    // A strip whose device has gone away still reports what it is waiting for: that is the durable
    // operator intent (DesiredDeviceName), and "waiting" and "unassigned" need to look different.
    public string DeviceTooltip => _selectedDevice != null
        ? _selectedDevice.FriendlyName + SourceSuffix
        : DesiredDeviceName != null
            ? "Waiting for " + DesiredDeviceName + SourceSuffix
            : "No microphone assigned";

    // Fixed-band high-pass, 0 = off. Removes the rumble/HVAC/handling energy that dominates a
    // DSP-free mic's floor without making any level-dependent decision. Set for every strip at once
    // by the global low-cut in Settings.
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
            }
        }
    }

    /// <summary>Shown on the level slider's tooltip; unity is the normal setting, not a compromise.</summary>
    public string VolumeText => _volumePercent >= 99.5f
        ? "Level: full (normal)"
        : $"Level: {_volumePercent:F0}% — the fader can only attenuate, never boost";

    // A discrete list, not a slider. The cutoff used to be a 0-200 Hz slider with 10 Hz snap ticks:
    // 21 positions in a ~115 px strip column is ~5 px per tick, so which values you could land on
    // depended on pixel rounding as you dragged, and operators reported cutoffs they simply could not
    // select. These are the cutoffs finding 5 actually measured, plus 90 because shipped presets use
    // it. A value outside the list (hand-edited preset) reports index -1 rather than being silently
    // snapped to a neighbour.
    public static readonly int[] HighPassOptions = { 0, 60, 80, 90, 100, 120, 150 };

    public static int HighPassIndexOf(int hz) => Array.IndexOf(HighPassOptions, hz <= 0 ? 0 : hz);

    // Which strip is the lapel: the persisted half of "the priority mic". Set together with
    // IsPriority by MainViewModel.LapelIndex, so the two cannot disagree.
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
    /// <summary>
    /// What to call this strip in a log, a record or a table — the operator's name if they gave one,
    /// otherwise the positional fallback. Written out nine times across three files before this
    /// existed, which is nine chances for one of them to drift.
    /// </summary>
    public string DisplayName => string.IsNullOrWhiteSpace(CustomLabel) ? Label : CustomLabel;

    public bool HasDevice => SelectedDevice != null;
    public bool IsRoutedAnywhere => Routes.Any(r => r.IsOn);

    public RelayCommand ClearDeviceCommand { get; }

    public RouteToggleViewModel[] Routes { get; }

    public float InputPeakDb => _channel.InputPeak.CurrentDb;
    public float PostPeakDb => _channel.PostPeak.CurrentDb;
    public float PostPeakHoldDb => _channel.PostPeak.HoldDb;
    public bool IsAutoMixActive => _channel.IsAutoMixActive;

    // Wires the output strips' (renameable) labels into this channel's route toggles, so the toggle
    // and bus-LED tooltips track the real output names. Called after the outputs exist.
    public void AttachOutputs(OutputViewModel[] outputs)
    {
        for (int o = 0; o < Routes.Length && o < outputs.Length; o++) Routes[o].AttachOutput(outputs[o]);
    }

    /// <summary>
    /// Unhooks every route from its output. Needed when a strip is REMOVED by a count change: the
    /// OutputViewModels outlive the app, so a route left subscribed keeps the discarded strip alive
    /// and responding to bus changes forever. Re-attaching was always safe (AttachOutput unsubscribes
    /// first); only removal leaked.
    /// </summary>
    public void DetachOutputs()
    {
        foreach (var r in Routes) r.DetachOutput();
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

    /// <summary>
    /// Raised 30 times a second per strip, so this list is a running cost and every entry has to earn
    /// its place. It used to raise thirteen names plus two per route; the UI binds four of them.
    ///
    /// The unbound ones were not merely wasted — the meter tick and the autosave debounce share one
    /// PropertyChanged stream, so every name raised here is a name that must stay out of
    /// PersistedProperties or autosave silently stops working (see the gotcha in CLAUDE.md). Fewer
    /// raises is less surface for that.
    ///
    /// IsDucking / IsAutoMixActive / IsRoutedAnywhere are deliberately absent: RowState's getter reads
    /// them itself, so raising RowState already refreshes anything that depends on them.
    /// </summary>
    public void RefreshMeters()
    {
        RaisePropertyChanged(nameof(PostPeakDb));
        RaisePropertyChanged(nameof(PostPeakHoldDb));
        RaisePropertyChanged(nameof(RowState));
        RaisePropertyChanged(nameof(CalibrationText));
        foreach (var r in Routes) r.RefreshSelection();
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

    public string ShortLabel => OutputViewModel.Tag(_outputIndex);
    public string Tooltip => $"Route to {OutputLabel}";

    // The bus's live name (e.g. "MON") when the operator has renamed it, else its letter.
    private string OutputLabel => string.IsNullOrWhiteSpace(_output?.CustomLabel)
        ? $"Output {ShortLabel}"
        : _output!.CustomLabel;

    // IsOn must NOT be raised from the meter tick: it is a persisted property, so a 30 Hz
    // notification would reset the autosave debounce forever (see PersistedProperties). It changes
    // only via the toggle or ApplyPreset, both of which already raise it.
    // Lets the toggle's tooltip follow the (renameable) output label.
    public void AttachOutput(OutputViewModel output)
    {
        if (_output != null) _output.PropertyChanged -= OnOutputChanged;
        _output = output;
        _output.PropertyChanged += OnOutputChanged;
        RaiseLabels();
    }

    public void DetachOutput()
    {
        if (_output != null) _output.PropertyChanged -= OnOutputChanged;
        _output = null;
    }

    private void OnOutputChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OutputViewModel.CustomLabel)) RaiseLabels();
    }

    private void RaiseLabels()
    {
        RaisePropertyChanged(nameof(Tooltip));
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

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using AudioMixer.Audio;
using AudioMixer.Models;
using AudioMixer.Services;
using NAudio.CoreAudioApi;

namespace AudioMixer.ViewModels;

public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private readonly AudioEngine _engine;
    private readonly PresetStore _presetStore = new();
    private readonly DispatcherTimer _meterTimer;
    private readonly DispatcherTimer _autosaveTimer;
    private readonly MixRecorder[] _recorders = new MixRecorder[AudioEngine.OutputCount];
    private StateServer? _stateServer;
    private readonly DiagnosticsLog _diagnostics;
    private readonly SessionRecorder? _session;
    private readonly DeviceWatcher _deviceWatcher;
    private bool _suppressAutosave;
    private bool _suppressRebuild;
    private bool _rebuildInProgress;
    private List<AudioDeviceInfo> _allInputDevices = new();
    private List<AudioDeviceInfo> _allOutputDevices = new();

    public ObservableCollection<ChannelViewModel> Channels { get; } = new();
    public OutputViewModel[] Outputs { get; }

    public RelayCommand RefreshDevicesCommand { get; }
    public RelayCommand RecordCommand { get; }
    public RelayCommand ResyncAudioCommand { get; }
    public RelayCommand DownloadVbCableCommand { get; }
    public RelayCommand DismissVbCablePromptCommand { get; }
    public RelayCommand OpenDocumentationCommand { get; }
    public RelayCommand ResetCalibrationCommand { get; }

    private static string RecordingRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AudioMixer");

    /// <summary>
    /// Recording stops itself after this long. Somebody forgetting to close the app must not mean a
    /// recording that runs until the disk is full, and an hour is comfortably longer than the window
    /// anyone actually reviews afterwards — the point of the capture is to check the selector's
    /// choices, the leveler and clipping, not to archive the service.
    /// </summary>
    public static readonly TimeSpan MaxRecordingLength = TimeSpan.FromHours(1);

    private readonly RecordingRetention _retention = new(
        Path.Combine(RecordingRoot, "analysis"), Path.Combine(RecordingRoot, "recordings"));

    private DateTime _recordingStarted;
    private bool _recording;
    private DecisionTrack? _decisions;
    public bool IsRecording => _recording;
    public string RecordIcon => _recording ? "■" : "●";
    public string RecordTooltip => _recording
        ? "Stop recording"
        : "Record everything — every microphone to its own file, and every bus";

    private const string VbCableUrl = "https://vb-audio.com/Cable/";
    private const string DocsUrl = "https://github.com/wimkerkhoff/audio-mixer";
    private bool _vbCableInstalled;
    private bool _vbCablePromptDismissed;

    // Shown when VB-CABLE isn't among the enumerated endpoints and the user hasn't dismissed the hint.
    public bool ShowVbCablePrompt => !_vbCableInstalled && !_vbCablePromptDismissed;

    private string _statusText = "Idle";
    public string StatusText
    {
        get => _statusText;
        set
        {
            if (!SetField(ref _statusText, value)) return;
            // Transient for the operator panel. A status line that permanently reads "Output B:
            // Speakers (Lync USB Headset)" is a startup confirmation nobody asked for occupying the
            // one place a real message — a refused route, a mic that dropped — has to appear.
            // Advanced keeps the persistent line; the panel only shows what was just said.
            _statusShown = true;
            RaisePropertyChanged(nameof(TransientStatus));
            RaisePropertyChanged(nameof(HasTransientStatus));
            _statusFade.Stop();
            _statusFade.Start();
        }
    }

    private bool _statusShown;
    private readonly DispatcherTimer _statusFade = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromSeconds(6),
    };

    public string TransientStatus => _statusShown ? _statusText : "";
    public bool HasTransientStatus => _statusShown && !string.IsNullOrWhiteSpace(_statusText);

    public int[] InputCountOptions { get; } = Enumerable.Range(
        AudioEngine.MinInputCount, AudioEngine.MaxInputCount - AudioEngine.MinInputCount + 1).ToArray();

    private int _inputCount = AudioEngine.DefaultInputCount;
    public int InputCount
    {
        get => _inputCount;
        set
        {
            int clamped = Math.Clamp(value, AudioEngine.MinInputCount, AudioEngine.MaxInputCount);
            if (clamped == _inputCount)
            {
                if (value != clamped) RaisePropertyChanged();
                return;
            }
            ApplyInputCount(clamped);
            _inputCount = clamped;
            RaisePropertyChanged();
            QueueAutosave();
        }
    }

    // Per-strip allowance + the 230 px output column + window chrome. A UniformGrid divides its
    // column equally and IGNORES each child's MinWidth, so this number is the ONLY thing keeping the
    // strips legible — too small and the right-most controls clip silently, A/B route toggles first.
    // Measured at 10 inputs 2026-09-20: the old `count * 96 + 240` produced a 1200 px window whose
    // client area is ~1184, leaving (1184 - 230) / 10 = 95.4 px per strip and 85.4 px of content
    // against the strip's MinWidth of 86 — clipping by a hair because the border was never counted.
    public MainViewModel()
    {
        _engine = new AudioEngine();
        _engine.InputRestarted += (idx, attempt) => RunOnUi(() =>
            StatusText = $"Input {idx + 1} dropped — auto-restarted (attempt {attempt}).");
        _engine.InputRestartGaveUp += idx => RunOnUi(() =>
            StatusText = $"Input {idx + 1} not responding — re-pick the device or click Resync.");

        _allInputDevices = AudioDeviceInfo.Enumerate(DataFlow.Capture);
        _allOutputDevices = AudioDeviceInfo.Enumerate(DataFlow.Render);

        // Without this nothing ever notices a receiver being unplugged or plugged back in, so the
        // reattach-on-replug logic could never run and the operator remapped by hand every time.
        _deviceWatcher = new DeviceWatcher();
        _deviceWatcher.DevicesChanged += () => RunOnUi(() =>
        {
            RefreshDevices();
            StatusText = "Audio devices changed — rechecked.";
        });

        for (int i = 0; i < _engine.InputCount; i++)
        {
            Channels.Add(CreateChannel(i));
        }
        _inputCount = Channels.Count;
        Channels[0].Routes[0].IsOn = true;
        if (Channels[0].Routes.Length > 1) Channels[0].Routes[1].IsOn = true;

        Outputs = new OutputViewModel[AudioEngine.OutputCount];
        for (int o = 0; o < AudioEngine.OutputCount; o++)
        {
            _recorders[o] = new MixRecorder();
            Outputs[o] = new OutputViewModel(
                o, _engine.Outputs[o], _engine, _allOutputDevices,
                (idx, dev) => SetOutputDevice(idx, dev));
        }

        RefreshDevicesCommand = new RelayCommand(RefreshDevices);
        RecordCommand = new RelayCommand(ToggleRecording);
        ResyncAudioCommand = new RelayCommand(ResyncAudio);
        DownloadVbCableCommand = new RelayCommand(OpenVbCableDownload);
        DismissVbCablePromptCommand = new RelayCommand(DismissVbCablePrompt);
        OpenDocumentationCommand = new RelayCommand(() => OpenUrl(DocsUrl));
        ResetCalibrationCommand = new RelayCommand(ResetCalibration);
        ApplyFixCommand = new RelayCommand<HealthAlert>(ApplyFix, a => a.Fix != FixKind.None);

        _statusFade.Tick += (_, _) =>
        {
            _statusFade.Stop();
            _statusShown = false;
            RaisePropertyChanged(nameof(TransientStatus));
            RaisePropertyChanged(nameof(HasTransientStatus));
        };

        UpdateVbCableStatus();

        _autosaveTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _autosaveTimer.Tick += (_, _) =>
        {
            _autosaveTimer.Stop();
            SavePreset();
        };

        foreach (var ch in Channels) AttachChannel(ch);
        foreach (var op in Outputs) op.PropertyChanged += OnSettingChanged;

        InitScenesAndHealth();
        for (int o = 0; o < _lastOutputSound.Length; o++) _lastOutputSound[o] = Environment.TickCount64;

        _meterTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(33),
        };
        _diagnostics = new DiagnosticsLog(_engine, Channels, Outputs);
        // Not gated on AudioLog.Enabled, unlike the diagnostics log: the session that matters is the
        // one nobody thought to prepare for. Replay is excluded — it is a sandbox, not a service.
        _meterTimer.Tick += (_, _) =>
        {
            foreach (var ch in Channels) ch.RefreshMeters();
            foreach (var op in Outputs) op.RefreshMeters();
            _diagnostics.Tick();
            _session?.Tick();
            CheckRecordingLimits();
            _decisions?.Sample(
                o => _engine.AutoMixActiveInput(o),
                // Clamped as well as stopped above: the tick runs on a timer and must never be able to
                // index past a list that shrank under it, whatever else changes.
                // The smoothed RMS the selector actually compares, NOT the peak: with this rig's
                // 20-45 dB crest the two are nowhere near each other, and the whole point of the
                // column is lining it up against PriorityActiveRms and SilenceFloorRms. Same
                // conversion /state uses for envDb.
                i => i < _engine.Inputs.Length ? Db(_engine.Inputs[i].CurrentLevelLinear) : -120.0,
                (i, o) => i < _engine.Inputs.Length ? _engine.Inputs[i].GetAutoMixGain(o) : 1f,
                o => Outputs[o].LevelerGainDb,
                Scenes.Current?.ToString() ?? "Custom");
            RefreshHealth();
        };
        _meterTimer.Start();

        // Records by default. The service nobody prepared for is the one worth having, which is the
        // same argument as the session record — except audio is four orders of magnitude bigger, so it
        // comes with a length cap and a disk guard rather than only an age rule. Delayed so devices
        // have bound: starting at construction would record whichever strips happened to be ready.
        if (!_isReplaying)
        {
            var autoRecord = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(12),
            };
            autoRecord.Tick += (_, _) =>
            {
                autoRecord.Stop();
                if (!_recording) ToggleRecording();
            };
            autoRecord.Start();
        }

        TryLoadInitialPreset();

        // AFTER the preset: SessionAggregator sizes itself from Channels.Count, and before the preset
        // that is still DefaultInputCount = 3. On the six-transmitter rig every session record showed
        // inputs 4-6 with zero leader, ducked and muted time — plausible-looking and wrong. Replay is
        // excluded: it is a sandbox, not a service.
        if (!_isReplaying)
        {
            _session = new SessionRecorder(_engine, Channels, Outputs) { Config = BuildSessionConfig };
            Scenes.OnOperatorAction = what => _session?.Action(what);
        }

        StartReplayIfRequested();
        // After the preset, so a scene overrides saved state rather than the other way round.
        if (App.StartupScene is { } scene) Scenes.Apply(scene);
        StartStateServer();
    }

    // --- Scenes and health (Simple mode) -------------------------------------------------------

    public SceneController Scenes { get; private set; } = null!;

    public RelayCommand StandbyCommand { get; private set; } = null!;
    public RelayCommand TeachingCommand { get; private set; } = null!;
    public RelayCommand PrayerCommand { get; private set; } = null!;
    public RelayCommand SingingCommand { get; private set; } = null!;
    public RelayCommand UseLapelCommand { get; private set; } = null!;
    public RelayCommand UseRoomMicsCommand { get; private set; } = null!;

    /// <summary>
    /// Does the thing an alert suggests. Until 2026-09-21 every suggested fix was a label, which for a
    /// volunteer running the service alone is the same as no advice at all — and an alert nobody can
    /// act on teaches people to skim the window that will one day matter.
    /// </summary>
    public RelayCommand<HealthAlert> ApplyFixCommand { get; }

    /// <summary>
    /// Two fixes need a window, and windows are not the view model's to open. SimpleWindow subscribes
    /// and owns the Show/Activate, which keeps the fix dispatcher testable.
    /// </summary>
    public event Action<FixKind>? FixNeedsWindow;

    public ObservableCollection<HealthAlert> Alerts { get; } = new();

    /// <summary>
    /// The panel carries no banner, so this badge is the only thing that says something is wrong —
    /// it has to show how many and how bad, not merely that the window exists.
    /// </summary>
    public int AlertCount => Alerts.Count;

    public string AlertBadgeState => Alerts.Count == 0 ? "clear"
        : Alerts.Any(a => a.Severity == AlertSeverity.Critical) ? "bad" : "warn";

    public string AlertBadgeText => Alerts.Count == 0 ? "checks" : $"◎ {Alerts.Count}";

    // --- Diagnostics: Session and Devices tabs ---------------------------------------------------

    /// <summary>
    /// The session so far, built on demand. Live for the running service; the same shape that gets
    /// written to disk, so the tab reads a finished record and a running one identically.
    /// </summary>
    public SessionSummary? SessionSnapshot => _session?.BuildSummary();

    /// <summary>
    /// The rig as configured, for the session record. Without it a saved session cannot be read:
    /// "12 hand-offs a minute" means one thing under Gate and another under Off, and "this mic never
    /// won" is expected if it was never routed.
    /// </summary>
    private SessionConfig BuildSessionConfig() => new()
    {
        LowCutHz = _lowCutHz,
        Lapel = Channels.FirstOrDefault(c => c.IsLapel)?.CustomLabel,
        Inputs = Channels.Select(c =>
            $"{(string.IsNullOrWhiteSpace(c.CustomLabel) ? c.Label : c.CustomLabel)} | " +
            $"{c.SelectedDevice?.FriendlyName ?? "(none)"} | {c.Source} | " +
            $"routes {string.Join("", c.Routes.Select((r, i) => r.IsOn ? OutputViewModel.Tag(i) : "-"))}" +
            (c.Muted ? " | muted" : "") + (c.IsPriority ? " | priority" : "") +
            (c.VolumePercent < 99.5f ? $" | level {c.VolumePercent:F0}%" : "")).ToList(),
        Outputs = Outputs.Select(o =>
            $"{OutputViewModel.Tag(o.Index)} | {o.SelectedDevice?.FriendlyName ?? "(none)"} | " +
            $"{o.AutoMixModeOptions[Math.Clamp(o.AutoMixModeIndex, 0, o.AutoMixModeOptions.Length - 1)]} | " +
            $"leveler {(o.LevelerEnabled ? o.LevelerStrength.ToString() : "off")}" +
            (o.VolumePercent < 99.5f ? $" | volume {o.VolumePercent:F0}%" : "")).ToList(),
    };

    /// <summary>Saved records, newest first. Empty until a service has run for a minute.</summary>
    public IReadOnlyList<SessionFile> PastSessions => new SessionStore().List();

    public sealed record DeviceRow(
        string Endpoint, string Bus, string Identity, string Gain, string BoundTo, string Side,
        string State);

    /// <summary>
    /// Every capture endpoint with the facts that decide whether it will still be here next week:
    /// the bus type (BTHENUM is the only value that means Bluetooth) and whether a strip holds it.
    /// </summary>
    public IReadOnlyList<DeviceRow> DeviceRows()
    {
        var rows = new List<DeviceRow>();
        foreach (var d in _allInputDevices)
        {
            var holder = Channels.FirstOrDefault(c => c.SelectedDevice?.Id == d.Id);
            string state = holder == null ? "free"
                : Environment.TickCount64 - _engine.Inputs[holder.Index].LastDataTicks > 2000 ? "no signal"
                : "capturing";
            rows.Add(new DeviceRow(
                d.FriendlyName,
                d.Bus ?? "—",
                DeviceIdentity.Describe(DeviceIdentity.Classify(d.ContainerId, d.Bus)),
                GainTextFor(d),
                holder == null ? "—" : (string.IsNullOrWhiteSpace(holder.CustomLabel) ? holder.Label : holder.CustomLabel),
                holder?.Source.ToString() ?? "—",
                state));
        }
        foreach (var o in Outputs.Where(o => o.SelectedDevice != null))
        {
            rows.Add(new DeviceRow(
                o.SelectedDevice!.FriendlyName, o.SelectedDevice.Bus ?? "—",
                DeviceIdentity.Describe(DeviceIdentity.Classify(o.SelectedDevice.ContainerId,
                                                                o.SelectedDevice.Bus)),
                "—", $"Bus {OutputViewModel.Tag(o.Index)} out", "—", "playing"));
        }
        return rows;
    }

    private static string GainTextFor(AudioDeviceInfo d)
    {
        // Endpoint gain is keyed to the endpoint, so it resets on a port change — worth showing
        // precisely because it is invisible everywhere else and has bitten this rig twice.
        try
        {
            using var dev = d.Resolve();
            return dev == null ? "—" : $"{dev.AudioEndpointVolume.MasterVolumeLevel:F1} dB";
        }
        catch { return "—"; }
    }

    // --- the lapel, as a single choice ------------------------------------------------------------

    /// <summary>
    /// Which channel is the presenter's lapel, as one selection rather than a set of checkboxes.
    /// Operator's call 2026-09-20: only one input is ever the lapel on this rig.
    ///
    /// This sets ROLE, not IsPriority. Role is what scenes read — it has to survive Prayer clearing
    /// the priority flag, which is why it is a property of the mic rather than of the current setup.
    /// Setting priority on several channels at once is still possible from the per-input settings,
    /// so the old pastor-plus-worship-leader case is awkward but not lost.
    /// </summary>
    public IReadOnlyList<string> LapelOptions =>
        new[] { "(none)" }.Concat(Channels.Select(c =>
            string.IsNullOrWhiteSpace(c.CustomLabel) ? c.Label : c.CustomLabel)).ToList();

    public int LapelIndex
    {
        get
        {
            var lapel = Channels.FirstOrDefault(c => c.IsLapel);
            return lapel == null ? 0 : lapel.Index + 1;
        }
        set
        {
            // Exclusive by construction: one channel becomes the lapel, every other becomes a room mic.
            // Priority IS the lapel now (operator, 2026-09-20: only one mic is ever priority). Role
            // and priority were two controls for one idea, and the separate checkbox was the one that
            // could be left armed on an unused mic — the documented hazard where a bumped lapel
            // silently ducks every room mic off the stream. Scenes still clear priority where they
            // must: Prayer mutes and de-prioritises the lapel outright.
            for (int i = 0; i < Channels.Count; i++)
            {
                bool isLapel = i == value - 1;
                Channels[i].IsLapel = isLapel;
                Channels[i].IsPriority = isLapel;
            }
            RaisePropertyChanged();
            // LapelOptions is NOT re-raised here. It lists the strips' names and does not depend on
            // which one is the lapel, so replacing the ComboBox's ItemsSource mid-set achieves
            // nothing — and it can push SelectedIndex back as -1 through the TwoWay binding, which
            // un-picks the lapel the operator just chose. Renames raise it instead, from
            // OnSettingChanged, which is the case that actually changes the list.
        }
    }

    public string ChecksHeadline
    {
        get
        {
            int bad = Alerts.Count(a => a.Severity == AlertSeverity.Critical);
            int warn = Alerts.Count - bad;
            if (Alerts.Count == 0) return "Everything is ready";
            if (bad == 0) return warn == 1 ? "1 warning" : $"{warn} warnings";
            if (warn == 0) return bad == 1 ? "1 problem" : $"{bad} problems";
            return $"{bad} problem{(bad == 1 ? "" : "s")}, {warn} warning{(warn == 1 ? "" : "s")}";
        }
    }
    private readonly HashSet<string> _dismissedAlerts = new();

    private void InitScenesAndHealth()
    {
        Scenes = new SceneController(Channels, Outputs);
        // A scene claim must not outlive a hand-edit, or the Simple-mode pill lies about what is live.
        Scenes.SceneApplied += _ => { _autosaveTimer.Stop(); _autosaveTimer.Start(); };

        StandbyCommand = new RelayCommand(() => Scenes.Apply(Models.Scene.Standby));
        TeachingCommand = new RelayCommand(() => Scenes.Apply(Models.Scene.Teaching));
        PrayerCommand = new RelayCommand(() => Scenes.Apply(Models.Scene.Prayer));
        SingingCommand = new RelayCommand(() => Scenes.Apply(Models.Scene.Singing));
        UseLapelCommand = new RelayCommand(() => Scenes.VoiceSource = Models.VoiceSource.Lapel);
        UseRoomMicsCommand = new RelayCommand(() => Scenes.VoiceSource = Models.VoiceSource.RoomMics);
    }

    // --- Settings-window options ----------------------------------------------------------------
    // Persisted: a picker filter the operator has to re-tick on every launch is not a setting. They
    // are in PersistedProperties, so changing one triggers the autosave debounce like any other.

    private bool _hideVirtualInputs;
    public bool HideVirtualInputs
    {
        get => _hideVirtualInputs;
        set { if (SetField(ref _hideVirtualInputs, value)) { RebuildAvailableDevices(); QueueAutosave(); } }
    }

    private bool _hideVoicemeeterOutputs;
    public bool HideVoicemeeterOutputs
    {
        get => _hideVoicemeeterOutputs;
        set { if (SetField(ref _hideVoicemeeterOutputs, value)) { RebuildAvailableDevices(); QueueAutosave(); } }
    }

    /// <summary>
    /// The low-cut, for all microphones at once. Per-input was a distinction nobody was actually
    /// using: the values that had accumulated across the strips were accidental, and every mic here
    /// is a voice in one room with one HVAC floor. 80 Hz removes rumble and handling while sitting
    /// below congregational singing, whose fundamentals start around 98 Hz — and it is a fixed band,
    /// not a gate, so unlike the speakerphones it cannot punch holes in sustained material.
    /// </summary>
    public int[] LowCutOptions { get; } = ChannelViewModel.HighPassOptions;

    /// <summary>80 Hz: rumble, handling and headroom. Never an S/N fix — finding 5 measured a
    /// high-pass moving speech-band S/N by 0.1-0.2 dB.</summary>
    public const int DefaultLowCutHz = 80;

    private int _lowCutHz = DefaultLowCutHz;
    public int LowCutHz
    {
        get => _lowCutHz;
        set
        {
            if (!SetField(ref _lowCutHz, value)) return;
            foreach (var c in Channels) c.HighPassHz = value;
            RaisePropertyChanged(nameof(LowCutIndex));
            QueueAutosave();
        }
    }

    public int LowCutIndex
    {
        get => Math.Max(0, ChannelViewModel.HighPassIndexOf(_lowCutHz));
        set { if (value >= 0 && value < LowCutOptions.Length) LowCutHz = LowCutOptions[value]; }
    }

    private bool _warnOnBluetoothMics = true;
    public bool WarnOnBluetoothMics
    {
        get => _warnOnBluetoothMics;
        set { if (SetField(ref _warnOnBluetoothMics, value)) { RefreshHealth(force: true); QueueAutosave(); } }
    }

    // MainViewModel's own settings do not pass through OnSettingChanged (that is subscribed to the
    // strips and gated by PersistedProperties), so they restart the debounce themselves.
    private void QueueAutosave()
    {
        if (_suppressAutosave) return;
        _autosaveTimer.Stop();
        _autosaveTimer.Start();
    }

    public string DiagnosticsSummary =>
        $"Log: {(AudioLog.Enabled ? "on (%TEMP%\\AudioMixer.log)" : "off — relaunch with --log")}\n" +
        $"State endpoint: {(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AUDIOMIXER_STATE")) ? "off — relaunch with --state" : $"http://127.0.0.1:{Environment.GetEnvironmentVariable("AUDIOMIXER_STATE")}/state")}\n" +
        $"Binding errors: {Services.BindingErrorListener.ErrorCount}\n" +
        (IsReplaying ? ReplaySessionLabel : "Live capture");

    private long _lastHealthTicks;

    // Evaluated at ~1 Hz rather than on every meter tick: the alert set is stable on that timescale,
    // and re-raising a collection 30x/second would churn the UI for nothing.
    // The Bluetooth rule and its remediation text are written for the Anker speakerphones, which had
    // to run over their Soundsync dongles. On a rig without them the advice is wrong — and the rule
    // matches on a bare "Headset" substring, so any endpoint named that way trips it. Filtering by Id
    // here rather than gating HealthMonitor keeps that pure evaluator (and its tests) untouched, and
    // makes the Settings checkbox real: it was bound to the UI and read by nothing.
    private bool IsAlertWanted(HealthAlert alert) =>
        _warnOnBluetoothMics || !alert.Id.EndsWith(".bluetooth", StringComparison.Ordinal);

    private void RefreshHealth(bool force = false)
    {
        long now = Environment.TickCount64;
        if (!force && now - _lastHealthTicks < 1000) return;
        _lastHealthTicks = now;

        var snapshot = BuildHealthSnapshot(now);
        var fresh = HealthMonitor.Evaluate(snapshot)
            .Where(a => !_dismissedAlerts.Contains(a.Id))
            .Where(IsAlertWanted)
            .ToList();

        // An alert that clears becomes dismissible again, so a recurrence is not silently swallowed.
        var liveIds = fresh.Select(a => a.Id).ToHashSet();
        _dismissedAlerts.RemoveWhere(id => !HealthMonitor
            .Evaluate(snapshot).Any(a => a.Id == id));

        // Before the early return: the scene is not an alert, and hanging it off this comparison
        // meant a session that ran cleanly (no alert ever changing) recorded whatever scene happened
        // to be live at the first health tick.
        if (_session != null) _session.Scene = Scenes.Current?.ToString();

        if (fresh.Count == Alerts.Count && fresh.Zip(Alerts).All(p => p.First == p.Second)) return;

        Alerts.Clear();
        foreach (var a in fresh) Alerts.Add(a);
        if (_session != null)
        {
            _session.Scene = Scenes.Current?.ToString();
            _session.Note(fresh);
        }
        RaisePropertyChanged(nameof(AlertCount));
        RaisePropertyChanged(nameof(AlertBadgeState));
        RaisePropertyChanged(nameof(AlertBadgeText));
        RaisePropertyChanged(nameof(ChecksHeadline));
    }

    private HealthSnapshot BuildHealthSnapshot(long now)
    {
        var channels = new List<ChannelHealth>(Channels.Count);
        for (int i = 0; i < Channels.Count; i++)
        {
            var vm = Channels[i];
            var input = _engine.Inputs[i];
            var cal = input.SnapshotCalibration();
            channels.Add(new ChannelHealth(
                i,
                string.IsNullOrWhiteSpace(vm.CustomLabel) ? $"Input {i + 1}" : vm.CustomLabel,
                vm.Role,
                vm.SelectedDevice?.FriendlyName,
                vm.Routes.Any(r => r.IsOn),
                vm.Muted,
                vm.IsPriority,
                vm.InputPeakDb,
                (now - input.LastDataTicks) / 1000.0,
                (now - input.LastSoundTicks) / 1000.0,
                vm.SelectedDevice?.Bus,
                vm.SelectedDevice?.Id,
                (int)vm.Source,
                cal.SpeechDb,
                cal.IsStale));
        }

        var outputs = new List<OutputHealth>(Outputs.Length);
        for (int o = 0; o < Outputs.Length; o++)
        {
            var vm = Outputs[o];
            if (vm.OutputPeakDb > -80) _lastOutputSound[o] = now;
            outputs.Add(new OutputHealth(
                o,
                string.IsNullOrWhiteSpace(vm.CustomLabel) ? OutputViewModel.Tag(o) : vm.CustomLabel,
                vm.SelectedDevice != null,
                vm.Muted,
                vm.OutputPeakDb,
                (now - _lastOutputSound[o]) / 1000.0,
                vm.VolumePercent,
                vm.SelectedDevice == null || vm.IsPlaying));
        }

        return new HealthSnapshot(Scenes.Current, channels, outputs, IsReplaying);
    }

    private readonly long[] _lastOutputSound = new long[AudioEngine.OutputCount];

    // --- Diagnostics ("why this mic?") ----------------------------------------------------------

    public ObservableCollection<DiagnosticRow> DiagnosticRows { get; } = new();

    /// <summary>
    /// Rebuilds the ranked selection table. Driven by the Diagnostics window's own 10 Hz timer rather
    /// than the meter tick, so it costs nothing when that window is closed.
    /// </summary>
    // Clear every input's speech/floor histogram. The readings are cumulative on purpose (a settling
    // number is what makes gain setting a matching exercise), so they must be cleared by hand after
    // changing a transmitter's gain — otherwise the pre-change buffers keep dragging the median.
    private void ResetCalibration()
    {
        foreach (var input in _engine.Inputs) input.ResetCalibration();
        StatusText = "Calibration histograms cleared.";
        _session?.Action("calibration reset");
    }

    /// <summary>
    /// Carries out an alert's suggested fix. Every branch is a change the operator could have made by
    /// hand; nothing here decides anything the rules did not already decide, which is what keeps the
    /// judgement in the pure layer.
    ///
    /// Recorded as an operator action, because from the session record's point of view clicking the
    /// suggestion IS an intervention — and step 8 of the review asks whether each one helped.
    /// </summary>
    private void ApplyFix(HealthAlert alert)
    {
        var ch = alert.Target >= 0 && alert.Target < Channels.Count ? Channels[alert.Target] : null;
        var op = alert.Target >= 0 && alert.Target < Outputs.Length ? Outputs[alert.Target] : null;

        RunGuarded("Fix", () =>
        {
            switch (alert.Fix)
            {
                case FixKind.Unmute:
                    if (op == null) return;
                    op.Muted = false;
                    StatusText = $"{op.TaggedLabel} unmuted.";
                    break;

                case FixKind.RaiseVolume:
                    if (op == null) return;
                    op.VolumePercent = 100f;
                    StatusText = $"{op.TaggedLabel} turned up to 100%.";
                    break;

                case FixKind.ClearPriority:
                    if (ch == null) return;
                    ch.IsPriority = false;
                    StatusText = $"{Label(ch)} is no longer the priority mic.";
                    break;

                case FixKind.RouteToBuses:
                    // Putting a mic back ON air only ever adds, so RouteGuard has nothing to refuse.
                    if (ch == null) return;
                    foreach (var r in ch.Routes) r.IsOn = true;
                    StatusText = $"{Label(ch)} routed to every bus.";
                    break;

                case FixKind.SplitSides:
                    ApplySplit(ch);
                    break;

                case FixKind.ResetCalibration:
                    if (ch == null) { ResetCalibration(); return; }
                    _engine.Inputs[ch.Index].ResetCalibration();
                    StatusText = $"{Label(ch)}'s calibration cleared — it will settle again as it is used.";
                    break;

                case FixKind.Resync:
                    ResyncAudioCommand.Execute(null);
                    return;   // Resync writes its own status and action

                case FixKind.ReapplyScene:
                    if (Scenes.Current is not Models.Scene scene) return;
                    Scenes.Apply(scene);
                    StatusText = $"{scene} re-applied.";
                    break;

                case FixKind.OpenSettings:
                case FixKind.OpenDiagnostics:
                    FixNeedsWindow?.Invoke(alert.Fix);
                    return;   // opening a window is not an intervention worth recording

                default:
                    return;
            }
            _session?.Action($"fix: {alert.Id}");
        });
    }

    /// <summary>
    /// Two strips on one receiver, both Stereo, each carrying the same blend. Set the first to Left and
    /// the second to Right — which is the ONLY correct answer, since a Wireless PRO in Split mode puts
    /// TX1 on the left and TX2 on the right of the single endpoint.
    /// </summary>
    private void ApplySplit(ChannelViewModel? first)
    {
        if (first?.SelectedDevice == null) return;
        var pair = Channels
            .Where(c => c.SelectedDevice?.Id == first.SelectedDevice.Id
                     && c.Source == Audio.ChannelSource.Stereo)
            .OrderBy(c => c.Index)
            .Take(2)
            .ToList();
        if (pair.Count < 2) return;

        pair[0].Source = Audio.ChannelSource.Left;
        pair[1].Source = Audio.ChannelSource.Right;
        StatusText = $"{Label(pair[0])} set to Left, {Label(pair[1])} to Right.";
    }

    public void RefreshDiagnostics()
    {
        var diag = _engine.AutoMixSnapshot();

        // Ranked by level, which is the only thing the selector decides on now — the correlation and
        // flux-CV columns went with the selectors that used them.
        var order = Enumerable.Range(0, Channels.Count)
            .Where(i => Channels[i].HasDevice && !Channels[i].Muted && Channels[i].IsRoutedAnywhere)
            .OrderByDescending(i => i < diag.Env.Length ? diag.Env[i] : 0f)
            .ToList();

        var rows = new List<DiagnosticRow>(Channels.Count);
        for (int i = 0; i < Channels.Count; i++)
        {
            int rank = order.IndexOf(i);
            rows.Add(DiagnosticRow.Build(i, Channels[i], diag, _engine.Inputs[i],
                AudioEngine.OutputCount, rank < 0 ? 0 : rank + 1));
        }

        // Rebuild in place: replacing the collection would drop the ItemsControl's scroll position.
        while (DiagnosticRows.Count > rows.Count) DiagnosticRows.RemoveAt(DiagnosticRows.Count - 1);
        for (int i = 0; i < rows.Count; i++)
        {
            if (i < DiagnosticRows.Count) DiagnosticRows[i] = rows[i];
            else DiagnosticRows.Add(rows[i]);
        }

        foreach (var o in Outputs) o.RefreshVerdict(diag, Channels);
    }

    // --replay: drive the inputs from a recorded session instead of live mics. Runs after the preset
    // load so routes/modes/priority flags carry over — the point is to replay a session *through* the
    // operator's real configuration.
    private void StartReplayIfRequested()
    {
        var opts = Audio.Replay.ReplayOptions.Current;
        if (opts == null) return;

        // Never let a sandbox instance open the operator's outputs — two processes both writing CABLE
        // Input would double audio into Zoom. The operator picks an output by hand to listen.
        if (opts.SuppressOutputDevices)
            foreach (var op in Outputs) op.SelectedDevice = null;

        RunGuarded("Replay", () =>
        {
            var rig = Audio.Replay.ReplayRig.Open(opts.Directory, opts.Stamp);
            rig.Speed = Audio.Replay.ReplayOptions.Speed;
            rig.Loop = Audio.Replay.ReplayOptions.Loop;
            if (Audio.Replay.ReplayOptions.Seek > TimeSpan.Zero) rig.Seek(Audio.Replay.ReplayOptions.Seek);
            _engine.StartReplay(rig);

            // --for: a batch run must exit on its own so a harness can wait on the process.
            var runFor = Audio.Replay.ReplayOptions.Duration;
            if (runFor > TimeSpan.Zero)
            {
                var stopAt = rig.Position + runFor;
                var watch = new DispatcherTimer(DispatcherPriority.Background)
                { Interval = TimeSpan.FromMilliseconds(200) };
                watch.Tick += (_, _) =>
                {
                    if (rig.Position < stopAt) return;
                    watch.Stop();
                    Application.Current?.Shutdown();
                };
                watch.Start();
            }
            rig.ReachedEnd += () => RunOnUi(() =>
            {
                StatusText = $"Replay finished — {rig.Stamp}";
                if (!rig.Loop && Audio.Replay.ReplayOptions.Duration > TimeSpan.Zero)
                    Application.Current?.Shutdown();
            });
            IsReplaying = true;
            ReplaySessionLabel = $"REPLAY {rig.Stamp} · {rig.InputCount} inputs · {rig.Duration:hh\\:mm\\:ss}";
            StatusText = ReplaySessionLabel;
        });
    }

    private bool _isReplaying;
    public bool IsReplaying
    {
        get => _isReplaying;
        private set => SetField(ref _isReplaying, value);
    }

    private string _replaySessionLabel = "";
    public string ReplaySessionLabel
    {
        get => _replaySessionLabel;
        private set => SetField(ref _replaySessionLabel, value);
    }

    // Loopback JSON state endpoint for diagnostics — opt-in via AUDIOMIXER_STATE (a port number, or
    // any non-empty value for the default 7077). Read-only; see StateServer.
    private void StartStateServer()
    {
        var env = Environment.GetEnvironmentVariable("AUDIOMIXER_STATE");
        if (string.IsNullOrEmpty(env)) return;
        int port = int.TryParse(env, out var p) ? p : 7077;
        try
        {
            _stateServer = new StateServer(port, SnapshotJson);
            _stateServer.Start();
            AudioLog.Write($"State server listening on http://127.0.0.1:{port}/state");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"State server failed to start: {ex}");
            AudioLog.Write($"State server failed to start on port {port}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Called on the server's background thread — marshal to the UI thread to read VM state coherently.
    private string SnapshotJson()
    {
        var disp = Application.Current?.Dispatcher;
        return disp == null || disp.CheckAccess() ? BuildStateJson() : disp.Invoke(BuildStateJson);
    }

    private string BuildStateJson() =>
        StateSnapshot.Build(_engine, Channels, Outputs, InputCount, StatusText,
            Scenes.Current?.ToString(), Alerts);

    private ChannelViewModel CreateChannel(int index)
    {
        var ch = new ChannelViewModel(
            index, _engine.Inputs[index], _allInputDevices, AudioEngine.OutputCount,
            (idx, dev) => SetInputDevice(idx, dev));
        // The low-cut is global, so a strip added at runtime must not come up at 0 Hz while every
        // other mic is filtered — one unfiltered mic on the bus is all it takes to put the room's
        // rumble back on the stream.
        ch.HighPassHz = _lowCutHz;
        return ch;
    }

    private void AttachChannel(ChannelViewModel ch)
    {
        ch.PropertyChanged += OnSettingChanged;
        foreach (var r in ch.Routes) r.PropertyChanged += OnSettingChanged;
        ch.AttachOutputs(Outputs);

        // The operator panel can switch routes and mute directly, which is a path straight around the
        // invariant SceneTransform's tests protect. Only this class can see the sibling channels the
        // decision depends on, so the guard is wired from here.
        ch.MuteGuard = i => Allow(Services.RouteGuard.CheckMute(RoutingSnapshot(), i));
        ch.RouteGuard = (i, o) => Allow(Services.RouteGuard.CheckUnroute(RoutingSnapshot(), i, o));
        for (int o = 0; o < ch.Routes.Length; o++)
        {
            int output = o;
            ch.Routes[o].Guard = _ => Allow(
                Services.RouteGuard.CheckUnroute(RoutingSnapshot(), ch.Index, output));
            ch.Routes[o].IsLeaderOnOutput = out_ => _engine.AutoMixActiveInput(out_) == ch.Index;
        }
    }

    private bool Allow(RouteVerdict verdict)
    {
        // A scene rewrites every channel at once, and Write walks them in index order, so the guard
        // sees torn intermediate states: applying Prayer from Singing(Lapel) muted the lapel FIRST,
        // while it was still the only cover on both buses, and the guard refused — leaving the lapel
        // routed and unmuted with "Bus A would have no microphone" on the status line. The scene's own
        // end state is guaranteed correct by SceneTransform (which is tested); the guard exists for
        // the operator panel's direct mute/route toggles, which have no such plan.
        if (Scenes.IsApplying) return true;
        if (verdict.Allowed) return true;
        StatusText = verdict.Reason!;
        return false;
    }

    /// <summary>Linear RMS to dBFS, floored the way /state floors it so silence is finite.</summary>
    private static double Db(double linear) =>
        linear <= 1e-6 ? -120.0 : Math.Round(20 * Math.Log10(linear), 1);

    private List<ChannelRouting> RoutingSnapshot() =>
        Channels.Select(c => new ChannelRouting(
            c.Index,
            string.IsNullOrWhiteSpace(c.CustomLabel) ? c.Label : c.CustomLabel,
            c.Routes.Select(r => r.IsOn).ToArray(),
            c.Muted,
            c.SelectedDevice != null,
            Environment.TickCount64 - _engine.Inputs[c.Index].LastDataTicks > 2000)).ToList();

    private void DetachChannel(ChannelViewModel ch)
    {
        ch.PropertyChanged -= OnSettingChanged;
        foreach (var r in ch.Routes) r.PropertyChanged -= OnSettingChanged;
        // The outputs outlive every strip, so a route left subscribed to one keeps this discarded
        // strip alive and reacting for the rest of the session.
        ch.DetachOutputs();
    }

    private void ApplyInputCount(int count)
    {
        // Recording is always on, so this is the NORMAL state, and the decisions CSV fixes its column
        // count when it opens: shrinking the strip count left the meter tick indexing Channels past
        // the end 33 ms later, throwing on the dispatcher. App's handler records the crash but does
        // not mark it handled, so the process exited mid-service. Stopping first also gives every
        // removed strip's diag WAV a finalised header instead of abandoning it.
        if (IsRecording && count != Channels.Count)
        {
            StopRecording();
            StatusText = $"Recording stopped — changing to {count} mic strips starts a new one.";
        }

        bool prevAutosave = _suppressAutosave;
        bool prevRebuild = _suppressRebuild;
        _suppressAutosave = true;
        _suppressRebuild = true;
        try
        {
            int cur = Channels.Count;
            if (count > cur)
            {
                _engine.SetInputCount(count);
                for (int i = cur; i < count; i++)
                {
                    var ch = CreateChannel(i);
                    AttachChannel(ch);
                    Channels.Add(ch);
                }
            }
            else if (count < cur)
            {
                for (int i = cur - 1; i >= count; i--)
                {
                    DetachChannel(Channels[i]);
                    Channels.RemoveAt(i);
                }
                _engine.SetInputCount(count);
            }
        }
        finally
        {
            _suppressAutosave = prevAutosave;
            _suppressRebuild = prevRebuild;
        }
        RebuildAvailableDevices();
    }

    // User-triggered actions all report failure the same way: a status-bar line naming the action.
    private void RunGuarded(string what, Action body)
    {
        try { body(); }
        catch (Exception ex) { StatusText = $"{what} failed: {ex.Message}"; }
    }

    private void SetInputDevice(int index, AudioDeviceInfo? device)
    {
        if (device != null && !_suppressRebuild) ClaimFreeSide(index, device);
        RunGuarded($"Input {index + 1}", () =>
        {
            _engine.SetInputDevice(index, device);
            StatusText = device == null ? $"Input {index + 1}: (none)" : $"Input {index + 1}: {device.FriendlyName}";
        });
        if (!_suppressRebuild) RebuildAvailableDevices();
    }

    // Picking an endpoint a sibling has already half-claimed means the operator is splitting a
    // two-transmitter receiver, so take the side still free rather than doubling the same audio onto
    // the bus. An explicit side that is still available is left alone.
    private void ClaimFreeSide(int index, AudioDeviceInfo device)
    {
        var claimed = ClaimsExcept(index);
        if (DeviceResolver.IsFree(claimed, device.Id, Channels[index].Source)) return;
        var free = FreeSideFor(claimed, device.Id);
        if (free != null) Channels[index].Source = free.Value;
    }

    private void SetOutputDevice(int index, AudioDeviceInfo? device)
    {
        string tag = OutputViewModel.Tag(index);
        RunGuarded($"Output {tag}", () =>
        {
            _engine.SetOutputDevice(index, device);
            StatusText = device == null ? $"Output {tag}: (none)" : $"Output {tag}: {device.FriendlyName}";
        });
        if (!_suppressRebuild) RebuildAvailableDevices();
    }

    // Device pickers are exclusive: a strip may only offer devices no sibling strip has claimed.
    private static void RefreshExclusive<T>(
        IReadOnlyList<T> strips, IReadOnlyList<AudioDeviceInfo> all,
        Func<T, string?> selectedId, Action<T, IEnumerable<AudioDeviceInfo>> refresh)
    {
        for (int i = 0; i < strips.Count; i++)
        {
            var excluded = new HashSet<string>();
            for (int j = 0; j < strips.Count; j++)
            {
                if (j == i) continue;
                var id = selectedId(strips[j]);
                if (!string.IsNullOrEmpty(id)) excluded.Add(id);
            }
            refresh(strips[i], all.Where(d => !excluded.Contains(d.Id)));
        }
    }

    // Input pickers are exclusive per SIDE, not per endpoint: a split two-transmitter receiver is
    // one WASAPI device that legitimately feeds two strips, so an endpoint stays on offer until
    // every side of it is claimed.
    private void RefreshExclusiveChannels()
    {
        for (int i = 0; i < Channels.Count; i++)
        {
            var claimed = ClaimsExcept(i);
            var self = Channels[i];
            self.RefreshDevices(VisibleInputs().Where(
                d => d.Id == self.SelectedDevice?.Id || FreeSideFor(claimed, d.Id) != null));
        }
    }

    private HashSet<string> ClaimsExcept(int index)
    {
        var claimed = new HashSet<string>();
        for (int j = 0; j < Channels.Count; j++)
        {
            if (j == index) continue;
            var id = Channels[j].SelectedDevice?.Id;
            if (!string.IsNullOrEmpty(id)) claimed.Add(DeviceResolver.Claim(id, Channels[j].Source));
        }
        return claimed;
    }

    // The side of `id` a strip could still take, or null when the endpoint is fully claimed. Stereo
    // is offered first so an untouched device still binds whole.
    private static ChannelSource? FreeSideFor(IReadOnlySet<string> claimed, string id)
    {
        if (DeviceResolver.IsFree(claimed, id, ChannelSource.Stereo)) return ChannelSource.Stereo;
        if (DeviceResolver.IsFree(claimed, id, ChannelSource.Left)) return ChannelSource.Left;
        if (DeviceResolver.IsFree(claimed, id, ChannelSource.Right)) return ChannelSource.Right;
        return null;
    }

    private void DropConflictingChannelSelections()
    {
        var claimed = new HashSet<string>();
        foreach (var ch in Channels)
        {
            var id = ch.SelectedDevice?.Id;
            if (string.IsNullOrEmpty(id)) continue;
            if (DeviceResolver.IsFree(claimed, id, ch.Source)) claimed.Add(DeviceResolver.Claim(id, ch.Source));
            else ch.SelectedDevice = null;
        }
    }

    private static void DropDuplicateSelections<T>(
        IEnumerable<T> strips, Func<T, string?> selectedId, Action<T> clear)
    {
        var seen = new HashSet<string>();
        foreach (var strip in strips)
        {
            var id = selectedId(strip);
            if (string.IsNullOrEmpty(id)) continue;
            if (!seen.Add(id)) clear(strip);
        }
    }

    private void RebuildAvailableDevices()
    {
        if (_rebuildInProgress) return;
        _rebuildInProgress = true;
        try
        {
            RefreshExclusiveChannels();
            RefreshExclusive(Outputs, VisibleOutputs().ToList(),
                o => o.SelectedDevice?.Id, (o, devices) => o.RefreshDevices(devices));
        }
        finally
        {
            _rebuildInProgress = false;
        }
    }

    /// <summary>
    /// Re-binds any strip whose desired device has reappeared. Hot-plug USB audio comes back with a
    /// NEW endpoint GUID, so this matches on id first and then friendly name, exactly as a preset
    /// load does — which is what turns "unplug the receiver, plug it back in, remap both strips by
    /// hand" into the strips simply lighting up again.
    /// </summary>
    private void ReattachDesiredDevices()
    {
        // Buses first: a headset coming back should start playing again without the operator noticing
        // it ever went. Claims are per-list, so outputs cannot collide with input strips.
        var usedOutputs = new HashSet<string>(
            Outputs.Where(o => o.SelectedDevice != null).Select(o => o.SelectedDevice!.Id));
        foreach (var op in Outputs)
        {
            if (op.SelectedDevice != null) continue;
            if (op.DesiredDeviceId == null && op.DesiredDeviceName == null) continue;

            var found = DeviceResolver.Resolve(
                _allOutputDevices, op.DesiredDeviceId, op.DesiredDeviceName, usedOutputs);
            if (found == null) continue;

            op.SelectedDevice = found;
            AudioLog.Write($"Bus {OutputViewModel.Tag(op.Index)} reattached to '{found.FriendlyName}' "
                         + $"(desired '{op.DesiredDeviceName}') after it reappeared.");
        }

        for (int i = 0; i < Channels.Count; i++)
        {
            var ch = Channels[i];
            if (ch.SelectedDevice != null) continue;
            if (ch.DesiredDeviceId == null && ch.DesiredDeviceName == null
                && ch.DesiredDeviceKey == null) continue;

            var claimed = ClaimsExcept(i);
            var match = DeviceResolver.Resolve(
                _allInputDevices, ch.DesiredDeviceId, ch.DesiredDeviceName, claimed, ch.Source,
                ch.DesiredDeviceKey);
            if (match == null) continue;

            ch.SelectedDevice = match;
            AudioLog.Write($"Input {i} reattached to '{match.FriendlyName}' (desired "
                         + $"'{ch.DesiredDeviceName}') after it reappeared.");
        }
    }

    private void DedupeAndRebuild()
    {
        if (!_suppressRebuild) ReattachDesiredDevices();
        DropConflictingChannelSelections();
        DropDuplicateSelections(Outputs, o => o.SelectedDevice?.Id, o => o.SelectedDevice = null);
        RebuildAvailableDevices();
    }

    private void ResyncAudio() => RunGuarded("Resync", () =>
    {
        _engine.RestartInputs();
        _engine.RestartOutputs();
        StatusText = "Audio resynced (inputs + outputs).";
        _session?.Action("resync");
    });

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    // Picker visibility is applied HERE, when the per-strip lists are built — never to the master
    // _allInputDevices / _allOutputDevices. ApplyPreset resolves a preset's saved devices against the
    // master lists (see the device-identity gotcha), so filtering those would make a preset naming a
    // hidden device silently fail to bind, which looks exactly like a lost device. A device that is
    // currently bound stays visible regardless, or a working strip shows an empty selection.
    private IEnumerable<AudioDeviceInfo> VisibleInputs()
    {
        if (!_hideVirtualInputs) return _allInputDevices;
        var bound = Channels.Select(c => c.SelectedDevice?.Id).Where(id => id != null).ToHashSet();
        return _allInputDevices.Where(
            d => bound.Contains(d.Id) || !VirtualDeviceFilter.IsVirtualInput(d.FriendlyName));
    }

    private IEnumerable<AudioDeviceInfo> VisibleOutputs()
    {
        if (!_hideVoicemeeterOutputs) return _allOutputDevices;
        var bound = Outputs.Select(o => o.SelectedDevice?.Id).Where(id => id != null).ToHashSet();
        return _allOutputDevices.Where(
            d => bound.Contains(d.Id) || !VirtualDeviceFilter.IsVirtualOutput(d.FriendlyName));
    }

    private void RefreshDevices()
    {
        _allInputDevices = AudioDeviceInfo.Enumerate(DataFlow.Capture);
        _allOutputDevices = AudioDeviceInfo.Enumerate(DataFlow.Render);
        DedupeAndRebuild();
        UpdateVbCableStatus();
        StatusText = $"Refreshed: {_allInputDevices.Count} inputs, {_allOutputDevices.Count} outputs";
    }

    // VB-CABLE installs "CABLE Input" (render) + "CABLE Output" (capture); detect either by the VB-Audio vendor tag.
    private static bool IsVbCableInstalled(IEnumerable<AudioDeviceInfo> a, IEnumerable<AudioDeviceInfo> b) =>
        a.Concat(b).Any(d =>
            d.FriendlyName.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase) ||
            d.FriendlyName.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase) ||
            d.FriendlyName.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase));

    private void UpdateVbCableStatus()
    {
        _vbCableInstalled = IsVbCableInstalled(_allInputDevices, _allOutputDevices);
        RaisePropertyChanged(nameof(ShowVbCablePrompt));
    }

    private void OpenVbCableDownload() => OpenUrl(VbCableUrl);

    private void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText = $"Couldn't open browser: {ex.Message}";
        }
    }

    private void DismissVbCablePrompt()
    {
        if (_vbCablePromptDismissed) return;
        _vbCablePromptDismissed = true;
        RaisePropertyChanged(nameof(ShowVbCablePrompt));
        QueueAutosave();
    }

    // The allowlist lives in Services.PersistedProperties so the "meter tick must never trigger
    // autosave" invariant can be unit-tested; see the comment there for why this is an allowlist.
    private void OnSettingChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_suppressAutosave) return;
        if (!PersistedProperties.Contains(e.PropertyName)) return;

        // Renaming a strip changes what the lapel picker shows, and nothing else raises it.
        if (e.PropertyName == nameof(ChannelViewModel.CustomLabel))
            RaisePropertyChanged(nameof(LapelOptions));

        // A veto raises the same notification a real change does, so that the control snaps back —
        // but the value behind it is unchanged, and everything below reads the value. Logging it
        // recorded the OPPOSITE action ("LAPEL unmuted" when a mute was refused), cleared the scene
        // the operator had not left, and restarted the autosave for a write that never happened.
        if (sender is ChannelViewModel { ChangeRefused: true }
            or RouteToggleViewModel { ChangeRefused: true }) return;

        // A hand-edit invalidates the active scene, so Simple mode stops claiming one. Guarded,
        // because applying a scene writes these same properties.
        if (Scenes is { IsApplying: false })
        {
            Scenes.MarkCustomised();
            // Only hand edits are logged as operator actions. A scene writes every channel and output
            // at once, so logging those would bury the one deliberate change in twenty derived ones —
            // the scene itself is logged instead, where it is applied.
            _session?.Action(DescribeChange(sender, e.PropertyName));
        }

        _autosaveTimer.Stop();
        _autosaveTimer.Start();
    }

    /// <summary>
    /// A change in the operator's words, not the view model's. "Rode L off bus B" is something you can
    /// line up against the audio; "IsOn changed" is not.
    /// </summary>
    private string DescribeChange(object? sender, string? property) => sender switch
    {
        RouteToggleViewModel r when property == nameof(RouteToggleViewModel.IsOn) =>
            $"{NameOf(r)} {(r.IsOn ? "routed to" : "removed from")} bus {r.ShortLabel}",
        ChannelViewModel c => property switch
        {
            nameof(ChannelViewModel.Muted) => $"{Label(c)} {(c.Muted ? "muted" : "unmuted")}",
            nameof(ChannelViewModel.VolumePercent) => $"{Label(c)} level {c.VolumePercent:F0}%",
            nameof(ChannelViewModel.SelectedDevice) =>
                $"{Label(c)} set to {c.SelectedDevice?.FriendlyName ?? "(none)"}",
            nameof(ChannelViewModel.Source) => $"{Label(c)} side {c.Source}",
            nameof(ChannelViewModel.IsPriority) => $"{Label(c)} priority {(c.IsPriority ? "on" : "off")}",
            nameof(ChannelViewModel.HighPassHz) => $"low-cut {c.HighPassHz} Hz",
            _ => "",
        },
        OutputViewModel o => property switch
        {
            nameof(OutputViewModel.Muted) => $"bus {OutputViewModel.Tag(o.Index)} {(o.Muted ? "muted" : "unmuted")}",
            nameof(OutputViewModel.VolumePercent) => $"bus {OutputViewModel.Tag(o.Index)} volume {o.VolumePercent:F0}%",
            nameof(OutputViewModel.AutoMixModeIndex) =>
                $"bus {OutputViewModel.Tag(o.Index)} automix {o.AutoMixModeOptions[Math.Clamp(o.AutoMixModeIndex, 0, o.AutoMixModeOptions.Length - 1)]}",
            nameof(OutputViewModel.LevelerEnabled) =>
                $"bus {OutputViewModel.Tag(o.Index)} leveler {(o.LevelerEnabled ? "on" : "off")}",
            nameof(OutputViewModel.LevelerStrength) =>
                $"bus {OutputViewModel.Tag(o.Index)} leveler {o.LevelerStrength}",
            _ => "",
        },
        _ => "",
    };

    private static string Label(ChannelViewModel c) =>
        string.IsNullOrWhiteSpace(c.CustomLabel) ? c.Label : c.CustomLabel;

    private string NameOf(RouteToggleViewModel r)
    {
        var owner = Channels.FirstOrDefault(c => c.Routes.Contains(r));
        return owner == null ? "a mic" : Label(owner);
    }

    private void SavePreset()
    {
        // A replay sandbox has no real input devices bound; letting it autosave would overwrite the
        // operator's preset with replay placeholders, possibly while they are mid-service.
        if (Audio.Replay.ReplayOptions.Current?.SuppressAutosave == true) return;

        var preset = PresetMapper.FromViewModels(Channels, Outputs, new PresetMapper.AppOptions(
            _vbCablePromptDismissed, _hideVirtualInputs, _hideVoicemeeterOutputs, _warnOnBluetoothMics,
            _lowCutHz));
        RunGuarded("Save", () =>
        {
            _presetStore.Save(preset);
        });
    }

    private void TryLoadInitialPreset()
    {
        var preset = _presetStore.Load();
        if (preset != null) ApplyPreset(preset);
    }

    private void ApplyPreset(MixerPreset preset)
    {
        _suppressAutosave = true;
        _suppressRebuild = true;
        try
        {
            _vbCablePromptDismissed = preset.VbCablePromptDismissed;
            UpdateVbCableStatus();

            // Straight to the backing fields: the public setters call RefreshDevices(), which rebuilds
            // the pickers mid-apply, before the channels have been given their devices. RefreshDevices
            // runs once at the end of ApplyPreset anyway.
            _hideVirtualInputs = preset.HideVirtualInputs;
            _hideVoicemeeterOutputs = preset.HideVoicemeeterOutputs;
            _warnOnBluetoothMics = preset.WarnOnBluetoothMics;
            // Older presets have no global value; take the first channel's, which is what the operator
            // was actually hearing, rather than silently imposing the default on a working rig.
            // Null means the preset predates the global low-cut, so take the first strip's own
            // value — that is what the operator had. A saved 0 is a real choice (filter off) and must
            // survive, which is exactly what the old non-null default could not express.
            _lowCutHz = preset.LowCutHz
                ?? (preset.Channels.Length > 0 ? preset.Channels[0].HighPassHz : DefaultLowCutHz);
            RaisePropertyChanged(nameof(HideVirtualInputs));
            RaisePropertyChanged(nameof(HideVoicemeeterOutputs));
            RaisePropertyChanged(nameof(WarnOnBluetoothMics));
            RaisePropertyChanged(nameof(LowCutHz));
            RaisePropertyChanged(nameof(LowCutIndex));

            int desired = Math.Clamp(preset.Channels.Length, AudioEngine.MinInputCount, AudioEngine.MaxInputCount);
            if (preset.Channels.Length > 0 && desired != Channels.Count)
            {
                ApplyInputCount(desired);
                _inputCount = desired;
                RaisePropertyChanged(nameof(InputCount));
            }

            var usedInputIds = new HashSet<string>();
            for (int i = 0; i < Channels.Count && i < preset.Channels.Length; i++)
            {
                var cp = preset.Channels[i];
                if (!string.IsNullOrEmpty(cp.CustomLabel)) Channels[i].CustomLabel = cp.CustomLabel;
                // Both before the device: Start builds the conversion chain and the filter from the
                // current side/cutoff, so setting them afterwards would open the capture wrong and
                // immediately rebuild it.
                Channels[i].Source = (ChannelSource)Math.Clamp(cp.Source, 0, 2);
                Channels[i].HighPassHz = _lowCutHz;   // one low-cut for every mic
                // Seeded first so an unresolvable device is still remembered: the strip keeps what it
                // wants, and ReattachDesiredDevices binds it the moment it reappears.
                Channels[i].RestoreDesiredDevice(cp.DeviceId, cp.DeviceName, cp.DeviceKey);
                var match = DeviceResolver.Resolve(
                    _allInputDevices, cp.DeviceId, cp.DeviceName, usedInputIds, Channels[i].Source,
                    cp.DeviceKey);
                Channels[i].SelectedDevice = match;
                Channels[i].VolumePercent = cp.VolumePercent;
                Channels[i].Muted = cp.Muted;
                Channels[i].IsPriority = cp.Priority;
                // Presets written before scenes existed have no Role, and 0 (Room) is indistinguishable
                // from "not set" — migrate those from the priority flag, which is what marked the lapel.
                Channels[i].Role = cp.Role != 0
                    ? (Models.ChannelRole)cp.Role
                    : (cp.Priority ? Models.ChannelRole.Lapel : Models.ChannelRole.Room);
                for (int r = 0; r < Channels[i].Routes.Length && r < cp.Routes.Length; r++)
                {
                    Channels[i].Routes[r].IsOn = cp.Routes[r];
                }
            }
            var usedOutputIds = new HashSet<string>();
            for (int o = 0; o < Outputs.Length && o < preset.Outputs.Length; o++)
            {
                var op = preset.Outputs[o];
                if (!string.IsNullOrEmpty(op.CustomLabel)) Outputs[o].CustomLabel = op.CustomLabel;
                Outputs[o].RestoreDesiredDevice(op.DeviceId, op.DeviceName);
                var match = DeviceResolver.Resolve(_allOutputDevices, op.DeviceId, op.DeviceName, usedOutputIds);
                Outputs[o].SelectedDevice = match;
                // Migrate presets written before Share was removed: the enum was Off=0, Share=1,
                // Gate=2, so a saved 2 is out of range now and a saved 1 meant Share. Both become
                // Gate — Share's job was follow-the-talker, and Gate is what every scene forced.
                Outputs[o].AutoMixModeIndex = (int)OutputPreset.MigrateMode(op.AutoMixMode);
                Outputs[o].VolumePercent = Math.Clamp(op.Volume, 0f, 100f);

                // Strength first (it rewrites threshold/ratio/cap), then the individual values, so a
                // preset that was tuned away from its strength preset keeps the tuned numbers.
                // Enabled LAST: never engage on half-applied settings.
                Outputs[o].LevelerStrength = (LevelerStrength)Math.Clamp(op.LevelerStrength, 0, 2);
                Outputs[o].LevelerThresholdDb = op.LevelerThresholdDb;
                Outputs[o].LevelerRatio = op.LevelerRatio;
                Outputs[o].LevelerAttackMs = op.LevelerAttackMs;
                Outputs[o].LevelerReleaseMs = op.LevelerReleaseMs;
                Outputs[o].LevelerMaxGainDb = op.LevelerMaxGainDb;
                Outputs[o].LevelerIdleFloorDb = op.LevelerIdleFloorDb;
                Outputs[o].LimiterCeilingDb = op.LimiterCeilingDb;
                Outputs[o].LevelerEnabled = op.LevelerEnabled;
            }
        }
        finally
        {
            _suppressAutosave = false;
            _suppressRebuild = false;
        }
        DedupeAndRebuild();
    }

    // Diagnostic: records every selected input to its own WAV via the pre-automix analysis tap (the
    // same tap "Detect Delays" uses), so the captured per-mic feeds show what the automixer's
    // selection is actually deciding on — the mix recorder is post-automix and useless for this.

    /// <summary>
    /// One button records EVERYTHING: every bound input to its own file, and every live bus.
    ///
    /// Per-input and per-output record buttons were separate until 2026-09-20, which meant the
    /// combination you actually want when something sounds wrong — the raw mics AND the mix they
    /// produced, from the same moment — took three deliberate clicks and was usually remembered
    /// afterwards. One stamp ties the whole set together so an offline replay lines up.
    /// </summary>
    private void ToggleRecording()
    {
        if (_recording) { StopRecording(); return; }

        // Old recordings first, so a start is not refused for space that is about to be freed.
        _retention.Prune();
        if (!_retention.HasRoomToStart())
        {
            StatusText = $"Not enough disk space to record " +
                         $"({RecordingRetention.FreeGb(RecordingRoot):F0} GB free).";
            return;
        }

        var inputs = Channels.Where(c => c.SelectedDevice != null).ToArray();
        var outputs = Outputs.Where(o => o.SelectedDevice != null).ToArray();
        if (inputs.Length == 0 && outputs.Length == 0)
        {
            StatusText = "Nothing to record — no microphone or output device is selected.";
            return;
        }

        RunGuarded("Record", () =>
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            // The per-mic captures keep living under analysis/: every offline tool globs for
            // diag-input*.wav there, and the replay rig loads a session by that stamp.
            string mics = Path.Combine(RecordingRoot, "analysis");
            string mixes = Path.Combine(RecordingRoot, "recordings");
            Directory.CreateDirectory(mics);
            Directory.CreateDirectory(mixes);

            foreach (var ch in inputs)
            {
                _engine.Inputs[ch.Index].StartAnalysisRecording(
                    Path.Combine(mics, $"diag-input{ch.Index + 1}-{stamp}.wav"));
            }

            // Shares the stamp so an offline tool can line the selector's choices up against the audio
            // it was choosing between. Every channel, not just the recorded ones, so an unbound strip
            // still shows as a column rather than shifting the ones after it.
            _decisions = new DecisionTrack(
                Path.Combine(mics, $"decisions-{stamp}.csv"),
                Channels.Select(c => string.IsNullOrWhiteSpace(c.CustomLabel) ? c.Label : c.CustomLabel).ToList(),
                Outputs.Select(o => OutputViewModel.Tag(o.Index)).ToList());
            foreach (var ovm in outputs)
            {
                var bus = _engine.Outputs[ovm.Index];
                var rec = _recorders[ovm.Index];
                rec.Start(Path.Combine(mixes, $"mix-{OutputViewModel.Tag(ovm.Index)}-{stamp}.wav"),
                          bus.InternalFormat);
                bus.Recorder = rec;
                ovm.SetRecording(true);
            }

            _recording = true;
            _recordingStarted = DateTime.Now;
            _session?.Action("recording started");
            RaiseRecordingState();
            StatusText = $"Recording {inputs.Length} mics and {outputs.Length} buses.";
        });
    }

    private void StopRecording()
    {
        if (!_recording) return;
        _recording = false;
        foreach (var ch in Channels) _engine.Inputs[ch.Index].StopAnalysisRecording();
        _decisions?.Dispose();
        _decisions = null;
        for (int o = 0; o < Outputs.Length; o++)
        {
            _engine.Outputs[o].Recorder = null;
            _recorders[o].Stop();
            Outputs[o].SetRecording(false);
        }
        RaiseRecordingState();
        StatusText = $"Recording saved to {RecordingRoot}";
    }

    /// <summary>
    /// Ends a recording that has run too long or is about to fill the disk. Both exist because this
    /// records unattended: the failure being guarded against is nobody being there to notice.
    /// </summary>
    private long _lastSpaceCheck;

    /// <summary>How often the free-space floor is probed. It was read on every meter tick, i.e. a
    /// DriveInfo query 30 times a second for the whole recording; a disk does not fill in 33 ms.</summary>
    private const long SpaceCheckMs = 1000;

    private void CheckRecordingLimits()
    {
        if (!_recording) return;

        if (DateTime.Now - _recordingStarted >= MaxRecordingLength)
        {
            StopRecording();
            StatusText = $"Recording stopped automatically after {MaxRecordingLength.TotalHours:F0} hours.";
            AudioLog.Write("Recording stopped: reached the maximum length.");
            return;
        }

        long nowTicks = Environment.TickCount64;
        if (nowTicks - _lastSpaceCheck < SpaceCheckMs) return;
        _lastSpaceCheck = nowTicks;

        if (_retention.MustStopNow())
        {
            StopRecording();
            StatusText = "Recording stopped — the disk is nearly full.";
            AudioLog.Write("Recording stopped: free space below the floor.");
        }
    }

    private void RaiseRecordingState()
    {
        RaisePropertyChanged(nameof(IsRecording));
        RaisePropertyChanged(nameof(RecordIcon));
        RaisePropertyChanged(nameof(RecordTooltip));
    }

    public void Dispose()
    {
        _stateServer?.Dispose();
        _deviceWatcher.Dispose();
        _meterTimer.Stop();
        _autosaveTimer.Stop();
        StopRecording();
        SavePreset();
        _session?.Dispose();
        foreach (var r in _recorders) r?.Dispose();
        _engine.Dispose();
    }
}

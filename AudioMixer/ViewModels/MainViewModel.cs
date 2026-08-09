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
    private bool _suppressAutosave;
    private bool _suppressRebuild;
    private bool _rebuildInProgress;
    private List<AudioDeviceInfo> _allInputDevices = new();
    private List<AudioDeviceInfo> _allOutputDevices = new();

    public ObservableCollection<ChannelViewModel> Channels { get; } = new();
    public OutputViewModel[] Outputs { get; }

    public RelayCommand RefreshDevicesCommand { get; }
    public RelayCommand DetectDelaysCommand { get; }
    public RelayCommand RecordInputsCommand { get; }
    public RelayCommand ResyncAudioCommand { get; }
    public RelayCommand DownloadVbCableCommand { get; }
    public RelayCommand DismissVbCablePromptCommand { get; }
    public RelayCommand OpenDocumentationCommand { get; }

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
        set => SetField(ref _statusText, value);
    }

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
            RaisePropertyChanged(nameof(WindowWidth));
            if (!_suppressAutosave) { _autosaveTimer.Stop(); _autosaveTimer.Start(); }
        }
    }

    public double WindowWidth => Math.Max(560, _inputCount * 96 + 240);

    private const double BaseWindowHeight = 344;
    private const double VbCableBannerHeight = 36;
    public double WindowHeight => BaseWindowHeight + (ShowVbCablePrompt ? VbCableBannerHeight : 0);

    public MainViewModel()
    {
        _engine = new AudioEngine();
        _engine.InputRestarted += (idx, attempt) => RunOnUi(() =>
            StatusText = $"Input {idx + 1} dropped — auto-restarted (attempt {attempt}).");
        _engine.InputRestartGaveUp += idx => RunOnUi(() =>
            StatusText = $"Input {idx + 1} not responding — re-pick the device or click Resync.");

        _allInputDevices = AudioDeviceInfo.Enumerate(DataFlow.Capture);
        _allOutputDevices = AudioDeviceInfo.Enumerate(DataFlow.Render);

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
                (idx, dev) => SetOutputDevice(idx, dev),
                ToggleRecord);
        }

        RefreshDevicesCommand = new RelayCommand(RefreshDevices);
        DetectDelaysCommand = new RelayCommand(StartDelayDetection);
        RecordInputsCommand = new RelayCommand(ToggleInputDiagRecording);
        ResyncAudioCommand = new RelayCommand(ResyncAudio);
        DownloadVbCableCommand = new RelayCommand(OpenVbCableDownload);
        DismissVbCablePromptCommand = new RelayCommand(DismissVbCablePrompt);
        OpenDocumentationCommand = new RelayCommand(() => OpenUrl(DocsUrl));

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
        _meterTimer.Tick += (_, _) =>
        {
            foreach (var ch in Channels) ch.RefreshMeters();
            foreach (var op in Outputs) op.RefreshMeters();
            _diagnostics.Tick();
            RefreshHealth();
            if (_isReplaying) RaisePropertyChanged(nameof(ReplayPositionText));
        };
        _meterTimer.Start();

        TryLoadInitialPreset();
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
    public RelayCommand DismissAlertCommand { get; private set; } = null!;

    public ObservableCollection<HealthAlert> Alerts { get; } = new();
    private readonly HashSet<string> _dismissedAlerts = new();

    public HealthAlert? TopAlert => Alerts.Count > 0 ? Alerts[0] : null;
    public bool HasAlert => Alerts.Count > 0;
    public string AlertSummary => Alerts.Count switch
    {
        0 => "All good",
        1 => TopAlert!.Message,
        _ => $"{TopAlert!.Message}   (+{Alerts.Count - 1} more)",
    };

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
        DismissAlertCommand = new RelayCommand(() =>
        {
            if (TopAlert != null) _dismissedAlerts.Add(TopAlert.Id);
            RefreshHealth(force: true);
        });
    }

    // --- Settings-window options ----------------------------------------------------------------
    // Deliberately runtime-only for now: they change what the pickers *show*, not what the mixer does,
    // and adding them to the preset means adding them to PersistedProperties too (see that file).

    private bool _hideVirtualInputs;
    public bool HideVirtualInputs
    {
        get => _hideVirtualInputs;
        set { if (SetField(ref _hideVirtualInputs, value)) RefreshDevices(); }
    }

    private bool _warnOnBluetoothMics = true;
    public bool WarnOnBluetoothMics
    {
        get => _warnOnBluetoothMics;
        set => SetField(ref _warnOnBluetoothMics, value);
    }

    public string DiagnosticsSummary =>
        $"Log: {(AudioLog.Enabled ? "on (%TEMP%\\AudioMixer.log)" : "off — relaunch with --log")}\n" +
        $"State endpoint: {(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AUDIOMIXER_STATE")) ? "off — relaunch with --state" : $"http://127.0.0.1:{Environment.GetEnvironmentVariable("AUDIOMIXER_STATE")}/state")}\n" +
        $"Binding errors: {Services.BindingErrorListener.ErrorCount}\n" +
        (IsReplaying ? ReplaySessionLabel : "Live capture");

    private long _lastHealthTicks;

    // Evaluated at ~1 Hz rather than on every meter tick: the alert set is stable on that timescale,
    // and re-raising a collection 30x/second would churn the UI for nothing.
    private void RefreshHealth(bool force = false)
    {
        long now = Environment.TickCount64;
        if (!force && now - _lastHealthTicks < 1000) return;
        _lastHealthTicks = now;

        var snapshot = BuildHealthSnapshot(now);
        var fresh = HealthMonitor.Evaluate(snapshot)
            .Where(a => !_dismissedAlerts.Contains(a.Id))
            .ToList();

        // An alert that clears becomes dismissible again, so a recurrence is not silently swallowed.
        var liveIds = fresh.Select(a => a.Id).ToHashSet();
        _dismissedAlerts.RemoveWhere(id => !HealthMonitor
            .Evaluate(snapshot).Any(a => a.Id == id));

        if (fresh.Count == Alerts.Count && fresh.Zip(Alerts).All(p => p.First == p.Second)) return;

        Alerts.Clear();
        foreach (var a in fresh) Alerts.Add(a);
        RaisePropertyChanged(nameof(TopAlert));
        RaisePropertyChanged(nameof(HasAlert));
        RaisePropertyChanged(nameof(AlertSummary));
    }

    private HealthSnapshot BuildHealthSnapshot(long now)
    {
        var channels = new List<ChannelHealth>(Channels.Count);
        for (int i = 0; i < Channels.Count; i++)
        {
            var vm = Channels[i];
            var input = _engine.Inputs[i];
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
                (now - input.LastSoundTicks) / 1000.0));
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
                (now - _lastOutputSound[o]) / 1000.0));
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
    public void RefreshDiagnostics()
    {
        var diag = _engine.AutoMixSnapshot();

        // Rank by whatever the first output is actually deciding on, so "why isn't #2 winning" is
        // answered by reading down the column that matters rather than guessing.
        bool natural = Outputs.Length > 0 && Outputs[0].PreferNatural;
        bool corr = Outputs.Length > 0 && Outputs[0].ReferenceGuided;

        var order = Enumerable.Range(0, Channels.Count)
            .Where(i => Channels[i].HasDevice && !Channels[i].Muted && Channels[i].IsRoutedAnywhere)
            .OrderByDescending(i => corr && i < diag.Corr.Length ? diag.Corr[i]
                : natural && i < diag.Cv.Length && diag.Cv[i] > 0 ? -diag.Cv[i]
                : i < diag.Env.Length ? diag.Env[i] : 0f)
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

    /// <summary>Replay position, polled by the meter timer so the UI can show a transport readout.</summary>
    public string ReplayPositionText
    {
        get
        {
            var rig = _engine.Replay;
            return rig == null ? "" : $"{rig.Position:hh\\:mm\\:ss} / {rig.Duration:hh\\:mm\\:ss}";
        }
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

    private ChannelViewModel CreateChannel(int index) =>
        new ChannelViewModel(
            index, _engine.Inputs[index], _allInputDevices, AudioEngine.OutputCount,
            (idx, dev) => SetInputDevice(idx, dev));

    private void AttachChannel(ChannelViewModel ch)
    {
        ch.PropertyChanged += OnSettingChanged;
        foreach (var r in ch.Routes) r.PropertyChanged += OnSettingChanged;
        ch.AttachOutputs(Outputs);
    }

    private void DetachChannel(ChannelViewModel ch)
    {
        ch.PropertyChanged -= OnSettingChanged;
        foreach (var r in ch.Routes) r.PropertyChanged -= OnSettingChanged;
    }

    private void ApplyInputCount(int count)
    {
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
        RunGuarded($"Input {index + 1}", () =>
        {
            _engine.SetInputDevice(index, device);
            StatusText = device == null ? $"Input {index + 1}: (none)" : $"Input {index + 1}: {device.FriendlyName}";
        });
        if (!_suppressRebuild) RebuildAvailableDevices();
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
            RefreshExclusive(Channels, _allInputDevices,
                c => c.SelectedDevice?.Id, (c, devices) => c.RefreshDevices(devices));
            RefreshExclusive(Outputs, _allOutputDevices,
                o => o.SelectedDevice?.Id, (o, devices) => o.RefreshDevices(devices));
        }
        finally
        {
            _rebuildInProgress = false;
        }
    }

    private void DedupeAndRebuild()
    {
        DropDuplicateSelections(Channels, c => c.SelectedDevice?.Id, c => c.SelectedDevice = null);
        DropDuplicateSelections(Outputs, o => o.SelectedDevice?.Id, o => o.SelectedDevice = null);
        RebuildAvailableDevices();
    }

    private void ResyncAudio() => RunGuarded("Resync", () =>
    {
        _engine.RestartInputs();
        _engine.RestartOutputs();
        StatusText = "Audio resynced (inputs + outputs).";
    });

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    // Virtual capture endpoints (CABLE Output, VoiceMeeter) are never the right microphone, but they
    // clutter every input picker. Filtered from INPUTS only — VB-CABLE must stay selectable as an
    // output, since that is the path into Zoom. A device already bound is never hidden, or the picker
    // would show an empty selection for a working channel.
    private static readonly string[] VirtualDeviceTags =
        { "VB-Audio", "CABLE Output", "CABLE Input", "VoiceMeeter", "Virtual" };

    private List<AudioDeviceInfo> FilterInputs(List<AudioDeviceInfo> devices)
    {
        if (!_hideVirtualInputs) return devices;
        var bound = Channels.Select(c => c.SelectedDevice?.Id).Where(id => id != null).ToHashSet();
        return devices
            .Where(d => bound.Contains(d.Id) ||
                        !VirtualDeviceTags.Any(t => d.FriendlyName.Contains(t, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    private void RefreshDevices()
    {
        _allInputDevices = FilterInputs(AudioDeviceInfo.Enumerate(DataFlow.Capture));
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
        RaisePropertyChanged(nameof(WindowHeight));
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
        RaisePropertyChanged(nameof(WindowHeight));
        if (!_suppressAutosave) { _autosaveTimer.Stop(); _autosaveTimer.Start(); }
    }

    private void ToggleRecord(int index)
    {
        if (index < 0 || index >= Outputs.Length) return;
        var ovm = Outputs[index];
        var bus = _engine.Outputs[index];
        var recorder = _recorders[index];

        if (ovm.IsRecording)
        {
            bus.Recorder = null;
            recorder.Stop();
            ovm.SetRecording(false);
            StatusText = $"Recording stopped. Saved to: {recorder.CurrentPath}";
            return;
        }

        string folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "AudioMixer", "recordings");
        string path = Path.Combine(folder, $"mix-{OutputViewModel.Tag(index)}-{DateTime.Now:yyyyMMdd-HHmmss}.wav");
        RunGuarded("Record", () =>
        {
            recorder.Start(path, bus.InternalFormat);
            bus.Recorder = recorder;
            ovm.SetRecording(true);
            StatusText = $"Recording {ovm.CustomLabel} → {Path.GetFileName(path)}";
        });
    }

    // The allowlist lives in Services.PersistedProperties so the "meter tick must never trigger
    // autosave" invariant can be unit-tested; see the comment there for why this is an allowlist.
    private void OnSettingChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_suppressAutosave) return;
        if (!PersistedProperties.Contains(e.PropertyName)) return;

        // A hand-edit in Advanced invalidates the active scene, so Simple mode stops claiming one.
        // Guarded, because applying a scene writes these same properties.
        if (Scenes is { IsApplying: false }) Scenes.MarkCustomised();

        _autosaveTimer.Stop();
        _autosaveTimer.Start();
    }

    private void SavePreset()
    {
        // A replay sandbox has no real input devices bound; letting it autosave would overwrite the
        // operator's preset with replay placeholders, possibly while they are mid-service.
        if (Audio.Replay.ReplayOptions.Current?.SuppressAutosave == true) return;

        var preset = PresetMapper.FromViewModels(Channels, Outputs, _vbCablePromptDismissed);
        RunGuarded("Save", () =>
        {
            _presetStore.Save(preset);
            StatusText = $"Saved {DateTime.Now:HH:mm:ss}";
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

            int desired = Math.Clamp(preset.Channels.Length, AudioEngine.MinInputCount, AudioEngine.MaxInputCount);
            if (preset.Channels.Length > 0 && desired != Channels.Count)
            {
                ApplyInputCount(desired);
                _inputCount = desired;
                RaisePropertyChanged(nameof(InputCount));
                RaisePropertyChanged(nameof(WindowWidth));
            }

            var usedInputIds = new HashSet<string>();
            for (int i = 0; i < Channels.Count && i < preset.Channels.Length; i++)
            {
                var cp = preset.Channels[i];
                if (!string.IsNullOrEmpty(cp.CustomLabel)) Channels[i].CustomLabel = cp.CustomLabel;
                var match = DeviceResolver.Resolve(_allInputDevices, cp.DeviceId, cp.DeviceName, usedInputIds);
                Channels[i].SelectedDevice = match;
                Channels[i].VolumePercent = cp.VolumePercent;
                Channels[i].Muted = cp.Muted;
                Channels[i].DelayMs = cp.DelayMs;
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
                var match = DeviceResolver.Resolve(_allOutputDevices, op.DeviceId, op.DeviceName, usedOutputIds);
                Outputs[o].SelectedDevice = match;
                Outputs[o].StrengthPercent = Math.Clamp(op.AutoMixStrength, 0f, 100f);
                Outputs[o].StableHandoff = op.AutoMixStableHandoff;
                Outputs[o].ReferenceGuided = op.AutoMixReferenceGuided;
                Outputs[o].PreferNatural = op.AutoMixPreferNatural;
                Outputs[o].AutoMixModeIndex = Math.Clamp(op.AutoMixMode, 0, 2);
                Outputs[o].VolumePercent = Math.Clamp(op.Volume, 0f, 100f);
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
    private bool _inputDiagRecording;
    public string InputDiagRecordIcon => _inputDiagRecording ? "■" : "●";
    public string InputDiagRecordTooltip => _inputDiagRecording
        ? "Stop recording inputs"
        : "Record all inputs to separate WAVs (diagnostic — raw pre-automix per-mic feeds)";

    private void ToggleInputDiagRecording()
    {
        if (_delayDetectionInProgress) { StatusText = "Busy with delay detection — try again in a moment."; return; }

        string folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "AudioMixer", "analysis");

        if (_inputDiagRecording)
        {
            foreach (var ch in Channels) _engine.Inputs[ch.Index].StopAnalysisRecording();
            _inputDiagRecording = false;
            RaisePropertyChanged(nameof(InputDiagRecordIcon));
            RaisePropertyChanged(nameof(InputDiagRecordTooltip));
            StatusText = $"Input recordings saved to {folder}";
            return;
        }

        var active = Channels.Where(c => c.SelectedDevice != null).ToArray();
        if (active.Length == 0) { StatusText = "No inputs with a device selected to record."; return; }

        RunGuarded("Input recording", () =>
        {
            Directory.CreateDirectory(folder);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            foreach (var ch in active)
            {
                string path = Path.Combine(folder, $"diag-input{ch.Index + 1}-{stamp}.wav");
                _engine.Inputs[ch.Index].StartAnalysisRecording(path);
            }
            _inputDiagRecording = true;
            RaisePropertyChanged(nameof(InputDiagRecordIcon));
            RaisePropertyChanged(nameof(InputDiagRecordTooltip));
            StatusText = $"Recording {active.Length} inputs — narrate which mic is closest as people talk.";
        });
    }

    private bool _delayDetectionInProgress;

    private async void StartDelayDetection()
    {
        if (_delayDetectionInProgress) return;
        var active = Channels.Where(c => c.SelectedDevice != null).ToArray();
        if (active.Length < 2)
        {
            MessageBox.Show("Need at least 2 inputs with a device selected.", "Detect Delays",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            "This will record all selected inputs for 4 seconds.\n\n" +
            "When you click OK, make ONE sharp sound (a clap is ideal) that all microphones can hear at the same instant.\n\n" +
            "Ready?",
            "Detect Delays", MessageBoxButton.OKCancel, MessageBoxImage.Information);
        if (confirm != MessageBoxResult.OK) return;

        _delayDetectionInProgress = true;
        try
        {
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "AudioMixer", "analysis");
            Directory.CreateDirectory(folder);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

            var recordings = new List<(int Index, string Path)>();
            foreach (var ch in active)
            {
                string path = Path.Combine(folder, $"input{ch.Index + 1}-{stamp}.wav");
                _engine.Inputs[ch.Index].StartAnalysisRecording(path);
                recordings.Add((ch.Index, path));
            }

            StatusText = "Recording 4 seconds — clap now!";
            await Task.Delay(TimeSpan.FromSeconds(4));

            foreach (var ch in active)
            {
                _engine.Inputs[ch.Index].StopAnalysisRecording();
            }

            StatusText = "Analyzing...";
            var result = DelayAnalyzer.Analyze(recordings);
            ShowAnalysisResult(result);
        }
        catch (Exception ex)
        {
            StatusText = $"Delay detection failed: {ex.Message}";
            MessageBox.Show(ex.ToString(), "Detect Delays — error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _delayDetectionInProgress = false;
        }
    }

    private void ShowAnalysisResult(DelayAnalyzer.AnalysisOutcome outcome)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Arrival offsets via onset cross-correlation (relative to the earliest input):");
        sb.AppendLine();
        foreach (var r in outcome.Inputs)
        {
            string label = $"Input {r.InputIndex + 1}";
            if (double.IsNaN(r.FirstTransientMs))
            {
                sb.AppendLine($"  {label}: no clear transient (peak {r.PeakAmplitude:F3})");
            }
            else
            {
                sb.AppendLine($"  {label}: arrived at {r.FirstTransientMs,7:F1} ms   →   suggested delay: {r.SuggestedDelayMs} ms   (corr {r.Confidence:F2})");
            }
        }
        if (outcome.Warning != null)
        {
            sb.AppendLine();
            sb.AppendLine("Warnings:");
            sb.AppendLine(outcome.Warning);
        }
        sb.AppendLine();
        sb.AppendLine("Apply the suggested delays?");
        sb.AppendLine("(This aligns all inputs to the latest one. The latest input gets 0 ms.)");

        var btn = MessageBox.Show(sb.ToString(), "Delay Analysis", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (btn == MessageBoxResult.Yes)
        {
            foreach (var r in outcome.Inputs)
            {
                if (double.IsNaN(r.FirstTransientMs)) continue;
                if (r.InputIndex >= 0 && r.InputIndex < Channels.Count)
                {
                    Channels[r.InputIndex].DelayMs = Math.Clamp(r.SuggestedDelayMs, 0, 1000);
                }
            }
            StatusText = "Suggested delays applied.";
        }
        else
        {
            StatusText = "Delay detection complete (not applied).";
        }
    }

    public void Dispose()
    {
        _stateServer?.Dispose();
        _meterTimer.Stop();
        _autosaveTimer.Stop();
        if (_inputDiagRecording)
            foreach (var ch in Channels) _engine.Inputs[ch.Index].StopAnalysisRecording();
        SavePreset();
        foreach (var r in _recorders) r?.Dispose();
        _engine.Dispose();
    }
}

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
    private bool _suppressAutosave;
    private bool _suppressRebuild;
    private bool _rebuildInProgress;
    private List<AudioDeviceInfo> _allInputDevices = new();
    private List<AudioDeviceInfo> _allOutputDevices = new();

    private long _lastLogTick;
    private readonly long[] _lastTotalSamples = new long[AudioEngine.OutputCount];
    private readonly int[] _lastAutoMixWinner = new int[AudioEngine.OutputCount];

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
                o, _engine.Outputs[o], _allOutputDevices,
                (idx, dev) => SetOutputDevice(idx, dev),
                (idx, mode) => _engine.SetAutoMixMode(idx, mode),
                (idx, strength) => _engine.SetAutoMixStrength(idx, strength),
                (idx, on) => _engine.SetAutoMixStableHandoff(idx, on),
                (idx, on) => _engine.SetAutoMixReferenceGuided(idx, on),
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

        _meterTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(33),
        };
        _lastLogTick = Environment.TickCount64;
        for (int o = 0; o < _lastAutoMixWinner.Length; o++) _lastAutoMixWinner[o] = -1;
        _meterTimer.Tick += (_, _) =>
        {
            foreach (var ch in Channels) ch.RefreshMeters();
            foreach (var op in Outputs) op.RefreshMeters();
            LogAutoMixSelectionChanges();
            MaybeLogDiagnostics();
        };
        _meterTimer.Start();

        TryLoadInitialPreset();
        StartStateServer();
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

    private string BuildStateJson()
    {
        static double ToDb(double lin) => lin <= 1e-6 ? -120.0 : Math.Round(20 * Math.Log10(lin), 1);
        var diag = _engine.AutoMixSnapshot();

        var channels = new List<object>(Channels.Count);
        for (int i = 0; i < Channels.Count; i++)
        {
            var ch = Channels[i];
            var input = _engine.Inputs[i];
            var gains = new double[AudioEngine.OutputCount];
            for (int o = 0; o < AudioEngine.OutputCount; o++) gains[o] = Math.Round(input.GetAutoMixGain(o), 3);
            channels.Add(new
            {
                index = i,
                label = ch.CustomLabel,
                device = ch.SelectedDevice?.FriendlyName,
                inputDb = Math.Round(ch.InputPeakDb, 1),
                postDb = Math.Round(ch.PostPeakDb, 1),
                rmsDb = ToDb(input.CurrentLevelLinear),
                envDb = i < diag.Env.Length ? ToDb(diag.Env[i]) : (double?)null,
                crest = i < diag.Crest.Length ? Math.Round(diag.Crest[i], 2) : (double?)null,
                refCorr = i < diag.Corr.Length ? Math.Round(diag.Corr[i], 3) : (double?)null,
                clarity = ch.HasClarity ? Math.Round(ch.ClarityBar, 2) : (double?)null,
                routes = ch.Routes.Select(r => r.IsOn).ToArray(),
                muted = ch.Muted,
                volumePercent = Math.Round(ch.VolumePercent, 0),
                delayMs = ch.DelayMs,
                isPriority = ch.IsPriority,
                isDucking = input.IsDucking,
                isAutoMixActive = input.IsAutoMixActive,
                automixGain = gains,
            });
        }

        var outputs = new List<object>(Outputs.Length);
        for (int o = 0; o < Outputs.Length; o++)
        {
            var op = Outputs[o];
            outputs.Add(new
            {
                index = o,
                label = op.CustomLabel,
                device = op.SelectedDevice?.FriendlyName,
                peakDb = Math.Round(op.OutputPeakDb, 1),
                volumePercent = Math.Round(op.VolumePercent, 0),
                recording = op.IsRecording,
                mode = o < diag.Mode.Length ? diag.Mode[o].ToString() : "Off",
                strengthPercent = Math.Round(op.StrengthPercent, 0),
                stableHandoff = op.StableHandoff,
                referenceGuided = op.ReferenceGuided,
                winner = o < diag.Winner.Length ? diag.Winner[o] : -1,
                winnerHold = o < diag.WinnerHold.Length ? diag.WinnerHold[o] : 0,
                activeInput = o < diag.ActiveInput.Length ? diag.ActiveInput[o] : -1,
            });
        }

        var root = new
        {
            ts = DateTime.Now.ToString("HH:mm:ss.fff"),
            inputCount = InputCount,
            status = StatusText,
            referenceInput = diag.ReferenceInput,
            channels,
            outputs,
        };
        return System.Text.Json.JsonSerializer.Serialize(
            root, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    // Logs each automixer talker hand-off (Output B: mic1 → mic3, clarity 78%). Polled per meter
    // tick (not throttled) so the trail captures fast switches, but only when file logging is on.
    private void LogAutoMixSelectionChanges()
    {
        if (!AudioLog.Enabled) return;
        for (int o = 0; o < Outputs.Length; o++)
        {
            int winner = _engine.AutoMixActiveInput(o);
            if (winner == _lastAutoMixWinner[o]) continue;
            int prev = _lastAutoMixWinner[o];
            _lastAutoMixWinner[o] = winner;
            string Name(int i) => i < 0 ? "none"
                : i < Channels.Count ? $"mic{i + 1} ('{Channels[i].CustomLabel}')" : $"mic{i + 1}";
            string clarity = winner >= 0 && winner < Channels.Count ? Channels[winner].ClarityText : "—";
            AudioLog.Write($"Output {(o == 0 ? "A" : "B")} auto-mix: {Name(prev)} → {Name(winner)} (clarity {clarity})");
        }
    }

    // Throttled to ~1 Hz. Short-circuits when file logging is off so we don't build the per-output
    // / per-input strings every meter tick on a normal run (logging is opt-in via AUDIOMIXER_LOG).
    private void MaybeLogDiagnostics()
    {
        if (!AudioLog.Enabled) return;
        long now = Environment.TickCount64;
        long elapsedMs = now - _lastLogTick;
        if (elapsedMs <= 1000) return;
        _lastLogTick = now;

        for (int o = 0; o < Outputs.Length; o++)
        {
            var bus = _engine.Outputs[o];
            long total = bus.TotalSamplesRead;
            long delta = total - _lastTotalSamples[o];
            // A bus restart (input-count change) resets TotalSamplesRead, so a stale
            // baseline yields a bogus negative delta — count from restart instead.
            if (delta < 0) delta = total;
            _lastTotalSamples[o] = total;
            long samplesPerSec = delta * 1000 / elapsedMs;
            AudioLog.Write(
                $"Output {o}: playing={bus.IsPlaying} samplesPerSec={samplesPerSec} peakDb={Outputs[o].OutputPeakDb:F1}");
        }
        for (int i = 0; i < Channels.Count; i++)
        {
            var dev = Channels[i].SelectedDevice;
            if (dev == null) continue;
            var bufMs = string.Join(",", Enumerable.Range(0, Outputs.Length)
                .Select(o => _engine.Inputs[i].BufferedMs(o).ToString()));
            var readSamples = string.Join(",", Enumerable.Range(0, Outputs.Length)
                .Select(o => _engine.Inputs[i].ReadSamplesForOutput(o).ToString()));
            var readCalls = string.Join(",", Enumerable.Range(0, Outputs.Length)
                .Select(o => _engine.Inputs[i].ReadCallsForOutput(o).ToString()));
            AudioLog.Write(
                $"Input {i} ('{dev.FriendlyName}'): inputDb={Channels[i].InputPeakDb:F1} postDb={Channels[i].PostPeakDb:F1} routes=[{string.Join(",", Channels[i].Routes.Select(r => r.IsOn ? "1" : "0"))}] mute={Channels[i].Muted} bufMs=[{bufMs}] readCalls=[{readCalls}] readSamples=[{readSamples}]");
        }
    }

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

    private void SetInputDevice(int index, AudioDeviceInfo? device)
    {
        try
        {
            _engine.SetInputDevice(index, device);
            StatusText = device == null ? $"Input {index + 1}: (none)" : $"Input {index + 1}: {device.FriendlyName}";
        }
        catch (Exception ex)
        {
            StatusText = $"Input {index + 1} error: {ex.Message}";
        }
        if (!_suppressRebuild) RebuildAvailableDevices();
    }

    private void SetOutputDevice(int index, AudioDeviceInfo? device)
    {
        try
        {
            _engine.SetOutputDevice(index, device);
            StatusText = device == null ? $"Output {(index == 0 ? "A" : "B")}: (none)" : $"Output {(index == 0 ? "A" : "B")}: {device.FriendlyName}";
        }
        catch (Exception ex)
        {
            StatusText = $"Output {(index == 0 ? "A" : "B")} error: {ex.Message}";
        }
        if (!_suppressRebuild) RebuildAvailableDevices();
    }

    private void RebuildAvailableDevices()
    {
        if (_rebuildInProgress) return;
        _rebuildInProgress = true;
        try
        {
            for (int i = 0; i < Channels.Count; i++)
            {
                var excluded = new HashSet<string>();
                for (int j = 0; j < Channels.Count; j++)
                {
                    if (j == i) continue;
                    var id = Channels[j].SelectedDevice?.Id;
                    if (!string.IsNullOrEmpty(id)) excluded.Add(id);
                }
                Channels[i].RefreshDevices(_allInputDevices.Where(d => !excluded.Contains(d.Id)));
            }
            for (int o = 0; o < Outputs.Length; o++)
            {
                var excluded = new HashSet<string>();
                for (int p = 0; p < Outputs.Length; p++)
                {
                    if (p == o) continue;
                    var id = Outputs[p].SelectedDevice?.Id;
                    if (!string.IsNullOrEmpty(id)) excluded.Add(id);
                }
                Outputs[o].RefreshDevices(_allOutputDevices.Where(d => !excluded.Contains(d.Id)));
            }
        }
        finally
        {
            _rebuildInProgress = false;
        }
    }

    private void DedupeAndRebuild()
    {
        var seenIn = new HashSet<string>();
        foreach (var ch in Channels)
        {
            var id = ch.SelectedDevice?.Id;
            if (string.IsNullOrEmpty(id)) continue;
            if (!seenIn.Add(id)) ch.SelectedDevice = null;
        }
        var seenOut = new HashSet<string>();
        foreach (var op in Outputs)
        {
            var id = op.SelectedDevice?.Id;
            if (string.IsNullOrEmpty(id)) continue;
            if (!seenOut.Add(id)) op.SelectedDevice = null;
        }
        RebuildAvailableDevices();
    }

    private void ResyncAudio()
    {
        try
        {
            _engine.RestartInputs();
            _engine.RestartOutputs();
            StatusText = "Audio resynced (inputs + outputs).";
        }
        catch (Exception ex)
        {
            StatusText = $"Resync failed: {ex.Message}";
        }
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
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
        string tag = index == 0 ? "A" : "B";
        string path = Path.Combine(folder, $"mix-{tag}-{DateTime.Now:yyyyMMdd-HHmmss}.wav");
        try
        {
            recorder.Start(path, bus.InternalFormat);
            bus.Recorder = recorder;
            ovm.SetRecording(true);
            StatusText = $"Recording {ovm.CustomLabel} → {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            StatusText = $"Record failed: {ex.Message}";
        }
    }

    private void OnSettingChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_suppressAutosave) return;
        if (e.PropertyName is nameof(ChannelViewModel.InputPeakDb)
            or nameof(ChannelViewModel.PostPeakDb)
            or nameof(ChannelViewModel.InputPeakHoldDb)
            or nameof(ChannelViewModel.PostPeakHoldDb)
            or nameof(OutputViewModel.OutputPeakDb)
            or nameof(OutputViewModel.OutputPeakHoldDb))
        {
            return;
        }
        _autosaveTimer.Stop();
        _autosaveTimer.Start();
    }

    private void SavePreset()
    {
        var preset = new MixerPreset
        {
            Name = "Default",
            VbCablePromptDismissed = _vbCablePromptDismissed,
            Channels = Channels.Select(c => new ChannelPreset
            {
                CustomLabel = c.CustomLabel,
                DeviceId = c.SelectedDevice?.Id,
                DeviceName = c.SelectedDevice?.FriendlyName,
                VolumePercent = c.VolumePercent,
                Muted = c.Muted,
                DelayMs = c.DelayMs,
                Priority = c.IsPriority,
                Routes = c.Routes.Select(r => r.IsOn).ToArray(),
            }).ToArray(),
            Outputs = Outputs.Select(o => new OutputPreset
            {
                CustomLabel = o.CustomLabel,
                DeviceId = o.SelectedDevice?.Id,
                DeviceName = o.SelectedDevice?.FriendlyName,
                AutoMixMode = o.AutoMixModeIndex,
                AutoMixStrength = o.StrengthPercent,
                AutoMixStableHandoff = o.StableHandoff,
                AutoMixReferenceGuided = o.ReferenceGuided,
                Volume = o.VolumePercent,
            }).ToArray(),
        };
        try
        {
            _presetStore.Save(preset);
            StatusText = $"Saved {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            StatusText = $"Save failed: {ex.Message}";
        }
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

            for (int i = 0; i < Channels.Count && i < preset.Channels.Length; i++)
            {
                var cp = preset.Channels[i];
                if (!string.IsNullOrEmpty(cp.CustomLabel)) Channels[i].CustomLabel = cp.CustomLabel;
                var match = cp.DeviceId == null ? null :
                    Channels[i].AvailableDevices.FirstOrDefault(d => d.Id == cp.DeviceId);
                Channels[i].SelectedDevice = match;
                Channels[i].VolumePercent = cp.VolumePercent;
                Channels[i].Muted = cp.Muted;
                Channels[i].DelayMs = cp.DelayMs;
                Channels[i].IsPriority = cp.Priority;
                for (int r = 0; r < Channels[i].Routes.Length && r < cp.Routes.Length; r++)
                {
                    Channels[i].Routes[r].IsOn = cp.Routes[r];
                }
            }
            for (int o = 0; o < Outputs.Length && o < preset.Outputs.Length; o++)
            {
                var op = preset.Outputs[o];
                if (!string.IsNullOrEmpty(op.CustomLabel)) Outputs[o].CustomLabel = op.CustomLabel;
                var match = op.DeviceId == null ? null :
                    Outputs[o].AvailableDevices.FirstOrDefault(d => d.Id == op.DeviceId);
                Outputs[o].SelectedDevice = match;
                Outputs[o].StrengthPercent = Math.Clamp(op.AutoMixStrength, 0f, 100f);
                Outputs[o].StableHandoff = op.AutoMixStableHandoff;
                Outputs[o].ReferenceGuided = op.AutoMixReferenceGuided;
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

        try
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
        }
        catch (Exception ex)
        {
            StatusText = $"Input recording failed: {ex.Message}";
        }
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

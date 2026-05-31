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
    private readonly MixRecorder _recorder = new();
    private bool _suppressAutosave;
    private bool _suppressRebuild;
    private bool _rebuildInProgress;
    private List<AudioDeviceInfo> _allInputDevices = new();
    private List<AudioDeviceInfo> _allOutputDevices = new();

    public ChannelViewModel[] Channels { get; }
    public OutputViewModel[] Outputs { get; }

    public RelayCommand RefreshDevicesCommand { get; }
    public RelayCommand ToggleRecordCommand { get; }
    public RelayCommand DetectDelaysCommand { get; }
    public RelayCommand ResyncAudioCommand { get; }

    private string _statusText = "Idle";
    public string StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value);
    }

    private bool _isRecording;
    public bool IsRecording
    {
        get => _isRecording;
        private set
        {
            if (SetField(ref _isRecording, value))
            {
                RaisePropertyChanged(nameof(RecordButtonText));
                RaisePropertyChanged(nameof(RecordIcon));
                RaisePropertyChanged(nameof(RecordTooltip));
            }
        }
    }

    public string RecordButtonText => IsRecording ? "Stop Recording" : "Record Mix";
    public string RecordIcon => IsRecording ? "" : "";
    public string RecordTooltip => IsRecording ? "Stop recording" : "Record mix to WAV";

    private int _recordFromOutputIndex = 0;
    public int RecordFromOutputIndex
    {
        get => _recordFromOutputIndex;
        set
        {
            if (SetField(ref _recordFromOutputIndex, value))
                RaisePropertyChanged(nameof(CurrentRecordSourceLabel));
        }
    }

    public string[] RecordSourceOptions { get; } = new[] { "A", "B" };
    public string CurrentRecordSourceLabel =>
        RecordSourceOptions[Math.Clamp(_recordFromOutputIndex, 0, RecordSourceOptions.Length - 1)];

    public MainViewModel()
    {
        _engine = new AudioEngine();

        _allInputDevices = AudioDeviceInfo.Enumerate(DataFlow.Capture);
        _allOutputDevices = AudioDeviceInfo.Enumerate(DataFlow.Render);

        Channels = new ChannelViewModel[AudioEngine.InputCount];
        for (int i = 0; i < AudioEngine.InputCount; i++)
        {
            Channels[i] = new ChannelViewModel(
                i, _engine.Inputs[i], _allInputDevices, AudioEngine.OutputCount,
                (idx, dev) => SetInputDevice(idx, dev));
        }
        Channels[0].Routes[0].IsOn = true;
        if (Channels[0].Routes.Length > 1) Channels[0].Routes[1].IsOn = true;

        Outputs = new OutputViewModel[AudioEngine.OutputCount];
        for (int o = 0; o < AudioEngine.OutputCount; o++)
        {
            Outputs[o] = new OutputViewModel(
                o, _engine.Outputs[o], _allOutputDevices,
                (idx, dev) => SetOutputDevice(idx, dev));
        }

        RefreshDevicesCommand = new RelayCommand(RefreshDevices);
        ToggleRecordCommand = new RelayCommand(ToggleRecord);
        DetectDelaysCommand = new RelayCommand(StartDelayDetection);
        ResyncAudioCommand = new RelayCommand(ResyncAudio);

        _autosaveTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _autosaveTimer.Tick += (_, _) =>
        {
            _autosaveTimer.Stop();
            SavePreset();
        };

        foreach (var ch in Channels)
        {
            ch.PropertyChanged += OnSettingChanged;
            foreach (var r in ch.Routes) r.PropertyChanged += OnSettingChanged;
        }
        foreach (var op in Outputs) op.PropertyChanged += OnSettingChanged;

        _meterTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(33),
        };
        long _lastLogTick = 0;
        long[] _lastTotalSamples = new long[Outputs.Length];
        _meterTimer.Tick += (_, _) =>
        {
            foreach (var ch in Channels) ch.RefreshMeters();
            foreach (var op in Outputs) op.RefreshMeters();

            long now = Environment.TickCount64;
            if (now - _lastLogTick > 1000)
            {
                _lastLogTick = now;
                for (int o = 0; o < Outputs.Length; o++)
                {
                    var bus = _engine.Outputs[o];
                    long total = bus.TotalSamplesRead;
                    long delta = total - _lastTotalSamples[o];
                    _lastTotalSamples[o] = total;
                    Audio.AudioLog.Write(
                        $"Output {o}: playing={bus.IsPlaying} samplesPerSec={delta} peakDb={Outputs[o].OutputPeakDb:F1}");
                }
                for (int i = 0; i < Channels.Length; i++)
                {
                    var dev = Channels[i].SelectedDevice;
                    if (dev != null)
                    {
                        var bufMs = string.Join(",", Enumerable.Range(0, Outputs.Length)
                            .Select(o => _engine.Inputs[i].BufferedMs(o).ToString()));
                        var readSamples = string.Join(",", Enumerable.Range(0, Outputs.Length)
                            .Select(o => _engine.Inputs[i].ReadSamplesForOutput(o).ToString()));
                        var readCalls = string.Join(",", Enumerable.Range(0, Outputs.Length)
                            .Select(o => _engine.Inputs[i].ReadCallsForOutput(o).ToString()));
                        Audio.AudioLog.Write(
                            $"Input {i} ('{dev.FriendlyName}'): inputDb={Channels[i].InputPeakDb:F1} postDb={Channels[i].PostPeakDb:F1} routes=[{string.Join(",", Channels[i].Routes.Select(r => r.IsOn ? "1" : "0"))}] mute={Channels[i].Muted} bufMs=[{bufMs}] readCalls=[{readCalls}] readSamples=[{readSamples}]");
                    }
                }
            }
        };
        _meterTimer.Start();

        TryLoadInitialPreset();
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
            for (int i = 0; i < Channels.Length; i++)
            {
                var excluded = new HashSet<string>();
                for (int j = 0; j < Channels.Length; j++)
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
            _engine.RestartOutputs();
            StatusText = "Audio resynced.";
        }
        catch (Exception ex)
        {
            StatusText = $"Resync failed: {ex.Message}";
        }
    }

    private void RefreshDevices()
    {
        _allInputDevices = AudioDeviceInfo.Enumerate(DataFlow.Capture);
        _allOutputDevices = AudioDeviceInfo.Enumerate(DataFlow.Render);
        DedupeAndRebuild();
        StatusText = $"Refreshed: {_allInputDevices.Count} inputs, {_allOutputDevices.Count} outputs";
    }

    private void ToggleRecord()
    {
        if (IsRecording)
        {
            int idx = RecordFromOutputIndex;
            if (idx >= 0 && idx < Outputs.Length) _engine.Outputs[idx].Recorder = null;
            _recorder.Stop();
            IsRecording = false;
            StatusText = $"Recording stopped. Saved to: {_recorder.CurrentPath}";
        }
        else
        {
            int idx = Math.Clamp(RecordFromOutputIndex, 0, Outputs.Length - 1);
            var bus = _engine.Outputs[idx];
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "AudioMixer", "recordings");
            string path = Path.Combine(folder, $"mix-{DateTime.Now:yyyyMMdd-HHmmss}.wav");
            try
            {
                _recorder.Start(path, bus.InternalFormat);
                bus.Recorder = _recorder;
                IsRecording = true;
                StatusText = $"Recording {Outputs[idx].Label} → {Path.GetFileName(path)}";
            }
            catch (Exception ex)
            {
                StatusText = $"Record failed: {ex.Message}";
            }
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
            Channels = Channels.Select(c => new ChannelPreset
            {
                CustomLabel = c.CustomLabel,
                DeviceId = c.SelectedDevice?.Id,
                DeviceName = c.SelectedDevice?.FriendlyName,
                VolumePercent = c.VolumePercent,
                Muted = c.Muted,
                DelayMs = c.DelayMs,
                Routes = c.Routes.Select(r => r.IsOn).ToArray(),
            }).ToArray(),
            Outputs = Outputs.Select(o => new OutputPreset
            {
                CustomLabel = o.CustomLabel,
                DeviceId = o.SelectedDevice?.Id,
                DeviceName = o.SelectedDevice?.FriendlyName,
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
            for (int i = 0; i < Channels.Length && i < preset.Channels.Length; i++)
            {
                var cp = preset.Channels[i];
                if (!string.IsNullOrEmpty(cp.CustomLabel)) Channels[i].CustomLabel = cp.CustomLabel;
                var match = cp.DeviceId == null ? null :
                    Channels[i].AvailableDevices.FirstOrDefault(d => d.Id == cp.DeviceId);
                Channels[i].SelectedDevice = match;
                Channels[i].VolumePercent = cp.VolumePercent;
                Channels[i].Muted = cp.Muted;
                Channels[i].DelayMs = cp.DelayMs;
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
            }
        }
        finally
        {
            _suppressAutosave = false;
            _suppressRebuild = false;
        }
        DedupeAndRebuild();
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
        sb.AppendLine("Detected transient arrival times (post-resample, pre-processing):");
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
                sb.AppendLine($"  {label}: arrived at {r.FirstTransientMs,7:F1} ms   →   suggested delay: {r.SuggestedDelayMs} ms");
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
                if (r.InputIndex >= 0 && r.InputIndex < Channels.Length)
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
        _meterTimer.Stop();
        _autosaveTimer.Stop();
        SavePreset();
        _recorder.Dispose();
        _engine.Dispose();
    }
}

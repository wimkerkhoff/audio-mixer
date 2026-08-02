using System.Threading;
using NAudio.Wave;

namespace AudioMixer.Audio;

public sealed class AudioEngine : IDisposable, IAutoMixControl
{
    public const int DefaultInputCount = 3;
    public const int MinInputCount = 1;
    public const int MaxInputCount = 10;
    public const int OutputCount = 2;

    public InputChannel[] Inputs { get; private set; }
    public OutputBus[] Outputs { get; }

    private AudioDeviceInfo?[] _inputDevices = new AudioDeviceInfo?[DefaultInputCount];
    private readonly AudioDeviceInfo?[] _outputDevices = new AudioDeviceInfo?[OutputCount];
    private readonly object _lock = new();

    private readonly AutoMixer _autoMix = new(OutputCount, MaxInputCount);
    private readonly Timer _autoMixTimer;

    // Capture-stall watchdog: a selected input that stops delivering buffers for StallMs is restarted
    // (Anker speakerphones drop on USB/BT renegotiation). Backoff + attempt cap stop a permanently-
    // gone device from restart-looping. Fires InputRestarted (index, attempt) for status surfacing.
    private const int StallMs = 1500;
    private const int RestartBackoffMs = 3000;
    private const int MaxRestartAttempts = 5;
    private readonly Timer _watchdogTimer;
    private readonly int[] _inputRestarting = new int[MaxInputCount];
    private readonly long[] _lastRestartTicks = new long[MaxInputCount];
    private readonly int[] _restartAttempts = new int[MaxInputCount];
    private readonly bool[] _restartGaveUp = new bool[MaxInputCount];

    public event Action<int, int>? InputRestarted;        // (index, attempt)
    public event Action<int>? InputRestartGaveUp;         // (index)

    public AudioEngine()
    {
        Inputs = new InputChannel[DefaultInputCount];
        for (int i = 0; i < DefaultInputCount; i++) Inputs[i] = new InputChannel(OutputCount);
        Outputs = new OutputBus[OutputCount];
        for (int o = 0; o < OutputCount; o++) Outputs[o] = new OutputBus();
        _autoMixTimer = new Timer(AutoMixTick, null, 10, 10);
        _watchdogTimer = new Timer(WatchdogTick, null, 1000, 500);
    }

    public void SetAutoMixMode(int output, AutoMixMode mode) => _autoMix.SetMode(output, mode);
    public void SetAutoMixStrength(int output, float strength) => _autoMix.SetStrength(output, strength);
    public void SetAutoMixStableHandoff(int output, bool on) => _autoMix.SetStableHandoff(output, on);
    public void SetAutoMixReferenceGuided(int output, bool on) => _autoMix.SetReferenceGuided(output, on);
    public void SetAutoMixPreferNatural(int output, bool on) => _autoMix.SetPreferNatural(output, on);
    public int AutoMixActiveInput(int output) => _autoMix.ActiveInput(output);
    public AutoMixDiag AutoMixSnapshot() => _autoMix.Snapshot(Inputs.Length);

    private bool _autoMixErrorLogged;

    private void AutoMixTick(object? state)
    {
        var inputs = Inputs; // single atomic reference read; safe vs SetInputCount's array swap
        try { _autoMix.Tick(inputs); }
        catch (Exception ex)
        {
            // A throwing Tick leaves every automix gain frozen at its last value — the mixer keeps
            // playing but stops selecting. Latched so a repeating fault can't flood at 100 Hz.
            if (_autoMixErrorLogged) return;
            _autoMixErrorLogged = true;
            System.Diagnostics.Trace.WriteLine($"AutoMixer.Tick failed: {ex}");
            AudioLog.Write($"AutoMixer.Tick failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void WatchdogTick(object? state)
    {
        InputChannel[] inputs;
        AudioDeviceInfo?[] devices;
        lock (_lock)
        {
            inputs = Inputs;
            devices = (AudioDeviceInfo?[])_inputDevices.Clone();
        }

        long now = Environment.TickCount64;
        for (int i = 0; i < inputs.Length && i < MaxInputCount; i++)
        {
            var dev = i < devices.Length ? devices[i] : null;
            var ch = inputs[i];
            if (dev == null || !ch.IsCapturing) continue;

            if (now - ch.LastDataTicks < StallMs)
            {
                // Healthy again — clear the backoff so a future stall gets a fresh budget.
                _restartAttempts[i] = 0;
                _restartGaveUp[i] = false;
                continue;
            }

            if (_restartAttempts[i] >= MaxRestartAttempts)
            {
                if (!_restartGaveUp[i]) { _restartGaveUp[i] = true; InputRestartGaveUp?.Invoke(i); }
                continue;
            }
            if (now - _lastRestartTicks[i] < RestartBackoffMs) continue;
            if (Interlocked.CompareExchange(ref _inputRestarting[i], 1, 0) != 0) continue;

            _lastRestartTicks[i] = now;
            int attempt = ++_restartAttempts[i];
            int idx = i;
            Task.Run(() => RestartInput(idx, dev, attempt));
        }
    }

    private void RestartInput(int index, AudioDeviceInfo device, int attempt)
    {
        try
        {
            lock (_lock)
            {
                if (index >= Inputs.Length) return;
                if (!ReferenceEquals(_inputDevices[index], device)) return; // device changed under us
                Inputs[index].Stop();
                Inputs[index].Start(device);
            }
            InputRestarted?.Invoke(index, attempt);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"Watchdog restart of input {index} failed: {ex}");
        }
        finally
        {
            Volatile.Write(ref _inputRestarting[index], 0);
        }
    }

    // Manual recovery (Resync button): restart every selected input's capture from scratch.
    public void RestartInputs()
    {
        lock (_lock)
        {
            for (int i = 0; i < Inputs.Length; i++)
            {
                var dev = _inputDevices[i];
                if (dev == null) continue;
                Inputs[i].Stop();
                try { Inputs[i].Start(dev); }
                catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"Resync input {i} failed: {ex}"); }
                if (i < MaxInputCount) { _restartAttempts[i] = 0; _lastRestartTicks[i] = 0; _restartGaveUp[i] = false; }
            }
        }
    }

    public int InputCount => Inputs.Length;

    public void SetInputCount(int count)
    {
        count = Math.Clamp(count, MinInputCount, MaxInputCount);
        lock (_lock)
        {
            int old = Inputs.Length;
            if (count == old) return;

            for (int i = count; i < old; i++)
            {
                Inputs[i].Stop();
                Inputs[i].Dispose();
            }

            var newInputs = new InputChannel[count];
            var newDevices = new AudioDeviceInfo?[count];
            for (int i = 0; i < count; i++)
            {
                newInputs[i] = i < old ? Inputs[i] : new InputChannel(OutputCount);
                newDevices[i] = i < old ? _inputDevices[i] : null;
            }
            Inputs = newInputs;
            _inputDevices = newDevices;

            for (int o = 0; o < OutputCount; o++) RestartOutputBus_NoLock(o);
        }
    }

    public void SetInputDevice(int index, AudioDeviceInfo? device)
    {
        lock (_lock)
        {
            _inputDevices[index] = device;
            Inputs[index].Stop();
            if (device != null) Inputs[index].Start(device);
            if (index < MaxInputCount)
            {
                _restartAttempts[index] = 0;
                _lastRestartTicks[index] = 0;
                _restartGaveUp[index] = false;
            }
        }
    }

    public void SetOutputDevice(int index, AudioDeviceInfo? device)
    {
        lock (_lock)
        {
            _outputDevices[index] = device;
            RestartOutputBus_NoLock(index);
        }
    }

    public void RestartOutputs()
    {
        lock (_lock)
        {
            for (int o = 0; o < OutputCount; o++) RestartOutputBus_NoLock(o);
        }
    }

    private void RestartOutputBus_NoLock(int index)
    {
        Outputs[index].Stop();
        var device = _outputDevices[index];
        if (device == null) return;

        var providers = new List<ISampleProvider>(Inputs.Length);
        for (int i = 0; i < Inputs.Length; i++)
        {
            Inputs[i].ClearOutputBuffer(index);
            providers.Add(Inputs[i].GetProviderForOutput(index));
        }
        Outputs[index].Start(device, providers);
    }

    public void StopAll()
    {
        lock (_lock)
        {
            foreach (var input in Inputs) input.Stop();
            foreach (var output in Outputs) output.Stop();
        }
    }

    public void Dispose()
    {
        _watchdogTimer.Dispose();
        _autoMixTimer.Dispose();
        StopAll();
        foreach (var input in Inputs) input.Dispose();
        foreach (var output in Outputs) output.Dispose();
    }
}

using NAudio.Wave;

namespace AudioMixer.Audio;

public sealed class AudioEngine : IDisposable
{
    public const int InputCount = 3;
    public const int OutputCount = 2;

    public InputChannel[] Inputs { get; }
    public OutputBus[] Outputs { get; }

    private readonly AudioDeviceInfo?[] _inputDevices = new AudioDeviceInfo?[InputCount];
    private readonly AudioDeviceInfo?[] _outputDevices = new AudioDeviceInfo?[OutputCount];
    private readonly object _lock = new();

    public AudioEngine()
    {
        Inputs = new InputChannel[InputCount];
        for (int i = 0; i < InputCount; i++) Inputs[i] = new InputChannel(OutputCount);
        Outputs = new OutputBus[OutputCount];
        for (int o = 0; o < OutputCount; o++) Outputs[o] = new OutputBus();
    }

    public void SetInputDevice(int index, AudioDeviceInfo? device)
    {
        lock (_lock)
        {
            _inputDevices[index] = device;
            Inputs[index].Stop();
            if (device != null) Inputs[index].Start(device);
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

        var providers = new List<ISampleProvider>(InputCount);
        for (int i = 0; i < InputCount; i++)
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
        StopAll();
        foreach (var input in Inputs) input.Dispose();
        foreach (var output in Outputs) output.Dispose();
    }
}

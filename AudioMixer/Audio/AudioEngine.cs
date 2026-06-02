using NAudio.Wave;

namespace AudioMixer.Audio;

public sealed class AudioEngine : IDisposable
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

    public AudioEngine()
    {
        Inputs = new InputChannel[DefaultInputCount];
        for (int i = 0; i < DefaultInputCount; i++) Inputs[i] = new InputChannel(OutputCount);
        Outputs = new OutputBus[OutputCount];
        for (int o = 0; o < OutputCount; o++) Outputs[o] = new OutputBus();
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
        StopAll();
        foreach (var input in Inputs) input.Dispose();
        foreach (var output in Outputs) output.Dispose();
    }
}

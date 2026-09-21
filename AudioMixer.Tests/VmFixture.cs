using AudioMixer.Audio;
using NAudio.CoreAudioApi;
using AudioMixer.ViewModels;

namespace AudioMixer.Tests;

/// <summary>
/// A live-shaped mixer with no devices in it.
///
/// `new AudioEngine()` allocates channels, buses and its timers but opens nothing — devices only
/// appear on Start — so the view models, the state snapshot and the session recorder can all be
/// exercised against a real engine rather than a mock of one. That matters here: the things these
/// fixtures test are exactly the wiring between those objects, which a mock would define away.
/// </summary>
internal sealed class VmFixture : IDisposable
{
    public AudioEngine Engine { get; }
    public List<ChannelViewModel> Channels { get; } = new();
    public List<OutputViewModel> Outputs { get; } = new();

    private sealed class NullAutoMix : IAutoMixControl
    {
        public void SetAutoMixMode(int output, AutoMixMode mode) { }
    }

    public static readonly AudioDeviceInfo Lapel = new("dev-lapel", "Wireless PRO RX", DataFlow.Capture);
    public static readonly AudioDeviceInfo Cable = new("dev-cable", "CABLE Input (VB-Audio Virtual Cable)", DataFlow.Render);

    public VmFixture(int inputs = 3)
    {
        Engine = new AudioEngine();
        var devices = new[] { Lapel, Cable };

        for (int i = 0; i < inputs && i < Engine.Inputs.Length; i++)
            Channels.Add(new ChannelViewModel(i, Engine.Inputs[i], devices, AudioEngine.OutputCount,
                                              (_, _) => { }));

        for (int o = 0; o < AudioEngine.OutputCount; o++)
            Outputs.Add(new OutputViewModel(o, Engine.Outputs[o], new NullAutoMix(), devices, (_, _) => { }));
    }

    public void Dispose() => Engine.Dispose();
}

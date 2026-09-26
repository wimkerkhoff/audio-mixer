using System.IO;
using AudioMixer.Audio;
using NAudio.Wave;

namespace AudioMixer.Tests;

public sealed class MixRecorderTests : IDisposable
{
    private const int Rate = 48000;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "AudioMixerTests-" + Guid.NewGuid());
    private static readonly WaveFormat Mono = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 1);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string NewPath() => Path.Combine(_dir, Guid.NewGuid() + ".wav");

    private static long Frames(string path)
    {
        using var r = new WaveFileReader(path);
        return r.SampleCount;
    }

    /// <summary>An unplugged receiver used to leave its outage out, so the rest of the file ran early.</summary>
    [Fact]
    public void AGap_IsWrittenAsSilence()
    {
        string path = NewPath();
        using (var rec = new MixRecorder())
        {
            rec.Start(path, Mono);
            rec.WriteSamples(new float[Rate / 10], 0, Rate / 10);   // 0.1 s, then the device goes away
            rec.PadTo(TimeSpan.FromSeconds(2));
        }
        Assert.Equal(2 * Rate, Frames(path));
    }

    /// <summary>Device clocks drift by ppm; that is not a gap and must not insert silence.</summary>
    [Fact]
    public void AShortfallUnderTheThreshold_IsLeftAlone()
    {
        string path = NewPath();
        using (var rec = new MixRecorder())
        {
            rec.Start(path, Mono);
            rec.WriteSamples(new float[Rate], 0, Rate);
            rec.PadTo(TimeSpan.FromSeconds(1.3));
        }
        Assert.Equal(Rate, Frames(path));
    }

    [Fact]
    public void AFileThatJoinsLate_StartsWithItsLeadIn()
    {
        string path = NewPath();
        using (var rec = new MixRecorder())
            rec.Start(path, Mono, joinedLate: TimeSpan.FromSeconds(3));

        long frames = Frames(path);
        Assert.InRange(frames, 3 * Rate, 3 * Rate + Rate / 2);
    }

    /// <summary>
    /// 2026-09-26: a Resync restarted every capture and each restart closed that mic's diag WAV, so
    /// all seven stopped eight minutes into the service while the UI still said "recording".
    /// </summary>
    [Fact]
    public void ACaptureStop_KeepsTheMicRecording_OnlyDisposeEndsIt()
    {
        var ch = new InputChannel(2);
        ch.StartAnalysisRecording(NewPath());

        ch.Stop();
        Assert.True(ch.HasAnalysisRecorder);

        ch.Dispose();
        Assert.False(ch.HasAnalysisRecorder);
    }
}

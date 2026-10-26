namespace AudioMixer.Audio;

/// <summary>
/// How loud each room mic normally hears the lapel's wearer, relative to the lapel: a running median
/// of (room dB - lapel dB), learned while the lapel is on air and its wearer is talking. A room mic
/// well above its own figure is hearing someone else. A fixed margin cannot do this, because the
/// figure is placement: on 2026-10-25 B2 sat by the presenter and read him at +0.2 dB over his own
/// lapel, A1 at -4.6, C1 at -9 — stable to a few tenths across both halves of the teaching.
///
/// 1 dB bins, halved as they fill so a moved mic is relearned in minutes. Frames where the room mic
/// is silent are the caller's to skip: an RF dropout reads -120 dB, and C1's 22% of them dragged
/// its median from -9.0 to -13.4, which made its ordinary pickup of the presenter look like a talker.
/// </summary>
public sealed class PresenterCoupling
{
    private const int MinDb = -40, MaxDb = 30, Bins = MaxDb - MinDb + 1;

    /// <summary>~10 s of the presenter talking before a mic's figure is trusted.</summary>
    public const int MinSamples = 1000;

    /// <summary>~5 min at 100 Hz: halving then keeps the figure recent.</summary>
    public const int HalveAt = 30000;

    private readonly int[,] _counts;
    private readonly int[] _total;

    public PresenterCoupling(int channels)
    {
        _counts = new int[channels, Bins];
        _total = new int[channels];
    }

    public void Add(int channel, double roomMinusLapelDb)
    {
        if (channel < 0 || channel >= _total.Length || double.IsNaN(roomMinusLapelDb)) return;
        int b = (int)Math.Round(Math.Clamp(roomMinusLapelDb, MinDb, MaxDb)) - MinDb;
        _counts[channel, b]++;
        if (++_total[channel] < HalveAt) return;
        int t = 0;
        for (int k = 0; k < Bins; k++) { _counts[channel, k] /= 2; t += _counts[channel, k]; }
        _total[channel] = t;
    }

    /// <summary>The median, or null until <see cref="MinSamples"/> frames have been seen.</summary>
    public double? Median(int channel)
    {
        if (channel < 0 || channel >= _total.Length || _total[channel] < MinSamples) return null;
        int half = (_total[channel] + 1) / 2, run = 0;
        for (int k = 0; k < Bins; k++)
        {
            run += _counts[channel, k];
            if (run >= half) return MinDb + k;
        }
        return MaxDb;
    }

    public void Reset(int channel)
    {
        if (channel < 0 || channel >= _total.Length) return;
        for (int k = 0; k < Bins; k++) _counts[channel, k] = 0;
        _total[channel] = 0;
    }

    public void ResetAll()
    {
        for (int i = 0; i < _total.Length; i++) Reset(i);
    }
}

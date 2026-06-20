using System.Threading;

namespace AudioMixer.Audio;

public sealed class PeakMeter
{
    private long _peakBits;
    private long _holdPeakBits;
    private DateTime _holdUntil;

    private static readonly TimeSpan HoldDuration = TimeSpan.FromMilliseconds(1200);

    public void Observe(float[] samples, int count) => Observe(samples, 0, count);

    public void Observe(float[] samples, int offset, int count)
    {
        if (count <= 0) return;
        float p = 0f;
        int end = offset + count;
        for (int i = offset; i < end; i++)
        {
            float a = samples[i];
            if (a < 0f) a = -a;
            if (a > p) p = a;
        }
        Interlocked.Exchange(ref _peakBits, BitConverter.SingleToInt32Bits(p));

        long holdPrev = Interlocked.Read(ref _holdPeakBits);
        float holdPrevF = BitConverter.Int32BitsToSingle((int)holdPrev);
        if (p >= holdPrevF || DateTime.UtcNow > _holdUntil)
        {
            Interlocked.Exchange(ref _holdPeakBits, BitConverter.SingleToInt32Bits(p));
            _holdUntil = DateTime.UtcNow + HoldDuration;
        }
    }

    // Zero the meter so a stopped/stalled channel's bar drops instead of freezing at its last value.
    public void Reset()
    {
        Interlocked.Exchange(ref _peakBits, 0);
        Interlocked.Exchange(ref _holdPeakBits, 0);
        _holdUntil = DateTime.UtcNow;
    }

    public float CurrentLinear => BitConverter.Int32BitsToSingle((int)Interlocked.Read(ref _peakBits));

    public float HoldLinear => BitConverter.Int32BitsToSingle((int)Interlocked.Read(ref _holdPeakBits));

    public float CurrentDb => ToDb(CurrentLinear);
    public float HoldDb => ToDb(HoldLinear);

    public static float ToDb(float linear)
    {
        if (linear <= 1e-7f) return -120f;
        return 20f * MathF.Log10(linear);
    }
}

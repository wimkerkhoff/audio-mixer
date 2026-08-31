namespace AudioMixer.Audio;

/// <summary>
/// Speech-vs-floor level histogram, used to set transmitter gain against a target (RODE-PRO-RIG.md).
///
/// A peak meter cannot answer "is this mic at the right level": a DSP-free wireless mic's crest
/// factor is ~20 dB, so its peak says almost nothing about where speech sits. Every capture buffer's
/// RMS is tallied into 1 dB bins — voiced buffers separately from the rest — and the medians read out
/// as "speech" and "room floor".
///
/// Deliberately CUMULATIVE until <see cref="Reset"/>, unlike the delta-based RF counters: an operator
/// turning a gain knob needs a number that settles, not a one-second sample. That also means it must
/// be reset by hand after a gain change, or the pre-change buffers keep dragging the median.
/// </summary>
public sealed class CalibrationHistogram
{
    public const int Bins = 91;     // -90..0 dBFS inclusive, 1 dB per bin
    public const int MinDb = -90;

    private readonly int[] _voiced = new int[Bins];
    private readonly int[] _quiet = new int[Bins];

    /// <summary>Medians in dBFS; NaN when that half has seen no audio yet.</summary>
    public readonly record struct Stats(float SpeechDb, float FloorDb, int VoicedBuffers, int TotalBuffers);

    /// <summary>Called from the audio thread, once per capture buffer. Allocation- and lock-free.</summary>
    public void Add(float rms, bool voiced)
    {
        var bins = voiced ? _voiced : _quiet;
        Interlocked.Increment(ref bins[BinFor(rms)]);
    }

    public void Reset()
    {
        Array.Clear(_voiced);
        Array.Clear(_quiet);
    }

    /// <summary>
    /// Callable from any thread at any time — bins are only ever incremented, so the worst a torn read
    /// costs is one buffer of resolution.
    /// </summary>
    public Stats Snapshot()
    {
        int speech = MedianDb(_voiced, out int nv);
        int floor = MedianDb(_quiet, out int nq);
        return new Stats(nv > 0 ? speech : float.NaN, nq > 0 ? floor : float.NaN, nv, nv + nq);
    }

    public static int BinFor(float rms)
    {
        int db = rms > 0f ? (int)Math.Round(20.0 * Math.Log10(rms)) : MinDb;
        int bin = db - MinDb;
        return bin < 0 ? 0 : bin >= Bins ? Bins - 1 : bin;
    }

    /// <summary>
    /// Lower median: the bin at which the running count first exceeds half the total. Total is summed
    /// in a separate pass, so a concurrent Add between the passes can shift the answer by one bin —
    /// acceptable for a diagnostic, and the alternative is a lock on the audio thread.
    /// </summary>
    public static int MedianDb(int[] bins, out int total)
    {
        total = 0;
        for (int i = 0; i < bins.Length; i++) total += Volatile.Read(ref bins[i]);
        if (total == 0) return MinDb;

        int half = total / 2, run = 0;
        for (int i = 0; i < bins.Length; i++)
        {
            run += Volatile.Read(ref bins[i]);
            if (run > half) return i + MinDb;
        }
        return Bins - 1 + MinDb;
    }
}

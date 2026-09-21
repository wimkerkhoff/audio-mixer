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

    /// <summary>
    /// Voiced buffers needed before a median is published at all. One buffer is enough to compute a
    /// median and it means nothing — the level alert would fire on a single frame of noise before
    /// anyone has spoken. A few seconds of actual speech is the smallest honest sample.
    /// </summary>
    public const int MinVoicedBuffers = 300;   // ~3 s at the shared-mode buffer rate

    /// <summary>
    /// Voiced buffers in the rolling window used to notice the cumulative median has gone stale.
    /// Long enough to be a median rather than a mood, short enough to react within a sentence or two.
    /// </summary>
    public const int RecentWindow = 1000;      // ~10 s of voiced audio

    /// <summary>
    /// How far the recent median may drift from the cumulative one before the cumulative figure is
    /// describing a rig that no longer exists. Wider than normal talker variation, narrower than any
    /// gain change worth making.
    /// </summary>
    public const float StaleDriftDb = 6f;

    private readonly int[] _voiced = new int[Bins];
    private readonly int[] _quiet = new int[Bins];

    // Ring of recent voiced bins. Written only from the audio thread; read under a torn-read tolerance
    // exactly like the histograms, because a diagnostic is not worth a lock on the capture path.
    private readonly byte[] _recent = new byte[RecentWindow];
    private int _recentCount;
    private int _recentNext;

    /// <param name="SpeechDb">Cumulative voiced median, NaN until MinVoicedBuffers have landed.</param>
    /// <param name="RecentSpeechDb">Rolling-window median, NaN until the window has enough.</param>
    /// <param name="IsStale">
    /// The two medians disagree by more than StaleDriftDb, so the cumulative figure is measuring a gain
    /// setting that has since changed. The operator cannot be expected to remember to reset by hand —
    /// a stale number that looks authoritative is worse than no number.
    /// </param>
    public readonly record struct Stats(
        float SpeechDb, float FloorDb, int VoicedBuffers, int TotalBuffers,
        float RecentSpeechDb = float.NaN, bool IsStale = false);

    /// <summary>Called from the audio thread, once per capture buffer. Allocation- and lock-free.</summary>
    public void Add(float rms, bool voiced)
    {
        int bin = BinFor(rms);
        var bins = voiced ? _voiced : _quiet;
        Interlocked.Increment(ref bins[bin]);

        if (!voiced) return;
        _recent[_recentNext] = (byte)bin;
        _recentNext = (_recentNext + 1) % RecentWindow;
        if (_recentCount < RecentWindow) _recentCount++;
    }

    public void Reset()
    {
        Array.Clear(_voiced);
        Array.Clear(_quiet);
        Array.Clear(_recent);
        _recentCount = 0;
        _recentNext = 0;
    }

    /// <summary>
    /// Callable from any thread at any time — bins are only ever incremented, so the worst a torn read
    /// costs is one buffer of resolution.
    /// </summary>
    public Stats Snapshot()
    {
        int speech = MedianDb(_voiced, out int nv);
        int floor = MedianDb(_quiet, out int nq);

        float cumulative = nv >= MinVoicedBuffers ? speech : float.NaN;
        float recent = RecentMedianDb();

        // Only meaningful once BOTH have a real sample: before that a disagreement says nothing.
        bool stale = !float.IsNaN(cumulative) && !float.IsNaN(recent)
                     && Math.Abs(recent - cumulative) > StaleDriftDb;

        return new Stats(cumulative, nq > 0 ? floor : float.NaN, nv, nv + nq, recent, stale);
    }

    private float RecentMedianDb()
    {
        int count = Volatile.Read(ref _recentCount);
        if (count < MinVoicedBuffers) return float.NaN;

        Span<int> bins = stackalloc int[Bins];
        for (int i = 0; i < count; i++) bins[_recent[i]]++;

        int half = count / 2, run = 0;
        for (int i = 0; i < Bins; i++)
        {
            run += bins[i];
            if (run > half) return i + MinDb;
        }
        return Bins - 1 + MinDb;
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

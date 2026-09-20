namespace AudioMixer.Services;

/// <summary>What one microphone did over a whole session.</summary>
public sealed record InputSummary
{
    public int Index { get; init; }
    public string Label { get; init; } = "";
    public string? DeviceName { get; init; }

    /// <summary>Settled calibration medians. NaN when the mic never produced enough voiced audio.</summary>
    public float SpeechDb { get; init; } = float.NaN;
    public float FloorDb { get; init; } = float.NaN;

    /// <summary>Share of the session this mic was the automix leader on at least one bus.</summary>
    public double LeaderPercent { get; init; }

    /// <summary>Share of the session it was routed but held below unity by the automixer.</summary>
    public double DuckedPercent { get; init; }

    /// <summary>Share of the session it was routed and hard-muted to zero — Gate's normal state.</summary>
    public double MutedByGatePercent { get; init; }

    public long Underruns { get; init; }
    public long ClippedSamples { get; init; }
}

/// <summary>What one bus did.</summary>
public sealed record OutputSummary
{
    public int Index { get; init; }
    public string Label { get; init; } = "";

    /// <summary>Leader changes, the figure that reads as chatter when the rig is mis-levelled.</summary>
    public long Handoffs { get; init; }
    public double HandoffsPerMinute { get; init; }

    /// <summary>Winner index → share of session. Key -1 is "no winner at all".</summary>
    public IReadOnlyDictionary<int, double> OccupancyPercent { get; init; } =
        new Dictionary<int, double>();

    public double NoWinnerPercent { get; init; }
}

public sealed record SessionSummary
{
    public string Stamp { get; init; } = "";
    public DateTime StartedUtc { get; init; }
    public double DurationMinutes { get; init; }
    public string? Scene { get; init; }
    public IReadOnlyList<InputSummary> Inputs { get; init; } = Array.Empty<InputSummary>();
    public IReadOnlyList<OutputSummary> Outputs { get; init; } = Array.Empty<OutputSummary>();

    /// <summary>Plain-language notes, generated as thresholds are crossed. Ordered by time.</summary>
    public IReadOnlyList<SessionEvent> Events { get; init; } = Array.Empty<SessionEvent>();
}

public sealed record SessionEvent(string TimeOfDay, string Kind, AlertSeverity Severity, string Message);

/// <summary>
/// Accumulates what a session DID, as distinct from what the mixer is doing right now.
///
/// Every readout in the app is instantaneous; every finding that mattered on 2026-09-20 was an
/// aggregate. "The lapel is at −39 dBFS" is a number. "The lapel sat 1 dB above the duck threshold, so
/// the duck released twelve times a minute for fifteen minutes" is the finding. Those figures were
/// computed by hand from a log that is off by default — this class produces them continuously.
///
/// Pure: it is fed samples and hands back a summary, with no clock, files or devices of its own, so
/// the arithmetic is testable without staging a service. Persistence is <see cref="SessionStore"/>.
/// </summary>
public sealed class SessionAggregator
{
    private readonly int _inputs;
    private readonly int _outputs;

    private readonly int[] _lastWinner;
    private readonly long[] _handoffs;
    private readonly Dictionary<int, long>[] _occupancyMs;

    private readonly long[] _leaderMs;
    private readonly long[] _duckedMs;
    private readonly long[] _mutedMs;

    private readonly List<SessionEvent> _events = new();
    private readonly HashSet<string> _raised = new();

    private long _elapsedMs;

    /// <summary>Below this an automix gain counts as ducked; at zero it counts as hard-muted.</summary>
    public const float DuckedBelow = 0.85f;

    public SessionAggregator(int inputs, int outputs)
    {
        _inputs = inputs;
        _outputs = outputs;
        _lastWinner = Enumerable.Repeat(int.MinValue, outputs).ToArray();
        _handoffs = new long[outputs];
        _occupancyMs = Enumerable.Range(0, outputs).Select(_ => new Dictionary<int, long>()).ToArray();
        _leaderMs = new long[inputs];
        _duckedMs = new long[inputs];
        _mutedMs = new long[inputs];
    }

    public long ElapsedMs => _elapsedMs;

    /// <summary>
    /// One observation. <paramref name="winners"/> is the automix leader per output (-1 for none), and
    /// <paramref name="gain"/> gives the applied automix gain for (input, output).
    /// </summary>
    public void Tick(long elapsedMs, IReadOnlyList<int> winners, Func<int, int, float> gain)
    {
        if (elapsedMs <= 0) return;
        _elapsedMs += elapsedMs;

        for (int o = 0; o < _outputs && o < winners.Count; o++)
        {
            int w = winners[o];
            // int.MinValue is "no observation yet" — the first sample is not a hand-off.
            if (_lastWinner[o] != int.MinValue && w != _lastWinner[o]) _handoffs[o]++;
            _lastWinner[o] = w;

            _occupancyMs[o].TryGetValue(w, out long ms);
            _occupancyMs[o][w] = ms + elapsedMs;
        }

        for (int i = 0; i < _inputs; i++)
        {
            bool leader = false, ducked = false, muted = false;
            for (int o = 0; o < _outputs; o++)
            {
                if (o < winners.Count && winners[o] == i) leader = true;
                float g = gain(i, o);
                if (g <= 0f) muted = true;
                else if (g < DuckedBelow) ducked = true;
            }
            if (leader) _leaderMs[i] += elapsedMs;
            if (muted) _mutedMs[i] += elapsedMs;
            else if (ducked) _duckedMs[i] += elapsedMs;
        }
    }

    /// <summary>
    /// Records a note once per <paramref name="key"/>. Latched because these are conditions, not
    /// instants: a mic 20 dB low is 20 dB low for the whole meeting, and one line says that better
    /// than nine hundred.
    /// </summary>
    public void Note(string key, string timeOfDay, string kind, AlertSeverity severity, string message)
    {
        if (!_raised.Add(key)) return;
        _events.Add(new SessionEvent(timeOfDay, kind, severity, message));
    }

    public SessionSummary Build(
        string stamp, DateTime startedUtc, string? scene,
        IReadOnlyList<InputSummary> inputSeed, IReadOnlyList<OutputSummary> outputSeed)
    {
        double minutes = _elapsedMs / 60000.0;
        double total = Math.Max(1, _elapsedMs);

        var inputs = inputSeed.Select((seed, i) => seed with
        {
            LeaderPercent = i < _inputs ? _leaderMs[i] * 100.0 / total : 0,
            DuckedPercent = i < _inputs ? _duckedMs[i] * 100.0 / total : 0,
            MutedByGatePercent = i < _inputs ? _mutedMs[i] * 100.0 / total : 0,
        }).ToList();

        var outputs = outputSeed.Select((seed, o) =>
        {
            if (o >= _outputs) return seed;
            var occ = _occupancyMs[o].ToDictionary(kv => kv.Key, kv => kv.Value * 100.0 / total);
            occ.TryGetValue(-1, out double none);
            return seed with
            {
                Handoffs = _handoffs[o],
                HandoffsPerMinute = minutes > 0 ? _handoffs[o] / minutes : 0,
                OccupancyPercent = occ,
                NoWinnerPercent = none,
            };
        }).ToList();

        return new SessionSummary
        {
            Stamp = stamp,
            StartedUtc = startedUtc,
            DurationMinutes = minutes,
            Scene = scene,
            Inputs = inputs,
            Outputs = outputs,
            Events = _events.ToList(),
        };
    }
}

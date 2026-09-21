using AudioMixer.Audio;
using AudioMixer.ViewModels;

namespace AudioMixer.Services;

/// <summary>
/// Drives <see cref="SessionAggregator"/> from the live engine and writes the record when the session
/// ends.
///
/// Runs on the meter tick, like <see cref="DiagnosticsLog"/>, because everything it needs is already
/// latched there — no new timer and nothing touched on an audio thread. Unlike DiagnosticsLog it does
/// NOT short-circuit when file logging is off: the whole reason a session record exists is the
/// service nobody prepared for.
/// </summary>
public sealed class SessionRecorder : IDisposable
{
    private readonly AudioEngine _engine;
    private readonly IReadOnlyList<ChannelViewModel> _channels;
    private readonly IReadOnlyList<OutputViewModel> _outputs;
    private readonly SessionStore _store;
    private readonly SessionAggregator _aggregator;
    private readonly DateTime _startedLocal = DateTime.Now;
    private readonly int[] _winners;

    private long _lastTick = Environment.TickCount64;
    private long _lastWrite = Environment.TickCount64;

    /// <summary>
    /// Written periodically, not only on exit. A record that only lands on a clean shutdown is lost to
    /// exactly the crash or force-kill it would have explained — which is how this morning's session
    /// was nearly lost. The file is keyed on the start stamp, so each checkpoint simply overwrites.
    /// </summary>
    public const double CheckpointMinutes = 2.0;

    /// <summary>The scene in force, set by the view model; recorded so a session can be read in context.</summary>
    public string? Scene { get; set; }

    /// <summary>
    /// How the rig is configured, sampled when the record is written. Supplied as a callback rather
    /// than a value because it must reflect the END of the session: a routing change halfway through
    /// is exactly the thing that explains an odd reading.
    /// </summary>
    public Func<SessionConfig>? Config { get; set; }

    /// <summary>Below this the session is a launch-and-close, not a service, and not worth a file.</summary>
    public const double MinimumMinutes = 1.0;

    public SessionRecorder(
        AudioEngine engine,
        IReadOnlyList<ChannelViewModel> channels,
        IReadOnlyList<OutputViewModel> outputs,
        SessionStore? store = null)
    {
        _engine = engine;
        _channels = channels;
        _outputs = outputs;
        _store = store ?? new SessionStore();
        _aggregator = new SessionAggregator(channels.Count, outputs.Count);
        _winners = new int[outputs.Count];
    }

    public string Stamp => SessionStore.StampFor(_startedLocal);

    public void Tick()
    {
        long now = Environment.TickCount64;
        long elapsed = now - _lastTick;
        _lastTick = now;
        // A long gap means the machine slept or the UI thread stalled; attributing minutes of
        // occupancy to whichever mic happened to be leading then would be a lie.
        if (elapsed <= 0 || elapsed > 5000) return;

        for (int o = 0; o < _winners.Length; o++) _winners[o] = _engine.AutoMixActiveInput(o);
        _aggregator.Tick(elapsed, _winners, GainFor);

        if (now - _lastWrite >= CheckpointMinutes * 60_000)
        {
            _lastWrite = now;
            Write();
        }
    }

    private float GainFor(int input, int output) =>
        input >= 0 && input < _engine.Inputs.Length ? _engine.Inputs[input].GetAutoMixGain(output) : 1f;

    /// <summary>
    /// Folds a live health alert into the record, once per alert id. Called with whatever the banner
    /// currently holds, so the saved record shows what the operator was being told at the time —
    /// including warnings they closed and carried on past.
    /// </summary>
    public void Note(IEnumerable<HealthAlert> alerts)
    {
        foreach (var a in alerts)
        {
            _aggregator.Note(a.Id, DateTime.Now.ToString("HH:mm"), Kind(a.Id), a.Severity, a.Message);
        }
    }

    private static string Kind(string id)
    {
        int dot = id.LastIndexOf('.');
        return dot >= 0 && dot < id.Length - 1 ? id[(dot + 1)..] : id;
    }

    public SessionSummary BuildSummary()
    {
        var inputs = new List<InputSummary>(_channels.Count);
        for (int i = 0; i < _channels.Count; i++)
        {
            var vm = _channels[i];
            var ch = _engine.Inputs[i];
            var cal = ch.SnapshotCalibration();
            inputs.Add(new InputSummary
            {
                Index = i,
                Label = string.IsNullOrWhiteSpace(vm.CustomLabel) ? $"Input {i + 1}" : vm.CustomLabel,
                DeviceName = vm.SelectedDevice?.FriendlyName,
                SpeechDb = cal.SpeechDb,
                FloorDb = cal.FloorDb,
                Underruns = Enumerable.Range(0, _outputs.Count).Sum(o => ch.UnderrunsForOutput(o)),
                ClippedSamples = ch.ClippedSamples,
            });
        }

        var outputs = Enumerable.Range(0, _outputs.Count).Select(o => new OutputSummary
        {
            Index = o,
            Label = string.IsNullOrWhiteSpace(_outputs[o].CustomLabel)
                ? OutputViewModel.Tag(o) : _outputs[o].CustomLabel,
        }).ToList();

        return _aggregator.Build(Stamp, _startedLocal.ToUniversalTime(), Scene, inputs, outputs, Config?.Invoke());
    }

    /// <summary>
    /// Writes the record for this session, overwriting any earlier checkpoint. Returns null when the
    /// session is too short to be a service.
    /// </summary>
    public string? Write()
    {
        if (_aggregator.ElapsedMs < MinimumMinutes * 60_000) return null;
        return _store.Save(BuildSummary());
    }

    public void Dispose()
    {
        var path = Write();
        if (path != null) AudioLog.Write($"Session record written: {path}");
    }
}

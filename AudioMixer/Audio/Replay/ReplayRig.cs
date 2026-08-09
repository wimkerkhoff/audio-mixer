using System.IO;
using System.Text.RegularExpressions;
using System.Threading;

namespace AudioMixer.Audio.Replay;

/// <summary>
/// Plays back a whole recorded session — the N sample-aligned <c>diag-input*.wav</c> files the
/// "record all inputs" tap produces — as if the mics were live. One clock pumps every source in
/// lockstep so the automixer sees the same relative timing it saw during the session; per-file timers
/// would drift and quietly change which mic wins.
///
/// The diag tap writes pre-gain, pre-delay, post-conversion samples, so replay re-runs gain, mute,
/// delay, flux-CV, RF tallies and the whole automix decision over exactly the samples the live
/// selector saw.
/// </summary>
public sealed partial class ReplayRig : IDisposable
{
    // WASAPI shared mode delivers <512-frame buffers, which is the entire reason InputChannel
    // accumulates flux windows across buffers. Replaying at 512+ would silently bypass that path and
    // exercise different code than production, so emit 480 like the real devices do.
    public const int ChunkFrames = 480;
    private const int TickMs = 10;

    [GeneratedRegex(@"diag-input(\d+)-(\d{8}-\d{6})\.wav$", RegexOptions.IgnoreCase)]
    private static partial Regex FileRx();

    private readonly Timer _timer;
    private readonly object _lock = new();
    private long _lastTicks;
    private double _pendingFrames;
    private bool _running;

    /// <summary>Sources indexed by input channel (diag-input<c>N</c> maps to channel <c>N-1</c>).</summary>
    public ReplaySource?[] Sources { get; }

    public string Stamp { get; }
    public string Directory { get; }
    public int SampleRate { get; }
    public double Speed { get; set; } = 1.0;
    public bool Loop { get; set; }
    public bool Paused { get; set; }

    public event Action? ReachedEnd;

    /// <summary>
    /// Raised after every source has been advanced by one <see cref="ChunkFrames"/> chunk, i.e. once
    /// per 10 ms of replayed audio. The engine drives the automix tick from this instead of its own
    /// wall-clock timer while replaying, which buys two things:
    ///   * determinism — the selector sees exactly one tick per chunk, so a fixture replays the same
    ///     way every run and a golden baseline can be compared meaningfully;
    ///   * speed independence — at Speed=4 the automixer still gets one tick per 10 ms of *audio*,
    ///     so a fast batch run behaves like the real-time one instead of quartering the hold time.
    /// Chunk pumping is synchronous, so levels are already latched when this fires.
    /// </summary>
    public event Action? Pumped;

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Documents", "AudioMixer", "analysis");

    private ReplayRig(string dir, string stamp, ReplaySource?[] sources)
    {
        Directory = dir;
        Stamp = stamp;
        Sources = sources;
        SampleRate = sources.FirstOrDefault(s => s != null)?.WaveFormat.SampleRate ?? 48_000;
        _timer = new Timer(Tick, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Lists the session stamps available in <paramref name="dir"/>, newest first.</summary>
    public static IReadOnlyList<string> ListSessions(string? dir = null)
    {
        dir ??= DefaultDirectory;
        if (!System.IO.Directory.Exists(dir)) return [];
        return System.IO.Directory.GetFiles(dir, "diag-input*.wav")
            .Select(p => FileRx().Match(Path.GetFileName(p)))
            .Where(m => m.Success)
            .Select(m => m.Groups[2].Value)
            .Distinct()
            .OrderDescending()
            .ToList();
    }

    /// <summary>
    /// Opens a session. <paramref name="stamp"/> may be null (latest), a full stamp, or any unique
    /// substring of one (so "--replay=20260809" works).
    /// </summary>
    public static ReplayRig Open(string? dir = null, string? stamp = null)
    {
        dir ??= DefaultDirectory;
        var sessions = ListSessions(dir);
        if (sessions.Count == 0) throw new FileNotFoundException($"no diag-input*.wav in {dir}");

        string resolved = stamp == null
            ? sessions[0]
            : sessions.FirstOrDefault(s => s == stamp)
              ?? sessions.FirstOrDefault(s => s.Contains(stamp, StringComparison.OrdinalIgnoreCase))
              ?? throw new FileNotFoundException($"no session matching '{stamp}' in {dir}");

        var matches = System.IO.Directory.GetFiles(dir, $"diag-input*-{resolved}.wav")
            .Select(p => (path: p, m: FileRx().Match(Path.GetFileName(p))))
            .Where(x => x.m.Success)
            .Select(x => (x.path, index: int.Parse(x.m.Groups[1].Value) - 1))
            .OrderBy(x => x.index)
            .ToList();

        int count = matches.Count == 0 ? 0 : matches.Max(x => x.index) + 1;
        var sources = new ReplaySource?[count];
        foreach (var (path, index) in matches)
        {
            if (index < 0 || index >= count) continue;
            sources[index] = new ReplaySource(path);
        }
        return new ReplayRig(dir, resolved, sources);
    }

    public int InputCount => Sources.Length;

    public TimeSpan Duration
    {
        get
        {
            long frames = 0;
            foreach (var s in Sources) if (s != null) frames = Math.Max(frames, s.TotalFrames);
            return TimeSpan.FromSeconds((double)frames / SampleRate);
        }
    }

    public TimeSpan Position
    {
        get
        {
            foreach (var s in Sources)
                if (s != null) return TimeSpan.FromSeconds((double)s.PositionFrames / SampleRate);
            return TimeSpan.Zero;
        }
    }

    public void Seek(TimeSpan position)
    {
        lock (_lock)
        {
            long frame = (long)(position.TotalSeconds * SampleRate);
            foreach (var s in Sources) s?.Seek(frame);
            _pendingFrames = 0;
        }
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_running) return;
            _running = true;
            _lastTicks = Environment.TickCount64;
            _pendingFrames = 0;
            foreach (var s in Sources) s?.StartRecording();
            _timer.Change(0, TickMs);
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (!_running) return;
            _running = false;
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            foreach (var s in Sources) s?.StopRecording();
        }
    }

    private void Tick(object? state)
    {
        if (!Monitor.TryEnter(_lock)) return;   // a slow tick must not stack up and burst
        try
        {
            if (!_running) return;
            long now = Environment.TickCount64;
            long elapsed = now - _lastTicks;
            _lastTicks = now;
            if (Paused) return;

            // Accumulate fractional frames so Speed != 1 stays accurate and drift can't build up.
            _pendingFrames += elapsed * (SampleRate / 1000.0) * Math.Max(0.01, Speed);

            // A long stall (debugger break, laptop sleep) would otherwise dump minutes of audio in one
            // burst and flood the output buffers; cap the catch-up at a quarter second.
            double cap = SampleRate * 0.25;
            if (_pendingFrames > cap) _pendingFrames = cap;

            bool ended = false;
            while (_pendingFrames >= ChunkFrames)
            {
                _pendingFrames -= ChunkFrames;
                foreach (var s in Sources)
                {
                    if (s == null) continue;
                    if (!s.Pump(ChunkFrames)) ended = true;
                }
                Pumped?.Invoke();
            }

            if (!ended) return;
            if (Loop) { foreach (var s in Sources) s?.Seek(0); }
            else { _running = false; _timer.Change(Timeout.Infinite, Timeout.Infinite); }
            ReachedEnd?.Invoke();
        }
        finally
        {
            Monitor.Exit(_lock);
        }
    }

    public void Dispose()
    {
        Stop();
        _timer.Dispose();
        foreach (var s in Sources) s?.Dispose();
    }
}

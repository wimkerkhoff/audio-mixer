namespace AudioMixer.Audio;

// Decides per-channel, per-output gains so that only the mic(s) closest to the active talker
// pass at full level — the standard fix for multiple distant mics summing the same voice
// (comb-filter "echo", raised noise floor, room reverb). Runs on a periodic engine timer,
// off the audio threads: it reads each channel's volatile level and writes each channel's
// volatile per-output gain. Lock-free; benign one-tick-stale races are acceptable.
public sealed class AutoMixer
{
    private const double TickSeconds = 0.010;     // engine calls Tick() at ~100 Hz
    private const float AttackMs = 8f;
    private const float ReleaseMs = 250f;         // doubles as the hold so word gaps don't drop the duck
    private const float SilenceFloorRms = 0.0018f; // ~ -55 dBFS; below this, don't duck a quiet room
    private const float PriorityActiveRms = 0.01f;  // ~ -40 dBFS; a priority mic above this is "speaking"

    // A presenter pauses between sentences. The envelope release (250 ms) needs ~575 ms to fall from
    // speech to PriorityActiveRms, which an ordinary sentence gap exceeds — so without a hangover the
    // duck releases mid-talk and Gate hands the bus to whatever room mic is loudest, for ~250 ms at a
    // time. Measured live 2026-08-30: 13 such hand-offs in 40 s, every one with the lapel envelope
    // just under the threshold, to a speakerphone sitting on the presenter's own table (-28 dBFS).
    // The leader selection is already protected this way (HandoffHoldTicks); this is the same guard
    // for the duck. Sized from that capture: every observed gap was <= 0.89 s.
    private const int PriorityHoldTicks = 120;      // ~1.2 s the duck is held after the lapel goes quiet

    // The hangover must not swallow a genuine interjection: at strength 100% the duck is a hard mute,
    // so holding it blindly would silence an audience question asked in the presenter's pause. A room
    // mic this loud during the hangover is a real talker, not the presenter's residual, so break the
    // hold immediately. Sits above SilenceFloorRms and below a talker at normal room distance.
    // NOTE: this can only separate the two when no room mic sits next to the presenter — one on his
    // own table reads far louder than a real interjection across the room (measured: -28 vs -43 dBFS).
    private const float PriorityBreakInRms = 0.0032f;  // ~ -50 dBFS

    // ...and it must also be louder than the lapel itself, i.e. hear someone the lapel does not. On
    // 2026-09-26 a room mic heard the presenter at -35 while his lapel read -30; in his pauses both
    // envelopes decay together, the room mic's residual stayed over -50 as the lapel crossed -40, and
    // the absolute break-in alone handed the bus lapel <-> room mic ~48 times a minute. A room talker
    // reads 10-20 dB over the lapel's faint pickup of them; one close enough for the lapel to hear
    // nearly as well is carried by the lapel anyway, since the priority mic is always at unity.
    private const float BreakInOverLapel = 2.0f;      // +6 dB

    // How long a challenger must keep its margin before it takes the bus, by how far ahead it is.
    // Within a few dB the talker is between two mics and either serves; switching is the only harm
    // (median tenure 0.4 s, top two 3.2 dB apart, 2026-09-26). A clear winner still takes over fast.
    // The wait also rejects a one-tick spike, whose envelope falls back through the bands in time.
    private const int SustainTicksClose = 50;      // +3..+6 dB: ~500 ms
    private const int SustainTicksMid = 25;        // +6..+10 dB: ~250 ms
    private const int SustainTicksClear = 10;      // over +10 dB: ~100 ms

    private static int RequiredSustainTicks(float ratio) =>
        ratio >= 3.162f ? SustainTicksClear : ratio >= 2.0f ? SustainTicksMid : SustainTicksClose;

    // Stable hand-off: the selected mic is held with hysteresis so a brief louder moment on another
    // mic can't steal it. This is what fixes the speakerphones — their AGC applies make-up gain in a
    // talker's pauses, momentarily out-leveling the close mic; without a hold the selection chatters
    // to whatever distant mic pumped up. Measured on real hardware: hold+hysteresis cuts selection
    // flips ~5x and tracks the closest mic (see CLAUDE.md finding 1).
    private const int HandoffHoldTicks = 20;       // ~200 ms a winner is held before it can switch
    private const float HandoffHysteresis = 1.413f; // challenger must be ~+3 dB louder to take over

    private readonly int _outputCount;
    private readonly int[] _modes;                 // AutoMixMode as int (enum can't use Volatile<T>)
    private readonly float[] _env;                 // smoothed level per channel (sized to max inputs)
    private readonly bool[] _activeAny;            // scratch: channel selected on any output this tick
    private readonly int[] _activeInput;           // per output, selected channel index (-1 = none)
    private readonly int[] _winner;                // per output held leader, -1 = none
    private readonly int[] _winnerHold;            // per output hold countdown
    private readonly int[] _priorityHold;          // per output, ticks the priority duck stays latched
    private readonly int[] _priorityArg;           // per output, priority channel that latched the duck
    private readonly int[] _challenger;            // per output, mic currently past the margin (-1 = none)
    private readonly int[] _challengeTicks;        // per output, consecutive ticks it has stayed there

    private readonly float[] _cv;                  // per channel spectral-flux instability (from InputChannel)

    private readonly float _attackCoef;
    private readonly float _releaseCoef;

    public AutoMixer(int outputCount, int maxChannels)
    {
        _outputCount = outputCount;
        _modes = new int[outputCount];
        _activeInput = new int[outputCount];
        _winner = new int[outputCount];
        _winnerHold = new int[outputCount];
        _priorityHold = new int[outputCount];
        _priorityArg = new int[outputCount];
        _challenger = new int[outputCount];
        _challengeTicks = new int[outputCount];
        for (int o = 0; o < outputCount; o++)
        {
            _activeInput[o] = -1;
            _winner[o] = -1;
            _priorityArg[o] = -1;
            _challenger[o] = -1;
        }

        _env = new float[maxChannels];
        _cv = new float[maxChannels];
        _activeAny = new bool[maxChannels];

        _attackCoef = (float)(1 - Math.Exp(-TickSeconds / (AttackMs / 1000.0)));
        _releaseCoef = (float)(1 - Math.Exp(-TickSeconds / (ReleaseMs / 1000.0)));
    }

    public void SetMode(int output, AutoMixMode mode)
    {
        if (output >= 0 && output < _outputCount) Volatile.Write(ref _modes[output], (int)mode);
    }

    // The channel the automixer is currently selecting on the given output (-1 = none/idle).
    public int ActiveInput(int output) =>
        output >= 0 && output < _outputCount ? Volatile.Read(ref _activeInput[output]) : -1;

    // Read-only snapshot of the selector's internal decision state for the diagnostic state server.
    // Reads the same volatile/array fields Tick() writes; one-tick-stale races are benign (consistent
    // with the rest of this lock-free design). Allocates per call — fine, requests are infrequent.
    public AutoMixDiag Snapshot(int channelCount)
    {
        int n = Math.Min(channelCount, _env.Length);
        var d = new AutoMixDiag
        {
            Env = new float[n],
            Cv = new float[n],
            Mode = new AutoMixMode[_outputCount],
            Winner = new int[_outputCount],
            WinnerHold = new int[_outputCount],
            ActiveInput = new int[_outputCount],
        };
        for (int i = 0; i < n; i++) { d.Env[i] = _env[i]; d.Cv[i] = _cv[i]; }
        for (int o = 0; o < _outputCount; o++)
        {
            d.Mode[o] = (AutoMixMode)Volatile.Read(ref _modes[o]);
            d.Winner[o] = _winner[o];
            d.WinnerHold[o] = _winnerHold[o];
            d.ActiveInput[o] = Volatile.Read(ref _activeInput[o]);
        }
        return d;
    }

    public void Tick(InputChannel[] inputs)
    {
        int n = Math.Min(inputs.Length, _env.Length);

        // Level envelope per channel.
        for (int i = 0; i < n; i++)
        {
            float inst = inputs[i].CurrentLevelLinear;
            float e = _env[i];
            e += (inst - e) * (inst > e ? _attackCoef : _releaseCoef);
            _env[i] = e;
            _cv[i] = inputs[i].CurrentFluxCv;

            _activeAny[i] = false;
        }

        for (int o = 0; o < _outputCount; o++)
        {
            var mode = (AutoMixMode)Volatile.Read(ref _modes[o]);
            if (mode == AutoMixMode.Off)
            {
                for (int i = 0; i < n; i++) inputs[i].SetAutoMixGain(o, 1f);
                _winner[o] = -1;
                _challenger[o] = -1;
                _activeInput[o] = -1;
                continue;
            }

            // Priority mics (e.g. a presenter's lapel) are always full level and never compete.
            // While a priority mic is active it ducks the room mics, so the same voice can't reach
            // the bus through both the clean lapel and a delayed room mic (which would comb-filter).
            bool priorityActive = false;
            bool anyPriority = false;
            float pmax = 0f;
            int pArg = -1;
            float lmax = 0f;
            int argmax = -1;
            for (int i = 0; i < n; i++)
            {
                if (!inputs[i].GetRoute(o)) continue;
                if (inputs[i].IsPriority)
                {
                    anyPriority = true;
                    inputs[i].SetAutoMixGain(o, 1f);
                    if (_env[i] > PriorityActiveRms)
                    {
                        priorityActive = true;
                        if (_env[i] > pmax) { pmax = _env[i]; pArg = i; }
                    }
                    continue;
                }
                if (_env[i] > lmax) { lmax = _env[i]; argmax = i; }
            }

            // Loudest wins. On a homogeneous DSP-free rig a level difference IS distance, so level is
            // the proximity cue (finding 6a) — the correlation and flux-CV selectors that used to sit
            // here existed for the Ankers' AGC and were measured as harmful once it was gone.
            int challenger = argmax;

            // Hold the duck across the presenter's sentence gaps, so a pause can't hand the bus to a
            // room mic for a quarter second at a time (see PriorityHoldTicks).
            if (priorityActive)
            {
                _priorityHold[o] = PriorityHoldTicks;
                _priorityArg[o] = pArg;
            }
            else if (anyPriority && _priorityHold[o] > 0)
            {
                int held = _priorityArg[o];
                bool heldStale = held < 0 || held >= n || !inputs[held].GetRoute(o) || !inputs[held].IsPriority;
                if (heldStale || (lmax > PriorityBreakInRms && lmax > _env[held] * BreakInOverLapel))
                {
                    _priorityHold[o] = 0;
                    _priorityArg[o] = -1;
                }
                else
                {
                    _priorityHold[o]--;
                    priorityActive = true;
                    pArg = held;
                }
            }
            else
            {
                _priorityHold[o] = 0;
                _priorityArg[o] = -1;
            }

            if (priorityActive)
            {
                // A hard mute: this is what strength 100% did, which is the only setting this rig ran.
                const float pduck = 0f;
                for (int i = 0; i < n; i++)
                    if (inputs[i].GetRoute(o) && !inputs[i].IsPriority) inputs[i].SetAutoMixGain(o, pduck);
                _winner[o] = -1;
                _challenger[o] = -1;
                _activeInput[o] = pArg;
                if (pArg >= 0) _activeAny[pArg] = true;
                continue;
            }

            // Silent room or nothing competing: open everything, no ducking.
            if (argmax < 0 || _env[argmax] < SilenceFloorRms)
            {
                for (int i = 0; i < n; i++)
                    if (inputs[i].GetRoute(o)) inputs[i].SetAutoMixGain(o, 1f);
                _winner[o] = -1;
                _challenger[o] = -1;
                _activeInput[o] = -1;
                continue;
            }

            // The held leader, always. Hysteresis plus a hold is the actual fix for "far mic wins"
            // (finding 1) and finding 6a says it stays exactly as necessary on a DSP-free rig, so
            // there is no setting that turns it off — one that did could only re-create the bug.
            int w = _winner[o];
            if (_winnerHold[o] > 0) _winnerHold[o]--;
            bool wStale = w < 0 || w >= n || !inputs[w].GetRoute(o) || inputs[w].IsPriority;
            if (wStale)
            {
                // Nobody holds the bus, so there is nothing to protect: take it at once.
                w = challenger;
                _winnerHold[o] = HandoffHoldTicks;
                _challenger[o] = -1;
            }
            else if (challenger != w && _winnerHold[o] <= 0
                     && _env[challenger] > _env[w] * HandoffHysteresis)
            {
                if (_challenger[o] != challenger) { _challenger[o] = challenger; _challengeTicks[o] = 0; }
                float ratio = _env[w] > 0f ? _env[challenger] / _env[w] : float.MaxValue;
                if (++_challengeTicks[o] >= RequiredSustainTicks(ratio))
                {
                    w = challenger;
                    _winnerHold[o] = HandoffHoldTicks;
                    _challenger[o] = -1;
                }
            }
            else
            {
                _challenger[o] = -1;
            }
            _winner[o] = w;
            int leader = w;

            const float others = 0f;
            for (int i = 0; i < n; i++)
            {
                if (!inputs[i].GetRoute(o) || inputs[i].IsPriority) continue;
                inputs[i].SetAutoMixGain(o, i == leader ? 1f : others);
            }
            _activeInput[o] = leader;
            _activeAny[leader] = true;
        }

        for (int i = 0; i < n; i++) inputs[i].IsAutoMixActive = _activeAny[i];
    }
}

// Per-call snapshot of AutoMixer state for diagnostics. Per-channel arrays sized to the live channel
// count; per-output arrays sized to the output count.
public sealed class AutoMixDiag
{
    public float[] Env = Array.Empty<float>();        // smoothed level per channel (the selection metric)
    public float[] Cv = Array.Empty<float>();         // spectral-flux instability (diagnostic: rises on RF dropouts)
    public AutoMixMode[] Mode = Array.Empty<AutoMixMode>();
    public int[] Winner = Array.Empty<int>();         // held leader per output (-1 none)
    public int[] WinnerHold = Array.Empty<int>();     // ticks remaining before the leader can change
    public int[] ActiveInput = Array.Empty<int>();    // currently selected channel per output (-1 none)
}

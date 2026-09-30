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

    // Once the presenter has spoken, the lapel keeps the floor through every pause, however long,
    // until a room mic breaks in. It used to be held only ~1.2 s, after which the loudest room mic
    // took the bus at once; on 2026-09-27 that put 94 room-mic moments on the stream in a 45-minute
    // study, and the operator's labels found none was someone speaking: 66 paper/rustle, 4 coughs,
    // 23 the presenter himself or unclear, 1 an "amen" (CLAUDE.md, finding 9).
    //
    // A break-in is what a real talker does and room noise mostly does not: louder than
    // PriorityBreakInRms and than the lapel, for BreakInSustainTicks without a gap. Offline on the
    // labelled clips, -45 dBFS held 0.4 s let through 5 of 93 noises and kept 97% of the pre-service
    // talk; the cost is the first ~0.4 s of a genuine question. A room mic on the presenter's own
    // table still out-levels a real interjection (measured: -28 vs -43 dBFS) — placement, not this.
    private const float PriorityBreakInRms = 0.0056f;  // ~ -45 dBFS
    private const int BreakInSustainTicks = 40;        // ~400 ms

    // ...and it must also be louder than the lapel itself, i.e. hear someone the lapel does not. On
    // 2026-09-26 a room mic heard the presenter at -35 while his lapel read -30; in his pauses both
    // envelopes decay together, the room mic's residual stayed over -50 as the lapel crossed -40, and
    // the absolute break-in alone handed the bus lapel <-> room mic ~48 times a minute. A room talker
    // reads 10-20 dB over the lapel's faint pickup of them; one close enough for the lapel to hear
    // nearly as well is carried by the lapel anyway, since the priority mic is always at unity.
    private const float BreakInOverLapel = 2.0f;      // +6 dB

    // Q&A: once a room talker holds the bus, the lapel takes it back only by out-levelling them for
    // LapelReclaimTicks. It used to take it back the moment it crossed PriorityActiveRms, and a
    // presenter's "mm-hm", or his lapel hearing the questioner, did that: on 2026-09-27 the stream
    // went lapel <-> room mic ~40 times a minute through the closing Q&A. In the 2026-09-26 theology
    // study 23 lapel reclaims lasted under 1 s, the lapel a median 9 dB UNDER the talker in the
    // shortest; louder-for-0.3 s would have kept 21 of them off and let 22 of 24 real answers
    // through, ~0.3 s later. The lapel is at unity throughout, so the "mm-hm" itself still airs.
    private const int LapelReclaimTicks = 30;          // ~300 ms

    // Lapel mode switches itself to Q&A (Gate) when a room mic carries sustained speech — the
    // break-in test held this long. At 1.5 s a replay of the 2026-09-27 confession study switched
    // 33 s in on paper shuffled at a room mic (-23 dBFS); at 2.0 s a 100 Hz re-run of the detector on
    // its audio fired never in 45 minutes, and 13 times in 9.5 min of the 2026-09-26 theology study.
    // The first question of a discussion loses about this much of its start.
    private const int RoomSpeechTicks = 200;           // ~2 s

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
    private readonly bool[] _priorityOwned;         // per output, the lapel holds the floor in its pauses
    private readonly int[] _breakInTicks;          // per output, consecutive ticks a room mic has broken in
    private readonly int[] _priorityArg;           // per output, priority channel that holds the floor
    private readonly int[] _reclaimTicks;          // per output, consecutive ticks the lapel out-levels a room leader
    private readonly int[] _roomTalkTicks;         // per output, Lapel mode: consecutive ticks of room speech
    private readonly int[] _roomSpeech;            // per output, 1 = Lapel mode heard room speech (UI consumes)
    private readonly bool[] _lapelYielded;         // per output, Lapel mode heard the room; until the lapel speaks
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
        _priorityOwned = new bool[outputCount];
        _breakInTicks = new int[outputCount];
        _reclaimTicks = new int[outputCount];
        _roomTalkTicks = new int[outputCount];
        _roomSpeech = new int[outputCount];
        _lapelYielded = new bool[outputCount];
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

    /// <summary>
    /// True once after Lapel mode heard sustained speech on a room mic on this output — the UI's cue
    /// to switch to Q&A. Reading clears it.
    /// </summary>
    public bool TakeRoomSpeech(int output) =>
        output >= 0 && output < _outputCount && Interlocked.Exchange(ref _roomSpeech[output], 0) != 0;

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
            int lapel = -1;                        // a routed, unmuted priority mic
            float lmax = 0f;
            int argmax = -1;
            for (int i = 0; i < n; i++)
            {
                if (!inputs[i].GetRoute(o)) continue;
                if (inputs[i].IsPriority)
                {
                    anyPriority = true;
                    inputs[i].SetAutoMixGain(o, 1f);
                    if (lapel < 0 && !inputs[i].Muted) lapel = i;
                    if (_env[i] > PriorityActiveRms)
                    {
                        priorityActive = true;
                        if (_env[i] > pmax) { pmax = _env[i]; pArg = i; }
                    }
                    continue;
                }
                if (_env[i] > lmax) { lmax = _env[i]; argmax = i; }
            }

            // Lapel: the lapel alone, every room mic off, however loud. Without a live lapel to carry
            // the bus it would put nothing on air, so it runs as Q&A until one is routed and unmuted.
            if (mode == AutoMixMode.Lapel && lapel >= 0)
            {
                for (int i = 0; i < n; i++)
                    if (inputs[i].GetRoute(o) && !inputs[i].IsPriority) inputs[i].SetAutoMixGain(o, 0f);
                bool talk = lmax > PriorityBreakInRms && lmax > _env[lapel] * BreakInOverLapel;
                _roomTalkTicks[o] = talk ? _roomTalkTicks[o] + 1 : 0;
                bool heard = _roomTalkTicks[o] >= RoomSpeechTicks;
                if (heard)
                {
                    Volatile.Write(ref _roomSpeech[o], 1);
                    _roomTalkTicks[o] = 0;
                    _lapelYielded[o] = true;
                }
                else if (_env[lapel] > PriorityActiveRms)
                {
                    _lapelYielded[o] = false;
                }
                // Hand Q&A a room that the talker already holds, rather than making them break in
                // again for another 0.4 s once the UI flips the mode (up to a meter tick later).
                _priorityOwned[o] = !_lapelYielded[o];
                _priorityArg[o] = _lapelYielded[o] ? -1 : lapel;
                _breakInTicks[o] = 0;
                _reclaimTicks[o] = 0;
                _winner[o] = -1;
                _challenger[o] = -1;
                _activeInput[o] = lapel;
                _activeAny[lapel] = true;
                continue;
            }
            _roomTalkTicks[o] = 0;

            // Loudest wins. On a homogeneous DSP-free rig a level difference IS distance, so level is
            // the proximity cue (finding 6a) — the correlation and flux-CV selectors that used to sit
            // here existed for the Ankers' AGC and were measured as harmful once it was gone.
            int challenger = argmax;

            // A room talker who holds the bus keeps it until the lapel out-levels them (see
            // LapelReclaimTicks). Meanwhile the lapel stays at unity, so its wearer is still heard.
            int roomLeader = _winner[o];
            bool roomHolds = !_priorityOwned[o] && roomLeader >= 0 && roomLeader < n
                             && inputs[roomLeader].GetRoute(o) && !inputs[roomLeader].IsPriority
                             && _env[roomLeader] > SilenceFloorRms;
            if (priorityActive && roomHolds)
            {
                _reclaimTicks[o] = _env[pArg] > _env[roomLeader] ? _reclaimTicks[o] + 1 : 0;
                if (_reclaimTicks[o] < LapelReclaimTicks) priorityActive = false;
            }
            else
            {
                _reclaimTicks[o] = 0;
            }

            // The lapel keeps the floor through its wearer's pauses until a room mic breaks in (see
            // PriorityBreakInRms). A muted lapel gives it up: muting is how an operator leaves the
            // lapel out of a meeting, and the room must then run as plain Gate.
            if (priorityActive)
            {
                _priorityOwned[o] = true;
                _priorityArg[o] = pArg;
                _breakInTicks[o] = 0;
            }
            else if (anyPriority && _priorityOwned[o])
            {
                int held = _priorityArg[o];
                bool heldStale = held < 0 || held >= n || !inputs[held].GetRoute(o)
                                 || !inputs[held].IsPriority || inputs[held].Muted;
                bool breaking = !heldStale
                                && lmax > PriorityBreakInRms && lmax > _env[held] * BreakInOverLapel;
                _breakInTicks[o] = breaking ? _breakInTicks[o] + 1 : 0;
                if (heldStale || _breakInTicks[o] >= BreakInSustainTicks)
                {
                    _priorityOwned[o] = false;
                    _priorityArg[o] = -1;
                    _breakInTicks[o] = 0;
                }
                else
                {
                    priorityActive = true;
                    pArg = held;
                }
            }
            else
            {
                _priorityOwned[o] = false;
                _priorityArg[o] = -1;
                _breakInTicks[o] = 0;
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

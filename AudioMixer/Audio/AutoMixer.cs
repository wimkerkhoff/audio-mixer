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

    // Stable hand-off: the selected mic is held with hysteresis so a brief louder moment on another
    // mic can't steal it. This is what fixes the speakerphones — their AGC applies make-up gain in a
    // talker's pauses, momentarily out-leveling the close mic; without a hold the selection chatters
    // to whatever distant mic pumped up. Measured on real hardware: hold+hysteresis cuts selection
    // flips ~5x AND tracks the closest mic better than the old crest weighting (see CLAUDE.md).
    private const int HandoffHoldTicks = 20;       // ~200 ms a winner is held before it can switch
    private const float HandoffHysteresis = 1.413f; // challenger must be ~+3 dB louder to take over

    // Reference-guided selection (opt-in per output): instead of picking the LOUDEST mic, pick the
    // room mic whose loudness envelope best matches the priority/lapel mic — a clean ground-truth copy
    // of the talker. Validated offline (tools/RefCorr): when level ranks a loud-but-bad speakerphone
    // above a quieter clean one, envelope-correlation-to-lapel inverts that and ranks the clean mic
    // first (level is fooled by the bad mic's AGC; correlation isn't, because the bad mic's envelope is
    // smeared by reverb/noise and tracks the lapel less faithfully). Needs an active priority mic as
    // the reference; falls back to level-wins when none is speaking.
    private const int RefHistFrames = 200;          // 2 s of envelope history for the correlation
    private const int RefMaxLagFrames = 60;         // search the room mic's delay vs lapel up to 600 ms
    private const int RefUpdateEvery = 5;           // recompute correlation every ~50 ms
    private const float RefSpeechRms = 0.01f;       // lapel above this (~ -40 dBFS) = talker present
    private const float CorrMs = 300f;              // correlation smoothing time constant
    private const float CorrReady = 0.05f;          // below this the correlation isn't trustworthy yet
    private const float CorrHysteresis = 0.05f;     // challenger corr must beat the leader's by this

    // Reference-free "prefer natural mic" (opt-in per output): among mics within NaturalFloorDb of the
    // loudest, pick the one with the lowest spectral-flux instability (InputChannel.CurrentFluxCv) —
    // the most natural/least scratchy. Validated offline (tools/naturalness.py): the over-processed
    // Anker measures clean on HNR/CPPS but unstable here. Combined with the level floor so it never
    // jumps to a too-quiet mic. Lower-precedence than reference-guided; both fall back to loudest.
    private const float NaturalFloorRatio = 0.398f; // -8 dB: candidate must be within 8 dB of the loudest
    private const float NaturalHysteresis = 0.05f;  // challenger CV must be this much lower to take over

    // Crest factor (peak/RMS) is NO LONGER part of the selection — on the speakerphone DSP it does
    // not track proximity (gating/AGC make it noise; it ranked the closest mic <40% of the time and
    // actually increased selection flips). It is kept only as the per-mic "clarity" readout in the
    // gear popup, mapped CrestMin..CrestMax -> [QualityFloor,1].
    private const float CrestMin = 2.2f;
    private const float CrestMax = 6.0f;
    private const float QualityFloor = 0.35f;
    private const float CrestMs = 120f;            // crest smoothing; slower than the level envelope

    private readonly int _outputCount;
    private readonly int[] _modes;                 // AutoMixMode as int (enum can't use Volatile<T>)
    private readonly float[] _strength;            // 0..1 per output
    private readonly int[] _stableOn;              // per output, stable hand-off enabled (0/1)
    private readonly float[] _env;                 // smoothed level per channel (sized to max inputs)
    private readonly float[] _crest;               // smoothed crest factor per channel (display only)
    private readonly bool[] _activeAny;            // scratch: channel selected on any output this tick
    private readonly int[] _activeInput;           // per output, selected channel index (-1 = none)
    private readonly int[] _winner;                // per output held leader, -1 = none
    private readonly int[] _winnerHold;            // per output hold countdown

    private readonly int[] _refEnabled;            // per output, reference-guided selection (0/1)
    private readonly int[] _preferNatural;         // per output, reference-free natural-mic selection (0/1)
    private readonly float[] _cv;                  // per channel spectral-flux instability (from InputChannel)
    private readonly float[][] _envHist;           // [channel] ring of instantaneous RMS (RefHistFrames)
    private int _histPos;                          // next write slot in the ring
    private int _histCount;                        // frames written (caps at RefHistFrames)
    private readonly float[] _corr;                // smoothed envelope correlation to the reference mic
    private int _refIndex = -1;                    // current reference (priority) channel, -1 = none
    private int _refTick;                          // counts ticks toward the next correlation update

    private readonly float _attackCoef;
    private readonly float _releaseCoef;
    private readonly float _crestCoef;
    private readonly float _corrCoef;

    public AutoMixer(int outputCount, int maxChannels)
    {
        _outputCount = outputCount;
        _modes = new int[outputCount];
        _strength = new float[outputCount];
        _stableOn = new int[outputCount];
        _activeInput = new int[outputCount];
        _winner = new int[outputCount];
        _winnerHold = new int[outputCount];
        for (int o = 0; o < outputCount; o++)
        {
            _strength[o] = 0.5f;
            _stableOn[o] = 1;      // stable hand-off on by default
            _winner[o] = -1;
            _activeInput[o] = -1;
        }
        _env = new float[maxChannels];
        _crest = new float[maxChannels];
        _activeAny = new bool[maxChannels];
        _refEnabled = new int[outputCount];
        _preferNatural = new int[outputCount];
        _corr = new float[maxChannels];
        _cv = new float[maxChannels];
        _envHist = new float[maxChannels][];
        for (int i = 0; i < maxChannels; i++) _envHist[i] = new float[RefHistFrames];
        _attackCoef = (float)(1 - Math.Exp(-TickSeconds / (AttackMs / 1000.0)));
        _releaseCoef = (float)(1 - Math.Exp(-TickSeconds / (ReleaseMs / 1000.0)));
        _crestCoef = (float)(1 - Math.Exp(-TickSeconds / (CrestMs / 1000.0)));
        _corrCoef = (float)(1 - Math.Exp(-(TickSeconds * RefUpdateEvery) / (CorrMs / 1000.0)));
    }

    public void SetMode(int output, AutoMixMode mode)
    {
        if (output >= 0 && output < _outputCount) Volatile.Write(ref _modes[output], (int)mode);
    }

    public void SetStrength(int output, float strength)
    {
        if (output >= 0 && output < _outputCount)
            Volatile.Write(ref _strength[output], Math.Clamp(strength, 0f, 1f));
    }

    public void SetStableHandoff(int output, bool on)
    {
        if (output >= 0 && output < _outputCount) Volatile.Write(ref _stableOn[output], on ? 1 : 0);
    }

    public void SetReferenceGuided(int output, bool on)
    {
        if (output >= 0 && output < _outputCount) Volatile.Write(ref _refEnabled[output], on ? 1 : 0);
    }

    public void SetPreferNatural(int output, bool on)
    {
        if (output >= 0 && output < _outputCount) Volatile.Write(ref _preferNatural[output], on ? 1 : 0);
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
            Crest = new float[n],
            Corr = new float[n],
            Cv = new float[n],
            Mode = new AutoMixMode[_outputCount],
            Strength = new float[_outputCount],
            Stable = new bool[_outputCount],
            ReferenceGuided = new bool[_outputCount],
            PreferNatural = new bool[_outputCount],
            Winner = new int[_outputCount],
            WinnerHold = new int[_outputCount],
            ActiveInput = new int[_outputCount],
            ReferenceInput = _refIndex,
        };
        for (int i = 0; i < n; i++) { d.Env[i] = _env[i]; d.Crest[i] = _crest[i]; d.Corr[i] = _corr[i]; d.Cv[i] = _cv[i]; }
        for (int o = 0; o < _outputCount; o++)
        {
            d.Mode[o] = (AutoMixMode)Volatile.Read(ref _modes[o]);
            d.Strength[o] = Volatile.Read(ref _strength[o]);
            d.Stable[o] = Volatile.Read(ref _stableOn[o]) != 0;
            d.ReferenceGuided[o] = Volatile.Read(ref _refEnabled[o]) != 0;
            d.PreferNatural[o] = Volatile.Read(ref _preferNatural[o]) != 0;
            d.Winner[o] = _winner[o];
            d.WinnerHold[o] = _winnerHold[o];
            d.ActiveInput[o] = Volatile.Read(ref _activeInput[o]);
        }
        return d;
    }

    public void Tick(InputChannel[] inputs)
    {
        int n = Math.Min(inputs.Length, _env.Length);

        // Level envelope per channel, plus the crest-derived clarity readout (display only — crest is
        // refreshed only while a mic hears speech, otherwise peak/RMS is meaningless noise).
        for (int i = 0; i < n; i++)
        {
            float inst = inputs[i].CurrentLevelLinear;
            float e = _env[i];
            e += (inst - e) * (inst > e ? _attackCoef : _releaseCoef);
            _env[i] = e;
            _cv[i] = inputs[i].CurrentFluxCv;

            if (inst > SilenceFloorRms)
            {
                float c = inputs[i].CurrentPeakLinear / (inst + 1e-6f);
                float ce = _crest[i];
                ce += (c - ce) * _crestCoef;
                _crest[i] = ce;
                float t = Math.Clamp((ce - CrestMin) / (CrestMax - CrestMin), 0f, 1f);
                inputs[i].Clarity = QualityFloor + (1f - QualityFloor) * t;
            }
            else
            {
                inputs[i].Clarity = float.NaN;   // idle: no estimate to show
            }
            _activeAny[i] = false;
        }

        // Reference-guided support: push the instantaneous envelope into the ring, pick the reference
        // (loudest active priority/lapel mic), and periodically refresh each room mic's correlation to
        // it. Cheap and global; per-output selection below consults _corr only when enabled.
        for (int i = 0; i < n; i++) _envHist[i][_histPos] = inputs[i].CurrentLevelLinear;
        _histPos = (_histPos + 1) % RefHistFrames;
        if (_histCount < RefHistFrames) _histCount++;

        int refIdx = -1; float refMax = 0f;
        for (int i = 0; i < n; i++)
            if (inputs[i].IsPriority && _env[i] > RefSpeechRms && _env[i] > refMax) { refMax = _env[i]; refIdx = i; }
        _refIndex = refIdx;

        if (++_refTick >= RefUpdateEvery)
        {
            _refTick = 0;
            if (refIdx >= 0 && _histCount >= RefHistFrames)
                for (int i = 0; i < n; i++)
                {
                    if (i == refIdx || inputs[i].IsPriority) { _corr[i] = 0f; continue; }
                    float c = LaggedCorr(i, refIdx);
                    if (!float.IsNaN(c)) _corr[i] += (c - _corr[i]) * _corrCoef;
                }
        }

        for (int o = 0; o < _outputCount; o++)
        {
            var mode = (AutoMixMode)Volatile.Read(ref _modes[o]);
            if (mode == AutoMixMode.Off)
            {
                for (int i = 0; i < n; i++) inputs[i].SetAutoMixGain(o, 1f);
                _winner[o] = -1;
                _activeInput[o] = -1;
                continue;
            }

            bool stable = Volatile.Read(ref _stableOn[o]) != 0;
            float s = Volatile.Read(ref _strength[o]);

            // Priority mics (e.g. a presenter's lapel) are always full level and never compete.
            // While a priority mic is active it ducks the room mics, so the same voice can't reach
            // the bus through both the clean lapel and a delayed room mic (which would comb-filter).
            bool priorityActive = false;
            float pmax = 0f;
            int pArg = -1;
            float lmax = 0f;
            int argmax = -1;
            float cmax = -1f;
            int argCorr = -1;
            for (int i = 0; i < n; i++)
            {
                if (!inputs[i].GetRoute(o)) continue;
                if (inputs[i].IsPriority)
                {
                    inputs[i].SetAutoMixGain(o, 1f);
                    if (_env[i] > PriorityActiveRms)
                    {
                        priorityActive = true;
                        if (_env[i] > pmax) { pmax = _env[i]; pArg = i; }
                    }
                    continue;
                }
                if (_env[i] > lmax) { lmax = _env[i]; argmax = i; }
                if (_corr[i] > cmax) { cmax = _corr[i]; argCorr = i; }
            }

            // Use correlation-to-reference to pick the leader only when it's enabled, a reference is
            // speaking, and the correlation has converged; otherwise fall back to loudest-wins.
            bool useCorr = Volatile.Read(ref _refEnabled[o]) != 0 && _refIndex >= 0
                           && argCorr >= 0 && cmax > CorrReady;

            // Reference-free natural-mic fallback (lower precedence than reference-guided): among mics
            // within NaturalFloorDb of the loudest, pick the lowest flux-instability (most natural).
            bool useNatural = false;
            int argNatural = -1;
            if (!useCorr && Volatile.Read(ref _preferNatural[o]) != 0 && argmax >= 0)
            {
                float floor = lmax * NaturalFloorRatio;
                float bestCv = float.MaxValue;
                for (int i = 0; i < n; i++)
                {
                    if (!inputs[i].GetRoute(o) || inputs[i].IsPriority) continue;
                    if (_env[i] < floor) continue;
                    float cv = _cv[i];
                    if (cv <= 0f) continue;            // no recent speech on this mic -> can't judge it
                    if (cv < bestCv) { bestCv = cv; argNatural = i; }
                }
                useNatural = argNatural >= 0;
            }

            int selMode = useCorr ? 1 : useNatural ? 2 : 0;        // 0 level, 1 correlation, 2 natural
            int challenger = useCorr ? argCorr : useNatural ? argNatural : argmax;

            if (priorityActive)
            {
                float pduck = Lerp(0.15f, 0f, s);
                for (int i = 0; i < n; i++)
                    if (inputs[i].GetRoute(o) && !inputs[i].IsPriority) inputs[i].SetAutoMixGain(o, pduck);
                _winner[o] = -1;
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
                _activeInput[o] = -1;
                continue;
            }

            // Held leader with hysteresis. Gate always uses it; Share uses it when Stable hand-off is
            // on, otherwise it falls back to the legacy instantaneous-loudest behavior.
            int leader;
            if (mode == AutoMixMode.Gate || stable)
            {
                int w = _winner[o];
                if (_winnerHold[o] > 0) _winnerHold[o]--;
                bool wStale = w < 0 || w >= n || !inputs[w].GetRoute(o) || inputs[w].IsPriority;
                if (wStale)
                {
                    w = challenger;
                    _winnerHold[o] = HandoffHoldTicks;
                }
                else if (challenger != w && _winnerHold[o] <= 0 && Beats(selMode, challenger, w))
                {
                    w = challenger;
                    _winnerHold[o] = HandoffHoldTicks;
                }
                _winner[o] = w;
                leader = w;
            }
            else
            {
                leader = challenger;
                _winner[o] = -1;
            }

            if (mode == AutoMixMode.Share)
            {
                float p = 1f + 3f * s;
                float floor = Lerp(0.25f, 0.03f, s);
                float refLevel = _env[leader];   // anchor the share to the (held) leader, not the instant max
                for (int i = 0; i < n; i++)
                {
                    if (!inputs[i].GetRoute(o) || inputs[i].IsPriority) continue;
                    float g = (float)Math.Pow(_env[i] / refLevel, p);
                    if (g < floor) g = floor;
                    else if (g > 1f) g = 1f;
                    inputs[i].SetAutoMixGain(o, g);
                }
                _activeInput[o] = leader;
                _activeAny[leader] = true;
            }
            else // Gate
            {
                float others = Lerp(0.15f, 0f, s);
                for (int i = 0; i < n; i++)
                {
                    if (!inputs[i].GetRoute(o) || inputs[i].IsPriority) continue;
                    inputs[i].SetAutoMixGain(o, i == leader ? 1f : others);
                }
                _activeInput[o] = leader;
                _activeAny[leader] = true;
            }
        }

        for (int i = 0; i < n; i++) inputs[i].IsAutoMixActive = _activeAny[i];
    }

    // Challenger-beats-leader test per selection mode: 1 correlation (higher better, additive margin),
    // 2 natural (lower flux-CV better, additive margin), else level (higher better, multiplicative dB).
    private bool Beats(int mode, int challenger, int held) => mode switch
    {
        1 => _corr[challenger] > _corr[held] + CorrHysteresis,
        2 => _cv[held] <= 0f || _cv[challenger] < _cv[held] - NaturalHysteresis,
        _ => _env[challenger] > _env[held] * HandoffHysteresis,
    };

    // Best-lag Pearson correlation of channel `ch`'s envelope against the reference's, over the ring,
    // counting only frames where the reference is speaking. Positive lag = ch delayed vs the reference.
    private float LaggedCorr(int ch, int refIdx)
    {
        const int W = RefHistFrames;
        float best = -2f;
        for (int d = 0; d <= RefMaxLagFrames; d++)
        {
            double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0; int nn = 0;
            for (int a = d; a < W; a++)
            {
                float x = Hist(refIdx, a);
                if (x < RefSpeechRms * 0.5f) continue;
                float y = Hist(ch, a - d);
                sx += x; sy += y; sxx += (double)x * x; syy += (double)y * y; sxy += (double)x * y; nn++;
            }
            if (nn < 50) continue;
            double cov = sxy - sx * sy / nn;
            double vx = sxx - sx * sx / nn, vy = syy - sy * sy / nn;
            if (vx <= 0 || vy <= 0) continue;
            float r = (float)(cov / Math.Sqrt(vx * vy));
            if (r > best) best = r;
        }
        return best <= -2f ? float.NaN : best;
    }

    // Reads the envelope ring by age: age 0 = most recent frame written.
    private float Hist(int ch, int age)
    {
        int idx = _histPos - 1 - age;
        idx %= RefHistFrames;
        if (idx < 0) idx += RefHistFrames;
        return _envHist[ch][idx];
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}

// Per-call snapshot of AutoMixer state for diagnostics. Per-channel arrays sized to the live channel
// count; per-output arrays sized to the output count.
public sealed class AutoMixDiag
{
    public float[] Env = Array.Empty<float>();        // smoothed level per channel (the selection metric)
    public float[] Crest = Array.Empty<float>();      // smoothed crest factor per channel (display only)
    public float[] Corr = Array.Empty<float>();       // envelope correlation to the reference mic
    public float[] Cv = Array.Empty<float>();         // spectral-flux instability per channel (lower = natural)
    public AutoMixMode[] Mode = Array.Empty<AutoMixMode>();
    public float[] Strength = Array.Empty<float>();
    public bool[] Stable = Array.Empty<bool>();
    public bool[] ReferenceGuided = Array.Empty<bool>();
    public bool[] PreferNatural = Array.Empty<bool>();
    public int[] Winner = Array.Empty<int>();         // held leader per output (-1 none)
    public int[] WinnerHold = Array.Empty<int>();     // ticks remaining before the leader can change
    public int[] ActiveInput = Array.Empty<int>();    // currently selected channel per output (-1 none)
    public int ReferenceInput = -1;                   // current reference (priority) channel, -1 none
}

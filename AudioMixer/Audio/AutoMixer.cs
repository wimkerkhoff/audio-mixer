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
    private const int GateHoldTicks = 20;          // ~200 ms a gate winner is held before it can switch
    private const float GateHysteresis = 1.413f;   // challenger must be ~+3 dB louder to take the gate

    // Crest factor (peak/RMS) → clarity weight. A close/dry mic keeps crisp transients (high crest);
    // a distant/reverberant one is smeared (low crest). AGC normalizes RMS but can't restore crest,
    // so this discriminates the better mic when levels are flattened. Mapped into [QualityFloor, 1]
    // so a muddy mic is down-weighted in the competition, not silenced.
    private const float CrestMin = 2.2f;           // ≈ +7 dB; at/below this a mic reads as muddy
    private const float CrestMax = 6.0f;           // ≈ +15.6 dB; at/above this a mic reads as clean
    private const float QualityFloor = 0.35f;      // weakest weight a muddy-but-loud mic can get
    private const float CrestMs = 120f;            // crest smoothing; slower than the level envelope

    private readonly int _outputCount;
    private readonly int[] _modes;                 // AutoMixMode as int (enum can't use Volatile<T>)
    private readonly float[] _strength;            // 0..1 per output
    private readonly int[] _qualityOn;             // per output, crest weighting enabled (0/1)
    private readonly float[] _env;                 // smoothed level per channel (sized to max inputs)
    private readonly float[] _crest;               // smoothed crest factor per channel
    private readonly float[] _weight;              // crest-derived clarity weight per channel (1 = neutral)
    private readonly bool[] _activeAny;            // scratch: channel selected on any output this tick
    private readonly int[] _activeInput;           // per output, selected channel index (-1 = none)
    private readonly int[] _gateWinner;            // per output, -1 = none
    private readonly int[] _gateHold;              // per output countdown

    private readonly float _attackCoef;
    private readonly float _releaseCoef;
    private readonly float _crestCoef;

    public AutoMixer(int outputCount, int maxChannels)
    {
        _outputCount = outputCount;
        _modes = new int[outputCount];
        _strength = new float[outputCount];
        _qualityOn = new int[outputCount];
        _activeInput = new int[outputCount];
        _gateWinner = new int[outputCount];
        _gateHold = new int[outputCount];
        for (int o = 0; o < outputCount; o++)
        {
            _strength[o] = 0.5f;
            _qualityOn[o] = 1;     // crest weighting on by default
            _gateWinner[o] = -1;
            _activeInput[o] = -1;
        }
        _env = new float[maxChannels];
        _crest = new float[maxChannels];
        _weight = new float[maxChannels];
        _activeAny = new bool[maxChannels];
        for (int i = 0; i < maxChannels; i++) _weight[i] = 1f;
        _attackCoef = (float)(1 - Math.Exp(-TickSeconds / (AttackMs / 1000.0)));
        _releaseCoef = (float)(1 - Math.Exp(-TickSeconds / (ReleaseMs / 1000.0)));
        _crestCoef = (float)(1 - Math.Exp(-TickSeconds / (CrestMs / 1000.0)));
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

    public void SetQualityWeighting(int output, bool on)
    {
        if (output >= 0 && output < _outputCount) Volatile.Write(ref _qualityOn[output], on ? 1 : 0);
    }

    // The channel the automixer is currently selecting on the given output (-1 = none/idle).
    public int ActiveInput(int output) =>
        output >= 0 && output < _outputCount ? Volatile.Read(ref _activeInput[output]) : -1;

    public void Tick(InputChannel[] inputs)
    {
        int n = Math.Min(inputs.Length, _env.Length);

        // Level envelope + crest-derived clarity weight per channel. Crest is only refreshed while a
        // mic actually hears speech (otherwise peak/RMS is dominated by noise and meaningless); the
        // weight holds its last value across gaps so a talker's mic doesn't flicker between words.
        for (int i = 0; i < n; i++)
        {
            float inst = inputs[i].CurrentLevelLinear;
            float e = _env[i];
            e += (inst - e) * (inst > e ? _attackCoef : _releaseCoef);
            _env[i] = e;

            if (inst > SilenceFloorRms)
            {
                float c = inputs[i].CurrentPeakLinear / (inst + 1e-6f);
                float ce = _crest[i];
                ce += (c - ce) * _crestCoef;
                _crest[i] = ce;
                float t = Math.Clamp((ce - CrestMin) / (CrestMax - CrestMin), 0f, 1f);
                float w = QualityFloor + (1f - QualityFloor) * t;
                _weight[i] = w;
                inputs[i].Clarity = w;
            }
            else
            {
                inputs[i].Clarity = float.NaN;   // idle: no estimate to show
            }
            _activeAny[i] = false;
        }

        for (int o = 0; o < _outputCount; o++)
        {
            var mode = (AutoMixMode)Volatile.Read(ref _modes[o]);
            if (mode == AutoMixMode.Off)
            {
                for (int i = 0; i < n; i++) inputs[i].SetAutoMixGain(o, 1f);
                _gateWinner[o] = -1;
                _activeInput[o] = -1;
                continue;
            }

            bool qualityOn = Volatile.Read(ref _qualityOn[o]) != 0;
            float Score(int i) => _env[i] * (qualityOn ? _weight[i] : 1f);

            // Priority mics (e.g. a presenter's lapel) are always full level and never compete.
            // While a priority mic is active it ducks the room mics, so the same voice can't reach
            // the bus through both the clean lapel and a delayed room mic (which would comb-filter).
            float s = Volatile.Read(ref _strength[o]);
            bool priorityActive = false;
            float pmax = 0f;
            int pArg = -1;
            float smax = 0f;
            int argmax = -1;
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
                float sc = Score(i);
                if (sc > smax) { smax = sc; argmax = i; }
            }

            if (priorityActive)
            {
                float pduck = Lerp(0.15f, 0f, s);
                for (int i = 0; i < n; i++)
                    if (inputs[i].GetRoute(o) && !inputs[i].IsPriority) inputs[i].SetAutoMixGain(o, pduck);
                _gateWinner[o] = -1;
                _activeInput[o] = pArg;
                if (pArg >= 0) _activeAny[pArg] = true;
                continue;
            }

            // Silent room or nothing competing: open everything, no ducking.
            if (argmax < 0 || _env[argmax] < SilenceFloorRms)
            {
                for (int i = 0; i < n; i++)
                    if (inputs[i].GetRoute(o)) inputs[i].SetAutoMixGain(o, 1f);
                _gateWinner[o] = -1;
                _activeInput[o] = -1;
                continue;
            }
            if (mode == AutoMixMode.Share)
            {
                float p = 1f + 3f * s;
                float floor = Lerp(0.25f, 0.03f, s);
                for (int i = 0; i < n; i++)
                {
                    if (!inputs[i].GetRoute(o) || inputs[i].IsPriority) continue;
                    float g = (float)Math.Pow(Score(i) / smax, p);
                    if (g < floor) g = floor;
                    else if (g > 1f) g = 1f;
                    inputs[i].SetAutoMixGain(o, g);
                }
                _activeInput[o] = argmax;
                _activeAny[argmax] = true;
            }
            else // Gate
            {
                int winner = _gateWinner[o];
                if (_gateHold[o] > 0) _gateHold[o]--;

                bool winnerStale = winner < 0 || winner >= n || !inputs[winner].GetRoute(o)
                    || inputs[winner].IsPriority;
                if (winnerStale)
                {
                    winner = argmax;
                    _gateHold[o] = GateHoldTicks;
                }
                else if (argmax != winner && _gateHold[o] <= 0 && Score(argmax) > Score(winner) * GateHysteresis)
                {
                    winner = argmax;
                    _gateHold[o] = GateHoldTicks;
                }
                _gateWinner[o] = winner;

                float others = Lerp(0.15f, 0f, s);
                for (int i = 0; i < n; i++)
                {
                    if (!inputs[i].GetRoute(o) || inputs[i].IsPriority) continue;
                    inputs[i].SetAutoMixGain(o, i == winner ? 1f : others);
                }
                _activeInput[o] = winner;
                if (winner >= 0) _activeAny[winner] = true;
            }
        }

        for (int i = 0; i < n; i++) inputs[i].IsAutoMixActive = _activeAny[i];
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}

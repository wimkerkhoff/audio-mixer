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

    private readonly float _attackCoef;
    private readonly float _releaseCoef;
    private readonly float _crestCoef;

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

    public void SetStableHandoff(int output, bool on)
    {
        if (output >= 0 && output < _outputCount) Volatile.Write(ref _stableOn[output], on ? 1 : 0);
    }

    // The channel the automixer is currently selecting on the given output (-1 = none/idle).
    public int ActiveInput(int output) =>
        output >= 0 && output < _outputCount ? Volatile.Read(ref _activeInput[output]) : -1;

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
            }

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
                    w = argmax;
                    _winnerHold[o] = HandoffHoldTicks;
                }
                else if (argmax != w && _winnerHold[o] <= 0 && _env[argmax] > _env[w] * HandoffHysteresis)
                {
                    w = argmax;
                    _winnerHold[o] = HandoffHoldTicks;
                }
                _winner[o] = w;
                leader = w;
            }
            else
            {
                leader = argmax;
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

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}

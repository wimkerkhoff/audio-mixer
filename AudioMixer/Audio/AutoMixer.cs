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

    private readonly int _outputCount;
    private readonly int[] _modes;                 // AutoMixMode as int (enum can't use Volatile<T>)
    private readonly float[] _strength;            // 0..1 per output
    private readonly float[] _env;                 // smoothed level per channel (sized to max inputs)
    private readonly int[] _gateWinner;            // per output, -1 = none
    private readonly int[] _gateHold;              // per output countdown

    private readonly float _attackCoef;
    private readonly float _releaseCoef;

    public AutoMixer(int outputCount, int maxChannels)
    {
        _outputCount = outputCount;
        _modes = new int[outputCount];
        _strength = new float[outputCount];
        _gateWinner = new int[outputCount];
        _gateHold = new int[outputCount];
        for (int o = 0; o < outputCount; o++) { _strength[o] = 0.5f; _gateWinner[o] = -1; }
        _env = new float[maxChannels];
        _attackCoef = (float)(1 - Math.Exp(-TickSeconds / (AttackMs / 1000.0)));
        _releaseCoef = (float)(1 - Math.Exp(-TickSeconds / (ReleaseMs / 1000.0)));
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

    public void Tick(InputChannel[] inputs)
    {
        int n = Math.Min(inputs.Length, _env.Length);

        for (int i = 0; i < n; i++)
        {
            float inst = inputs[i].CurrentLevelLinear;
            float e = _env[i];
            e += (inst - e) * (inst > e ? _attackCoef : _releaseCoef);
            _env[i] = e;
        }

        for (int o = 0; o < _outputCount; o++)
        {
            var mode = (AutoMixMode)Volatile.Read(ref _modes[o]);
            if (mode == AutoMixMode.Off)
            {
                for (int i = 0; i < n; i++) inputs[i].SetAutoMixGain(o, 1f);
                _gateWinner[o] = -1;
                continue;
            }

            // Priority mics (e.g. a presenter's lapel) are always full level and never compete.
            // While a priority mic is active it ducks the room mics, so the same voice can't reach
            // the bus through both the clean lapel and a delayed room mic (which would comb-filter).
            float s = Volatile.Read(ref _strength[o]);
            bool priorityActive = false;
            float lmax = 0f;
            int argmax = -1;
            for (int i = 0; i < n; i++)
            {
                if (!inputs[i].GetRoute(o)) continue;
                if (inputs[i].IsPriority)
                {
                    inputs[i].SetAutoMixGain(o, 1f);
                    if (_env[i] > PriorityActiveRms) priorityActive = true;
                    continue;
                }
                float e = _env[i];
                if (e > lmax) { lmax = e; argmax = i; }
            }

            if (priorityActive)
            {
                float pduck = Lerp(0.15f, 0f, s);
                for (int i = 0; i < n; i++)
                    if (inputs[i].GetRoute(o) && !inputs[i].IsPriority) inputs[i].SetAutoMixGain(o, pduck);
                _gateWinner[o] = -1;
                continue;
            }

            // Silent room or nothing competing: open everything, no ducking.
            if (argmax < 0 || lmax < SilenceFloorRms)
            {
                for (int i = 0; i < n; i++)
                    if (inputs[i].GetRoute(o)) inputs[i].SetAutoMixGain(o, 1f);
                _gateWinner[o] = -1;
                continue;
            }
            if (mode == AutoMixMode.Share)
            {
                float p = 1f + 3f * s;
                float floor = Lerp(0.25f, 0.03f, s);
                for (int i = 0; i < n; i++)
                {
                    if (!inputs[i].GetRoute(o) || inputs[i].IsPriority) continue;
                    float g = (float)Math.Pow(_env[i] / lmax, p);
                    if (g < floor) g = floor;
                    else if (g > 1f) g = 1f;
                    inputs[i].SetAutoMixGain(o, g);
                }
            }
            else // Gate
            {
                int winner = _gateWinner[o];
                if (_gateHold[o] > 0) _gateHold[o]--;

                bool winnerStale = winner < 0 || winner >= n || !inputs[winner].GetRoute(o);
                if (winnerStale)
                {
                    winner = argmax;
                    _gateHold[o] = GateHoldTicks;
                }
                else if (argmax != winner && _gateHold[o] <= 0 && _env[argmax] > _env[winner] * GateHysteresis)
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
            }
        }
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}

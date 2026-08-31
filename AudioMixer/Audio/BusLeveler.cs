using NAudio.Wave;

namespace AudioMixer.Audio;

/// <summary>How hard the leveler works. One choice that moves threshold, ratio and gain cap together.</summary>
public enum LevelerStrength
{
    Gentle,
    Medium,
    Strong,
}

/// <summary>
/// Leveler parameters. A plain value so the gain maths stays a pure function and is unit-testable
/// without a device, a window, or an audio thread.
/// </summary>
public readonly record struct LevelerSettings(
    float ThresholdDb,
    float Ratio,
    float AttackMs,     // gain moving DOWN — fast, catches a loud passage
    float ReleaseMs,    // gain moving UP — slow, because up is the direction that lifts room noise
    float MaxGainDb,
    float IdleFloorDb)
{
    public static LevelerSettings For(LevelerStrength strength) => strength switch
    {
        LevelerStrength.Gentle => new(-30f, 2f, 100f, 2000f, 6f, -45f),
        LevelerStrength.Strong => new(-22f, 4f, 100f, 2000f, 12f, -45f),
        _ => new(-26f, 3f, 100f, 2000f, 10f, -45f),
    };
}

/// <summary>One-pole smoothing coefficients. Derived off the audio thread, once per parameter change.</summary>
public readonly record struct LevelerCoefficients(float Detector, float Attack, float Release)
{
    public static LevelerCoefficients For(in LevelerSettings s, float dtSeconds)
    {
        static float Pole(float tauSeconds, float dt) =>
            tauSeconds <= 0f ? 1f : 1f - MathF.Exp(-dt / tauSeconds);

        return new LevelerCoefficients(
            Pole(BusLeveler.DetectorTc, dtSeconds),
            Pole(s.AttackMs / 1000f, dtSeconds),
            Pole(s.ReleaseMs / 1000f, dtSeconds));
    }
}

/// <param name="GainDb">Applied gain in dB.</param>
/// <param name="MeanSquare">Smoothed detector energy.</param>
/// <param name="Idle">True while the gain is frozen (see LevelerCore.Step).</param>
public readonly record struct LevelerState(float GainDb, float MeanSquare, bool Idle);

/// <summary>
/// The leveler's decision logic, isolated from the audio graph so every rule below is a unit test
/// rather than a live listen.
/// </summary>
public static class LevelerCore
{
    public const float MinGainDb = -20f;      // the limiter owns anything past this, not the leveler
    public const float IdleHysteresisDb = 3f;

    /// <summary>
    /// A single straight line of slope 1/Ratio through the threshold, in dB: unity at the threshold,
    /// cutting above it, lifting below it. The lift is what evens out a quiet talker, and MaxGainDb
    /// is the hard cap on it — every dB of lift is a dB of room floor (the floor here is acoustic
    /// HVAC, not converter hiss, so it cannot be filtered back out afterwards).
    /// </summary>
    public static float DesiredGainDb(float rmsDb, in LevelerSettings s)
    {
        float overDb = rmsDb - s.ThresholdDb;
        float ratio = s.Ratio < 1f ? 1f : s.Ratio;
        float gain = -overDb * (1f - 1f / ratio);
        return Math.Clamp(gain, MinGainDb, s.MaxGainDb);
    }

    /// <summary>
    /// Advance one sub-block.
    ///
    /// While idle the gain is carried forward *literally* — the smoother does not run at all. There
    /// is deliberately no code path here that lowers the gain when idle, so this can never behave as
    /// a downward gate: gating to silence is what made the previous microphones unusable, and a
    /// leveler that quietly re-introduced it would be the same bug wearing a different name. The
    /// worst it can do is decline to add gain.
    ///
    /// Two consequences that are wanted: the gain the last talker earned greets the next talker's
    /// first syllable, and when a priority mic ducks the room to zero the bus collapses to near
    /// silence and the leveler freezes instead of ramping into the duck and slamming back.
    /// </summary>
    public static LevelerState Step(
        in LevelerState prev, float blockMeanSquare, in LevelerSettings s, in LevelerCoefficients k)
    {
        float ms = prev.MeanSquare + (blockMeanSquare - prev.MeanSquare) * k.Detector;
        float rmsDb = 10f * MathF.Log10(ms + 1e-20f);

        // Asymmetric: entering idle needs the floor, leaving it only needs the floor + 3 dB, and the
        // exit is immediate. Any dwell on the way out would eat the first syllable of a new talker.
        bool idle = prev.Idle
            ? rmsDb < s.IdleFloorDb + IdleHysteresisDb
            : rmsDb < s.IdleFloorDb;

        if (idle) return new LevelerState(prev.GainDb, ms, true);

        float target = DesiredGainDb(rmsDb, s);
        float coeff = target < prev.GainDb ? k.Attack : k.Release;
        return new LevelerState(prev.GainDb + (target - prev.GainDb) * coeff, ms, false);
    }
}

/// <summary>
/// Live leveler settings for one output bus. Held by the bus rather than the provider so they survive
/// AudioEngine.RestartOutputBus_NoLock, which stops and restarts the graph on a device change.
/// Lock-free, same idiom as InputChannel's volatile properties.
/// </summary>
public sealed class BusLevelerSettings
{
    private int _enabled;
    private int _strength = (int)LevelerStrength.Medium;
    private float _thresholdDb = -26f, _ratio = 3f, _attackMs = 100f, _releaseMs = 2000f;
    private float _maxGainDb = 10f, _idleFloorDb = -45f, _ceilingDb = -1f;
    private int _version;

    public bool Enabled
    {
        get => Volatile.Read(ref _enabled) != 0;
        set { Volatile.Write(ref _enabled, value ? 1 : 0); Bump(); }
    }

    /// <summary>Applies the matching preset. The individual parameters stay adjustable afterwards.</summary>
    public LevelerStrength Strength
    {
        get => (LevelerStrength)Volatile.Read(ref _strength);
        set
        {
            Volatile.Write(ref _strength, (int)value);
            var s = LevelerSettings.For(value);
            ThresholdDb = s.ThresholdDb;
            Ratio = s.Ratio;
            MaxGainDb = s.MaxGainDb;
        }
    }

    public float ThresholdDb
    {
        get => Volatile.Read(ref _thresholdDb);
        set { Volatile.Write(ref _thresholdDb, Math.Clamp(value, -60f, 0f)); Bump(); }
    }

    public float Ratio
    {
        get => Volatile.Read(ref _ratio);
        set { Volatile.Write(ref _ratio, Math.Clamp(value, 1f, 10f)); Bump(); }
    }

    public float AttackMs
    {
        get => Volatile.Read(ref _attackMs);
        set { Volatile.Write(ref _attackMs, Math.Clamp(value, 5f, 500f)); Bump(); }
    }

    public float ReleaseMs
    {
        get => Volatile.Read(ref _releaseMs);
        set { Volatile.Write(ref _releaseMs, Math.Clamp(value, 200f, 8000f)); Bump(); }
    }

    /// <summary>Hard-capped at <see cref="BusLeveler.MakeupCeilingDb"/> — see DesiredGainDb.</summary>
    public float MaxGainDb
    {
        get => Volatile.Read(ref _maxGainDb);
        set { Volatile.Write(ref _maxGainDb, Math.Clamp(value, 0f, BusLeveler.MakeupCeilingDb)); Bump(); }
    }

    // Ships at -45 dBFS but MUST be calibrated per room: a bus summing several open mics can sit well
    // above that during "silence" (measured on this rig: a mic reading -30 dBFS in a presenter's
    // pauses), and then the hold would never engage and the leveler would spend every pause lifting
    // HVAC. Set it ~6 dB above the bus meter with the room empty.
    public float IdleFloorDb
    {
        get => Volatile.Read(ref _idleFloorDb);
        set { Volatile.Write(ref _idleFloorDb, Math.Clamp(value, -70f, -20f)); Bump(); }
    }

    public float CeilingDb
    {
        get => Volatile.Read(ref _ceilingDb);
        set { Volatile.Write(ref _ceilingDb, Math.Clamp(value, -12f, -0.1f)); Bump(); }
    }

    public int Version => Volatile.Read(ref _version);
    private void Bump() => Interlocked.Increment(ref _version);

    public LevelerSettings Snapshot() =>
        new(ThresholdDb, Ratio, AttackMs, ReleaseMs, MaxGainDb, IdleFloorDb);
}

/// <summary>
/// Slow broadcast-style leveler plus a protective brick-wall limiter, for ONE output bus.
///
/// **This is the only dynamics stage in the app, and it may never move upstream of the mixer.**
/// The automixer chooses the active microphone by comparing per-channel smoothed RMS latched in
/// <see cref="InputChannel"/> before the routing push. Any compression ahead of that flattens the
/// level differences that encode which mic is closest to the talker — which is exactly what a
/// transmitter's own AGC does, and why that has to be switched off on this rig.
/// </summary>
public sealed class BusLeveler : ISampleProvider
{
    public const float MakeupCeilingDb = 12f;
    public const float DetectorTc = 0.050f;      // short vs attack/release, long enough to ride out
                                                 // a glottal dip without tripping the idle freeze
    private const int SubBlockFrames = 32;       // 0.667 ms at 48 kHz
    private const float LimiterReleaseTc = 0.080f;
    private const float DisengageMs = 60f;

    private readonly ISampleProvider _source;
    private readonly BusLevelerSettings _settings;
    private readonly int _channels;

    private LevelerState _state;
    private LevelerSettings _cfg;
    private LevelerCoefficients _k;
    private int _cfgVersion = -1;

    private float _gainLin = 1f;
    private float _limGain = 1f;
    private float _ceilLin = 0.891251f;
    private float _kLimRelease;
    private float _subAcc;
    private int _subFill;

    private bool _engaged;
    private bool _disengaging;
    private float _disengageStepDb;

    private long _appliedGainBits;

    public BusLeveler(ISampleProvider source, BusLevelerSettings settings)
    {
        _source = source;
        _settings = settings;
        _channels = source.WaveFormat.Channels;
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>Gain currently applied, in dB. Latched lock-free; polled by the UI meter timer.</summary>
    public float AppliedGainDb =>
        BitConverter.Int32BitsToSingle((int)Interlocked.Read(ref _appliedGainBits));

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        if (read <= 0) return read;

        bool want = _settings.Enabled;
        if (want && !_engaged)
        {
            // Engaging is click-free without a special case: the detector starts at zero energy,
            // reads far below the idle floor, and therefore freezes at 0 dB until real audio arrives.
            _state = new LevelerState(0f, 0f, true);
            _gainLin = 1f;
            _limGain = 1f;
            _subAcc = 0f;
            _subFill = 0;
            _engaged = true;
            _disengaging = false;
        }
        else if (!want && _engaged && !_disengaging)
        {
            // Ramp back to unity before dropping out of the graph, so toggling it live doesn't click.
            _disengaging = true;
            float dtMs = 1000f * SubBlockFrames / WaveFormat.SampleRate;
            _disengageStepDb = MathF.Max(0.01f, MathF.Abs(_state.GainDb) * dtMs / DisengageMs);
        }

        if (!_engaged)
        {
            Interlocked.Exchange(ref _appliedGainBits, BitConverter.SingleToInt32Bits(0f));
            return read;   // true bypass: the samples are never touched and nothing is computed
        }

        RefreshConfigIfChanged();

        int frames = read / _channels;
        for (int f = 0; f < frames; f++)
        {
            int i = offset + f * _channels;

            float sq = 0f;
            for (int c = 0; c < _channels; c++) { float v = buffer[i + c]; sq += v * v; }
            _subAcc += sq / _channels;

            if (++_subFill >= SubBlockFrames)
            {
                AdvanceSubBlock();
                _subFill = 0;
                _subAcc = 0f;
            }

            for (int c = 0; c < _channels; c++) buffer[i + c] *= _gainLin;
            ApplyLimiter(buffer, i);
        }

        Interlocked.Exchange(ref _appliedGainBits, BitConverter.SingleToInt32Bits(_state.GainDb));

        if (_disengaging && MathF.Abs(_state.GainDb) < 0.01f && _limGain > 0.9999f)
        {
            _engaged = false;
            _disengaging = false;
        }
        return read;
    }

    private void AdvanceSubBlock()
    {
        if (_disengaging)
        {
            float g = _state.GainDb;
            g = g > 0f ? MathF.Max(0f, g - _disengageStepDb) : MathF.Min(0f, g + _disengageStepDb);
            _state = _state with { GainDb = g };
        }
        else
        {
            _state = LevelerCore.Step(_state, _subAcc / SubBlockFrames, _cfg, _k);
        }
        _gainLin = MathF.Pow(10f, _state.GainDb / 20f);
    }

    // Instant attack computed from the sample about to be written, so the ceiling is a true brick
    // wall with zero added latency — which matters because one bus is a live monitor headset and any
    // look-ahead would show up as delay. The trailing clamp catches float rounding on (ceil/peak)*peak.
    private void ApplyLimiter(float[] buffer, int i)
    {
        float peak = 0f;
        for (int c = 0; c < _channels; c++)
        {
            float a = MathF.Abs(buffer[i + c]);
            if (a > peak) peak = a;
        }

        if (peak > _ceilLin)
        {
            float req = _ceilLin / peak;
            if (req < _limGain) _limGain = req;
        }

        for (int c = 0; c < _channels; c++)
        {
            float v = buffer[i + c] * _limGain;
            buffer[i + c] = v > _ceilLin ? _ceilLin : v < -_ceilLin ? -_ceilLin : v;
        }

        _limGain += (1f - _limGain) * _kLimRelease;
    }

    private void RefreshConfigIfChanged()
    {
        int version = _settings.Version;
        if (version == _cfgVersion) return;
        _cfgVersion = version;

        _cfg = _settings.Snapshot();
        float dt = (float)SubBlockFrames / WaveFormat.SampleRate;
        _k = LevelerCoefficients.For(_cfg, dt);
        _ceilLin = MathF.Pow(10f, _settings.CeilingDb / 20f);
        _kLimRelease = 1f - MathF.Exp(-1f / (LimiterReleaseTc * WaveFormat.SampleRate));
    }
}

namespace AudioMixer.Audio;

/// <summary>
/// Spots runs of exact digital zero spanning buffers. A run is reported once, the moment it reaches
/// <c>minSamples</c>, so a long outage (a transmitter switched off) counts as one gap and a link that
/// keeps breaking up counts every break.
/// </summary>
public sealed class DropoutCounter
{
    private readonly int _minSamples;
    private int _run;

    public DropoutCounter(int minSamples) => _minSamples = minSamples;

    /// <summary>True when this buffer completes a new gap.</summary>
    public bool Observe(ReadOnlySpan<float> buffer)
    {
        int trailing = 0;
        while (trailing < buffer.Length && buffer[buffer.Length - 1 - trailing] == 0f) trailing++;
        int before = _run;
        // Saturate rather than overflow on an hours-long outage.
        _run = trailing == buffer.Length ? (int)Math.Min((long)before + trailing, int.MaxValue) : trailing;
        return before < _minSamples && _run >= _minSamples;
    }
}

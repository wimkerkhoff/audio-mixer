namespace AudioMixer.Services;

/// <summary>
/// Turns a monotonic gap counter into "gaps in the last minute", from samples taken at the health
/// rate (~1 Hz). Pure, so the rule's arithmetic is testable without a device.
/// </summary>
public sealed class DropoutWindow
{
    public const long WindowMs = 60_000;

    private readonly Queue<(long Ms, long Count)> _samples = new();

    public int Record(long nowMs, long cumulative)
    {
        // A counter that went backwards belongs to a new channel object (strips rebuilt): start over.
        if (_samples.Count > 0 && cumulative < _samples.Last().Count) _samples.Clear();
        _samples.Enqueue((nowMs, cumulative));
        while (_samples.Count > 1 && nowMs - _samples.Peek().Ms > WindowMs) _samples.Dequeue();
        return (int)(cumulative - _samples.Peek().Count);
    }
}

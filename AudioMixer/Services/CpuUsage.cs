using System.Runtime.InteropServices;

namespace AudioMixer.Services;

/// <summary>
/// Whole-PC CPU load, for the panel. On 2026-09-26 this PC sat at 88-100% during a service (OBS,
/// Zoom, audiodg) and routed mics underran ~1/s until the mixer ran at High priority; on 2026-10-25
/// a jump of OBS to 75% pinned it at 100% and every mic underran. The operator could not see
/// either. One GetSystemTimes call a second costs microseconds, unlike a PerformanceCounter.
/// </summary>
public static class CpuUsage
{
    /// <summary>Cumulative 100 ns ticks; kernel time INCLUDES idle time, as Windows reports it.</summary>
    public readonly record struct Times(long Idle, long Kernel, long User);

    /// <summary>Busy share of all cores between two readings, 0-100; null if no time passed.</summary>
    public static double? Percent(Times before, Times after)
    {
        long idle = after.Idle - before.Idle;
        long total = after.Kernel - before.Kernel + (after.User - before.User);
        if (total <= 0 || idle < 0) return null;
        return Math.Clamp(100.0 * (total - idle) / total, 0, 100);
    }

    /// <summary>Operator's bands: green under 70, amber 70 to 90, red 90 and over.</summary>
    public static string Band(double percent) => percent >= 90 ? "bad" : percent >= 70 ? "warn" : "ok";

    public static Times? Read()
    {
        if (!GetSystemTimes(out long idle, out long kernel, out long user)) return null;
        return new Times(idle, kernel, user);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);
}

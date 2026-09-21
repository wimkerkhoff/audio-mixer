using System.IO;

namespace AudioMixer.Services;

/// <summary>
/// Keeps always-on recording from filling the disk.
///
/// Recording every service unattended is only safe with a bound, and age alone is not one. At
/// 48 kHz stereo float32 a single stream is 1.29 GB/hour; five mics and two buses is 9 GB/hour, so a
/// two-hour service is ~18 GB and four weeks at two services a week is ~144 GB — more than the free
/// space on the machine this runs on. Pruning at 28 days would arrive about a week after the disk
/// filled.
///
/// So there are two rules, and the space one is what actually protects the machine:
///   * anything past <see cref="RetentionDays"/> goes, regardless of space;
///   * while free space is under <see cref="LowSpaceGb"/>, the OLDEST recordings go until it is not.
///
/// Session records are never touched here — they live elsewhere, are tens of kilobytes, and are the
/// thing you still want when the audio is gone.
/// </summary>
public sealed class RecordingRetention
{
    /// <summary>Operator's choice, 2026-09-20.</summary>
    public const int RetentionDays = 28;

    /// <summary>Below this, start deleting oldest-first even if nothing is old enough to expire.</summary>
    public const double LowSpaceGb = 20;

    /// <summary>Do not begin a recording with less than this free — it would not survive the session.</summary>
    public const double StartFloorGb = 15;

    /// <summary>Stop an in-flight recording here. Lower than the start floor so a session in progress
    /// is given every chance to finish rather than being cut off the moment it dips.</summary>
    public const double StopFloorGb = 8;

    private readonly string[] _folders;

    public RecordingRetention(params string[] folders) => _folders = folders;

    public static double FreeGb(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return root == null ? double.MaxValue : new DriveInfo(root).AvailableFreeSpace / (double)(1L << 30);
        }
        catch { return double.MaxValue; }   // unknown free space must never block a recording
    }

    public bool HasRoomToStart() => _folders.Length == 0 || FreeGb(_folders[0]) >= StartFloorGb;

    public bool MustStopNow() => _folders.Length > 0 && FreeGb(_folders[0]) < StopFloorGb;

    /// <summary>Deletes expired files, then oldest-first while space is short. Returns what it removed.</summary>
    public (int Files, double Gb) Prune(DateTime? nowUtc = null)
    {
        var cutoff = (nowUtc ?? DateTime.UtcNow).AddDays(-RetentionDays);
        var files = Wavs().OrderBy(f => f.LastWriteTimeUtc).ToList();

        int removed = 0;
        double bytes = 0;

        foreach (var f in files.Where(f => f.LastWriteTimeUtc < cutoff).ToList())
        {
            if (!Delete(f, ref bytes)) continue;
            removed++;
            files.Remove(f);
        }

        // Oldest-first until there is room again. A recording still being written is skipped by
        // Delete throwing on the open handle, which is the behaviour we want and not worth special
        // casing: the file in progress is the one the operator is least willing to lose.
        foreach (var f in files)
        {
            if (_folders.Length == 0 || FreeGb(_folders[0]) >= LowSpaceGb) break;
            if (!Delete(f, ref bytes)) continue;
            removed++;
        }

        if (removed > 0)
        {
            Audio.AudioLog.Write(
                $"Recording retention: removed {removed} file(s), {bytes / (1L << 30):F1} GB.");
        }
        return (removed, bytes / (1L << 30));
    }

    private static bool Delete(FileInfo f, ref double bytes)
    {
        try
        {
            long len = f.Length;
            f.Delete();
            bytes += len;
            return true;
        }
        catch { return false; }
    }

    private IEnumerable<FileInfo> Wavs()
    {
        foreach (var folder in _folders)
        {
            if (!Directory.Exists(folder)) continue;
            IEnumerable<string> paths;
            try { paths = Directory.EnumerateFiles(folder, "*.wav"); }
            catch { continue; }
            foreach (var p in paths)
            {
                FileInfo? info = null;
                try { info = new FileInfo(p); } catch { }
                if (info != null) yield return info;
            }
        }
    }
}

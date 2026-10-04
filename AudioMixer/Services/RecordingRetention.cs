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
/// So there are four rules, and the space one is what actually protects the machine:
///   * a per-mic capture past <see cref="DiagRetentionDays"/>, or a mix past <see cref="RetentionDays"/>, goes;
///   * a stub — a stamp none of whose files reached two minutes — goes after a day;
///   * while free space is under <see cref="LowSpaceGb"/>, the OLDEST recordings go until it is not.
///
/// Session records are never touched here — they live elsewhere, are tens of kilobytes, and are the
/// thing you still want when the audio is gone.
/// </summary>
public sealed class RecordingRetention
{
    /// <summary>Operator's choice, 2026-09-20. Applies to the bus mixes: what was actually sent.</summary>
    public const int RetentionDays = 28;

    /// <summary>
    /// The per-mic captures are ~80% of the bytes and are only worth anything while a session is still
    /// being reviewed; one worth keeping longer is a fixture and belongs in <see cref="KeepFolder"/>.
    /// </summary>
    public const int DiagRetentionDays = 14;

    /// <summary>
    /// Every launch starts a recording, so restarts leave stamps of a few seconds (seven on 2026-10-04
    /// alone). Under this length a stamp holds nothing a review can use. Two minutes of 48 kHz stereo
    /// float32 — the bus mixes are stereo, so a stamp only counts as a stub if its mixes are short too.
    /// </summary>
    public const long StubBytes = 2L * 60 * 48_000 * 2 * 4;

    /// <summary>
    /// Below this, start deleting oldest-first even if nothing is old enough to expire. Sized to hold a
    /// whole service: the 2026-10-04 rig (nine strips, two buses) wrote ~10 GB an hour, and the
    /// recording runs up to three. At 20 GB the disk sat one hour from the start floor all morning.
    /// </summary>
    public const double LowSpaceGb = 40;

    /// <summary>Disk a recording uses per hour for this many 48 kHz float32 channels.</summary>
    public static double GbPerHour(int audioChannels) =>
        audioChannels * 48_000.0 * 4 * 3600 / (1L << 30);

    /// <summary>Do not begin a recording with less than this free — it would not survive the session.</summary>
    public const double StartFloorGb = 15;

    /// <summary>Stop an in-flight recording here. Lower than the start floor so a session in progress
    /// is given every chance to finish rather than being cut off the moment it dips.</summary>
    public const double StopFloorGb = 8;

    private readonly string[] _folders;

    public RecordingRetention(params string[] folders) => _folders = folders;

    /// <summary>Free space of a folder's drive. Replaceable so tests do not depend on this disk.</summary>
    public Func<string, double> FreeGbOf { get; init; } = FreeGb;

    public static double FreeGb(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return root == null ? double.MaxValue : new DriveInfo(root).AvailableFreeSpace / (double)(1L << 30);
        }
        catch { return double.MaxValue; }   // unknown free space must never block a recording
    }

    public bool HasRoomToStart() => _folders.Length == 0 || FreeGbOf(_folders[0]) >= StartFloorGb;

    public bool MustStopNow() => _folders.Length > 0 && FreeGbOf(_folders[0]) < StopFloorGb;

    /// <summary>
    /// A capture kept as a replay fixture belongs in `analysis/keep/`, which ReplayRig also searches
    /// and this never walks — EnumerateFiles is top-level only, so a subfolder is already immune.
    ///
    /// It has to be somewhere, because the fixtures live in the folder this prunes: both golden
    /// baselines referenced a stamp 42 days older than the 28-day rule, so their source WAVs were
    /// deleted on the first launch after retention shipped. The fixtures are the only way to exercise
    /// the selector without a room full of people, so losing them silently is expensive.
    /// </summary>
    public const string KeepFolder = "keep";

    /// <summary>Deletes expired files and stubs, then oldest-first while space is short.</summary>
    public (int Files, double Gb) Prune(DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var files = Wavs().OrderBy(f => f.LastWriteTimeUtc).ToList();

        int removed = 0;
        double bytes = 0;

        foreach (var f in files.Where(f => IsExpired(f, now)).ToList())
        {
            if (!Delete(f, ref bytes)) continue;
            removed++;
            files.Remove(f);
        }

        // Stubs, a day on: long enough that a restart mid-service never sweeps the stamp before it.
        foreach (var stub in files.GroupBy(f => Stamp(f.Name))
                     .Where(g => g.Key != null
                                 && g.All(f => f.Length < StubBytes)
                                 && g.All(f => f.LastWriteTimeUtc < now.AddDays(-1)))
                     .SelectMany(g => g).ToList())
        {
            if (!Delete(stub, ref bytes)) continue;
            removed++;
            files.Remove(stub);
        }

        // Oldest-first until there is room again. A recording still being written is skipped by
        // Delete throwing on the open handle, which is the behaviour we want and not worth special
        // casing: the file in progress is the one the operator is least willing to lose.
        foreach (var f in files)
        {
            if (_folders.Length == 0 || FreeGbOf(_folders[0]) >= LowSpaceGb) break;
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

    private static bool IsExpired(FileInfo f, DateTime now) =>
        f.LastWriteTimeUtc < now.AddDays(f.Name.StartsWith("diag-", StringComparison.OrdinalIgnoreCase)
                                             ? -DiagRetentionDays : -RetentionDays);

    private static readonly System.Text.RegularExpressions.Regex StampPattern =
        new(@"\d{8}-\d{6}", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>The yyyyMMdd-HHmmss that ties a recording's files together, or null.</summary>
    public static string? Stamp(string fileName)
    {
        var m = StampPattern.Match(fileName);
        return m.Success ? m.Value : null;
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

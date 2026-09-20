using System.IO;
using System.Text.Json;

namespace AudioMixer.Services;

/// <summary>One saved session, without reading its contents.</summary>
public sealed record SessionFile(string Path, string Stamp, DateTime WrittenUtc, long Bytes);

/// <summary>
/// Saved session records: one small JSON file per service, written whether or not anyone armed audio
/// recording.
///
/// That "whether or not" is the point. The failure this exists for is a volunteer running a service
/// alone with nobody watching — if a session record only appeared when someone remembered to press
/// record, it would inherit exactly the defect the opt-in log already has, and the run that matters
/// is always the one nobody prepared for.
///
/// Cheap enough to keep: aggregates are tens of kilobytes against 2.4 GB for one session's audio.
/// They also carry no speech content — you cannot reconstruct what was said from "speech median
/// −45 dBFS, 177 hand-offs" — so always-on telemetry does not carry recorded audio's privacy weight.
/// </summary>
public sealed class SessionStore
{
    /// <summary>Operator's choice, 2026-09-20: keep everything, prune at 90 days.</summary>
    public const int RetentionDays = 90;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string Directory { get; }

    public SessionStore(string? directory = null)
    {
        Directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "AudioMixer", "sessions");
    }

    public static string StampFor(DateTime local) => local.ToString("yyyyMMdd-HHmmss");

    public string PathFor(string stamp) => Path.Combine(Directory, $"session-{stamp}.json");

    /// <summary>
    /// Writes the record and prunes old ones. Returns the path, or null if it could not be written —
    /// a session record that throws would take down the mixer it is describing, which is a worse
    /// outcome than losing the record.
    /// </summary>
    public string? Save(SessionSummary summary)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var path = PathFor(summary.Stamp);
            File.WriteAllText(path, JsonSerializer.Serialize(summary, Options));
            Prune();
            return path;
        }
        catch (Exception ex)
        {
            Audio.AudioLog.Write($"Session record could not be written: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Newest first, so the Session tab can open the most recent without reading them all.</summary>
    public IReadOnlyList<SessionFile> List()
    {
        try
        {
            if (!System.IO.Directory.Exists(Directory)) return Array.Empty<SessionFile>();
            return System.IO.Directory.EnumerateFiles(Directory, "session-*.json")
                .Select(p => new FileInfo(p))
                .Select(f => new SessionFile(f.FullName,
                    Path.GetFileNameWithoutExtension(f.Name).Replace("session-", ""),
                    f.LastWriteTimeUtc, f.Length))
                .OrderByDescending(f => f.WrittenUtc)
                .ToList();
        }
        catch { return Array.Empty<SessionFile>(); }
    }

    public SessionSummary? Load(string path)
    {
        try { return JsonSerializer.Deserialize<SessionSummary>(File.ReadAllText(path), Options); }
        catch (Exception ex)
        {
            Audio.AudioLog.Write($"Session record unreadable ({path}): {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Deletes records past the retention window. Failure here is never worth surfacing.</summary>
    public int Prune(DateTime? nowUtc = null)
    {
        var cutoff = (nowUtc ?? DateTime.UtcNow).AddDays(-RetentionDays);
        int removed = 0;
        foreach (var f in List().Where(f => f.WrittenUtc < cutoff))
        {
            try { File.Delete(f.Path); removed++; } catch { }
        }
        return removed;
    }
}

using System.IO;
using System.Text.Json;
using AudioMixer.Models;

namespace AudioMixer.Services;

/// <summary>
/// The rig's configuration: every strip's device, routing, level and role. The single
/// highest-value file the app writes — losing it means an operator remapping the whole rig by hand
/// before a service.
/// </summary>
public sealed class PresetStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        IncludeFields = false,
    };

    public string PresetPath { get; }

    /// <summary>
    /// The previous good copy, kept because the write is a replace. One autosave stale at worst, which
    /// is a far better failure than an unconfigured mixer.
    /// </summary>
    public string BackupPath => PresetPath + ".bak";

    private string TempPath => PresetPath + ".tmp";

    /// <param name="path">Overridable so tests never touch the operator's real preset.</param>
    public PresetStore(string? path = null)
    {
        if (path != null)
        {
            PresetPath = path;
        }
        else
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(appData, "AudioMixer");
            Directory.CreateDirectory(dir);
            PresetPath = Path.Combine(dir, "preset.json");
        }
    }

    public MixerPreset? Load()
    {
        var preset = TryRead(PresetPath);
        if (preset != null) return preset;

        // A torn or truncated preset is indistinguishable from an unconfigured mixer once it has been
        // rejected, so try the previous good copy before giving up on the rig's whole configuration.
        if (File.Exists(PresetPath) && File.Exists(BackupPath))
        {
            preset = TryRead(BackupPath);
            if (preset != null)
            {
                Audio.AudioLog.Write($"Preset recovered from backup ({BackupPath}) - "
                                   + "the main file was unreadable, settings may be one save stale.");
                return preset;
            }
        }
        return null;
    }

    private MixerPreset? TryRead(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<MixerPreset>(stream, Options);
        }
        catch (Exception ex)
        {
            // Returning null here is indistinguishable, to the operator, from never having saved a
            // preset: every channel comes up with no device. Say so somewhere they can read.
            System.Diagnostics.Trace.WriteLine($"Preset load failed: {ex.Message}");
            Audio.AudioLog.Write($"Preset load FAILED ({path}): {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Writes to a temporary file and replaces, so an interrupted save cannot leave a half-written
    /// preset behind. This app is force-killed often enough for that to matter — it is the same reason
    /// session records checkpoint rather than only writing on exit — and `WriteAllText` truncates the
    /// existing file before it writes a byte, so the window where a kill destroys the rig's
    /// configuration is the whole duration of the write.
    /// </summary>
    public void Save(MixerPreset preset)
    {
        var json = JsonSerializer.Serialize(preset, Options);
        var dir = Path.GetDirectoryName(PresetPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        File.WriteAllText(TempPath, json);
        if (!File.Exists(PresetPath))
        {
            File.Move(TempPath, PresetPath);
            return;
        }

        try
        {
            // Replace keeps the outgoing file as the backup, atomically, in one call.
            File.Replace(TempPath, PresetPath, BackupPath, ignoreMetadataErrors: true);
        }
        catch (IOException)
        {
            // %APPDATA% can be a roaming or redirected share, where Replace is not supported. An
            // overwriting Move still beats the truncate-in-place this used to do, so degrade rather
            // than lose the save outright.
            File.Move(TempPath, PresetPath, overwrite: true);
        }
    }
}

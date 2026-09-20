using System.IO;
using System.Text.Json;
using AudioMixer.Models;

namespace AudioMixer.Services;

public sealed class PresetStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        IncludeFields = false,
    };

    public string PresetPath { get; }

    public PresetStore()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appData, "AudioMixer");
        Directory.CreateDirectory(dir);
        PresetPath = Path.Combine(dir, "preset.json");
    }

    public MixerPreset? Load()
    {
        if (!File.Exists(PresetPath)) return null;
        try
        {
            using var stream = File.OpenRead(PresetPath);
            return JsonSerializer.Deserialize<MixerPreset>(stream, Options);
        }
        catch (Exception ex)
        {
            // Returning null here is indistinguishable, to the operator, from never having saved a
            // preset: every channel comes up with no device. Say so somewhere they can read.
            System.Diagnostics.Trace.WriteLine($"Preset load failed: {ex.Message}");
            Audio.AudioLog.Write($"Preset load FAILED ({PresetPath}): {ex.GetType().Name}: {ex.Message} "
                               + "- starting with an unconfigured mixer.");
            return null;
        }
    }

    public void Save(MixerPreset preset)
    {
        var json = JsonSerializer.Serialize(preset, Options);
        File.WriteAllText(PresetPath, json);
    }
}

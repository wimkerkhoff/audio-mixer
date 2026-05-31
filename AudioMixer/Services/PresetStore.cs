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
            System.Diagnostics.Trace.WriteLine($"Preset load failed: {ex.Message}");
            return null;
        }
    }

    public void Save(MixerPreset preset)
    {
        var json = JsonSerializer.Serialize(preset, Options);
        File.WriteAllText(PresetPath, json);
    }
}

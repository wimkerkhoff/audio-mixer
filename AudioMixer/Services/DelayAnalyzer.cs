using System.IO;
using NAudio.Wave;

namespace AudioMixer.Services;

public static class DelayAnalyzer
{
    public sealed record InputResult(int InputIndex, string Path, double FirstTransientMs, int SuggestedDelayMs, float PeakAmplitude);
    public sealed record AnalysisOutcome(InputResult[] Inputs, string? Warning);

    public static AnalysisOutcome Analyze(IEnumerable<(int InputIndex, string Path)> recordings)
    {
        var arrivals = new List<(int Index, string Path, double TimeMs, float Peak)>();
        var warnings = new List<string>();

        foreach (var (idx, path) in recordings)
        {
            if (!File.Exists(path)) { warnings.Add($"Input {idx + 1}: file missing"); continue; }
            var (timeMs, peak) = FindFirstTransient(path);
            if (double.IsNaN(timeMs))
            {
                warnings.Add($"Input {idx + 1}: no clear transient detected (peak {peak:F3})");
            }
            arrivals.Add((idx, path, timeMs, peak));
        }

        double maxArrival = 0;
        foreach (var a in arrivals)
        {
            if (!double.IsNaN(a.TimeMs) && a.TimeMs > maxArrival) maxArrival = a.TimeMs;
        }

        var results = arrivals
            .Select(a =>
            {
                int suggested = double.IsNaN(a.TimeMs) ? 0 : (int)Math.Round(maxArrival - a.TimeMs);
                return new InputResult(a.Index, a.Path, a.TimeMs, suggested, a.Peak);
            })
            .ToArray();

        return new AnalysisOutcome(results, warnings.Count == 0 ? null : string.Join(Environment.NewLine, warnings));
    }

    private static (double TimeMs, float Peak) FindFirstTransient(string path)
    {
        using var reader = new AudioFileReader(path);
        int sampleRate = reader.WaveFormat.SampleRate;
        int channels = reader.WaveFormat.Channels;
        int totalFrames = (int)Math.Min(int.MaxValue / channels, reader.Length / (reader.WaveFormat.BitsPerSample / 8) / channels);
        int totalSamples = totalFrames * channels;
        var buf = new float[totalSamples];
        int read = reader.Read(buf, 0, totalSamples);
        if (read <= 0) return (double.NaN, 0f);

        float max = 0f;
        for (int i = 0; i < read; i++)
        {
            float a = buf[i];
            if (a < 0f) a = -a;
            if (a > max) max = a;
        }
        if (max < 0.05f) return (double.NaN, max);

        float threshold = max * 0.5f;
        for (int i = 0; i < read; i++)
        {
            float a = buf[i];
            if (a < 0f) a = -a;
            if (a >= threshold)
            {
                int frameIdx = i / channels;
                return (frameIdx * 1000.0 / sampleRate, max);
            }
        }
        return (double.NaN, max);
    }
}

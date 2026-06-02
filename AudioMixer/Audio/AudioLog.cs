using System.IO;

namespace AudioMixer.Audio;

public static class AudioLog
{
    public static readonly string Path = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "AudioMixer.log");

    // Off by default — the meter loop writes ~1 line/sec, so we don't want a file growing on every
    // run. Enable by setting the AUDIOMIXER_LOG environment variable (any non-empty value).
    public static bool Enabled { get; set; } =
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AUDIOMIXER_LOG"));

    private static readonly object _lock = new();
    private static bool _initialized;

    public static void Write(string message)
    {
        if (!Enabled) return;
        lock (_lock)
        {
            try
            {
                if (!_initialized)
                {
                    File.AppendAllText(Path, $"\n=== AudioMixer started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n");
                    _initialized = true;
                }
                File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff} {message}\n");
            }
            catch { }
        }
    }
}

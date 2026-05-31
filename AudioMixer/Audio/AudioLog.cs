using System.IO;

namespace AudioMixer.Audio;

public static class AudioLog
{
    public static readonly string Path = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "AudioMixer.log");

    private static readonly object _lock = new();
    private static bool _initialized;

    public static void Write(string message)
    {
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

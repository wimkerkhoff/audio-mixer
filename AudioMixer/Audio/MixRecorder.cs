using System.IO;
using NAudio.Wave;

namespace AudioMixer.Audio;

public sealed class MixRecorder : IDisposable
{
    private readonly object _lock = new();
    private WaveFileWriter? _writer;
    public string? CurrentPath { get; private set; }

    public bool IsRecording
    {
        get { lock (_lock) return _writer != null; }
    }

    public void Start(string path, WaveFormat format)
    {
        lock (_lock)
        {
            Stop_NoLock();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _writer = new WaveFileWriter(path, format);
            CurrentPath = path;
        }
    }

    public void Stop()
    {
        lock (_lock) Stop_NoLock();
    }

    private void Stop_NoLock()
    {
        if (_writer != null)
        {
            try { _writer.Flush(); } catch { }
            try { _writer.Dispose(); } catch { }
            _writer = null;
        }
    }

    public void WriteSamples(float[] buffer, int offset, int count)
    {
        lock (_lock)
        {
            _writer?.WriteSamples(buffer, offset, count);
        }
    }

    public void Dispose() => Stop();
}

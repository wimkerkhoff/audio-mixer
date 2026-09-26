using System.Diagnostics;
using System.IO;
using NAudio.Wave;

namespace AudioMixer.Audio;

public sealed class MixRecorder : IDisposable
{
    /// <summary>
    /// A shortfall against wall-clock time bigger than this is a gap, not clock drift: two devices'
    /// clocks differ by tens of ppm, well under 0.5 s across the 1-hour recording cap.
    /// </summary>
    public const double GapThresholdSeconds = 0.5;

    private readonly object _lock = new();
    private readonly Stopwatch _clock = new();
    private WaveFileWriter? _writer;
    private long _framesWritten;
    public string? CurrentPath { get; private set; }

    /// <summary>
    /// Why writing stopped, if it did. A throw from the writer (disk full, file yanked) used to
    /// propagate into the capture or render callback; now the recorder stops itself and says so, so
    /// Checks can report it instead of the UI saying "recording" over a file that stopped growing.
    /// </summary>
    public string? Fault { get; private set; }

    public bool IsRecording
    {
        get { lock (_lock) return _writer != null; }
    }

    /// <param name="joinedLate">
    /// How far into the session this file starts — a strip or bus that got its device after recording
    /// began. The lead-in is written as silence so the file lines up with the rest of its stamp.
    /// </param>
    public void Start(string path, WaveFormat format, TimeSpan joinedLate = default)
    {
        lock (_lock)
        {
            Stop_NoLock();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _writer = new WaveFileWriter(path, format);
            CurrentPath = path;
            Fault = null;
            _framesWritten = 0;
            _offset = joinedLate;
            _clock.Restart();
        }
        if (joinedLate > TimeSpan.Zero) PadToNow();
    }

    private TimeSpan _offset;

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
        _clock.Reset();
    }

    public void WriteSamples(float[] buffer, int offset, int count)
    {
        lock (_lock)
        {
            if (_writer == null) return;
            try
            {
                _writer.WriteSamples(buffer, offset, count);
                _framesWritten += count / _writer.WaveFormat.Channels;
            }
            catch (Exception ex)
            {
                Fail_NoLock(ex);
            }
        }
    }

    /// <summary>
    /// Writes silence for any time the source was away, so every file of a session stays aligned to
    /// the others and to the decisions CSV. Without it an unplugged receiver or output simply left
    /// the gap out, and everything after it sat early by the length of the outage. Call it only off
    /// the audio threads (a restart path), before the source resumes: a long gap is many MB of zeros.
    /// </summary>
    public void PadToNow() => PadTo(_offset + _clock.Elapsed);

    internal void PadTo(TimeSpan elapsed)
    {
        lock (_lock)
        {
            if (_writer == null) return;
            var format = _writer.WaveFormat;
            long expected = (long)(elapsed.TotalSeconds * format.SampleRate);
            long missing = expected - _framesWritten;
            if (missing < GapThresholdSeconds * format.SampleRate) return;

            AudioLog.Write($"Recorder {Path.GetFileName(CurrentPath)}: padding " +
                           $"{missing / (double)format.SampleRate:F1}s of silence for a gap");
            var zeros = new float[format.SampleRate * format.Channels / 10];   // 100 ms
            try
            {
                while (missing > 0)
                {
                    int frames = (int)Math.Min(missing, zeros.Length / format.Channels);
                    _writer.WriteSamples(zeros, 0, frames * format.Channels);
                    _framesWritten += frames;
                    missing -= frames;
                }
            }
            catch (Exception ex)
            {
                Fail_NoLock(ex);
            }
        }
    }

    private void Fail_NoLock(Exception ex)
    {
        Fault = $"{ex.GetType().Name}: {ex.Message}";
        AudioLog.Write($"Recorder {Path.GetFileName(CurrentPath)} stopped writing: {Fault}");
        Stop_NoLock();
    }

    public void Dispose() => Stop();
}

using System.IO;
using NAudio.Wave;

namespace AudioMixer.Audio.Replay;

/// <summary>
/// An <see cref="IWaveIn"/> that replays a recorded diag WAV instead of capturing a device, so the
/// whole graph downstream (gain, delay, flux-CV, automixer, meters, LEDs) can be exercised without a
/// room full of people. Driven by <see cref="ReplayRig"/> — it never runs its own clock, because N
/// independently-timed sources would drift apart and desync the automix decision.
/// </summary>
public sealed class ReplaySource : IWaveIn
{
    // The diag tap writes the internal format, and the header's declared data size is 0 or stale
    // while a recording is in progress (WaveFileWriter only finalizes on Dispose). So we take every
    // byte after the 'data' chunk header regardless of what it claims — same trick as tools/live_wav.py.
    private readonly FileStream _stream;
    private readonly long _dataOffset;
    private readonly int _blockAlign;
    private byte[] _buffer = [];

    public WaveFormat WaveFormat { get; set; }
    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    public string Path { get; }
    public long TotalFrames { get; private set; }
    public long PositionFrames { get; private set; }
    public bool EndOfFile => PositionFrames >= TotalFrames;

    private bool _running;

    public ReplaySource(string path)
    {
        Path = path;
        // FileShare.ReadWrite so a session still being recorded can be replayed in another process.
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        (WaveFormat, _dataOffset) = ReadHeader(_stream, path);
        _blockAlign = WaveFormat.BlockAlign;
        RefreshLength();
    }

    /// <summary>Re-reads the file length, so a still-growing recording keeps yielding new audio.</summary>
    public void RefreshLength() => TotalFrames = Math.Max(0, (_stream.Length - _dataOffset) / _blockAlign);

    private static (WaveFormat, long) ReadHeader(FileStream fs, string path)
    {
        using var br = new BinaryReader(fs, System.Text.Encoding.ASCII, leaveOpen: true);
        if (new string(br.ReadChars(4)) != "RIFF") throw new InvalidDataException($"not a RIFF file: {path}");
        br.ReadUInt32();
        if (new string(br.ReadChars(4)) != "WAVE") throw new InvalidDataException($"not a WAVE file: {path}");

        WaveFormat? format = null;
        while (fs.Position + 8 <= fs.Length)
        {
            string id = new(br.ReadChars(4));
            uint size = br.ReadUInt32();
            if (id == "fmt ")
            {
                format = WaveFormat.FromFormatChunk(br, (int)size);
            }
            else if (id == "data")
            {
                if (format == null) throw new InvalidDataException($"data before fmt in {path}");
                return (format, fs.Position);
            }
            else
            {
                fs.Position += size + (size & 1);   // chunks are word-aligned
            }
        }
        throw new InvalidDataException($"no data chunk in {path}");
    }

    public void Seek(long frame)
    {
        PositionFrames = Math.Clamp(frame, 0, TotalFrames);
        _stream.Position = _dataOffset + PositionFrames * _blockAlign;
    }

    /// <summary>
    /// Emit exactly <paramref name="frames"/> frames as one DataAvailable, mimicking a WASAPI
    /// shared-mode buffer. Returns false at end of file. Caller (the rig) supplies the frame count so
    /// every source advances by the same amount on the same tick.
    /// </summary>
    internal bool Pump(int frames)
    {
        if (!_running) return true;
        int want = frames * _blockAlign;
        if (_buffer.Length < want) _buffer = new byte[want];

        int got = 0;
        while (got < want)
        {
            int n = _stream.Read(_buffer, got, want - got);
            if (n <= 0) break;
            got += n;
        }
        if (got <= 0) return false;

        // A short read at EOF is zero-padded rather than emitted ragged: a partial buffer would be a
        // buffer size the real capture path never produces.
        if (got < want) Array.Clear(_buffer, got, want - got);

        PositionFrames += frames;
        DataAvailable?.Invoke(this, new WaveInEventArgs(_buffer, want));
        return got >= want;
    }

    public void StartRecording() => _running = true;

    public void StopRecording()
    {
        if (!_running) return;
        _running = false;
        RecordingStopped?.Invoke(this, new StoppedEventArgs());
    }

    public void Dispose()
    {
        _running = false;
        _stream.Dispose();
    }
}

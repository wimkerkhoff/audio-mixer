namespace AudioMixer.Audio;

public sealed class DelayLine
{
    private readonly float[] _buffer;
    private int _writeIndex;
    private int _delaySamples;

    public DelayLine(int maxDelaySamples)
    {
        if (maxDelaySamples < 1) maxDelaySamples = 1;
        _buffer = new float[maxDelaySamples];
    }

    public int MaxDelaySamples => _buffer.Length;

    public int DelaySamples
    {
        get => _delaySamples;
        set
        {
            int v = Math.Clamp(value, 0, _buffer.Length - 1);
            Volatile.Write(ref _delaySamples, v);
        }
    }

    public void ProcessInPlace(float[] samples, int count)
    {
        int delay = Volatile.Read(ref _delaySamples);
        if (delay <= 0)
        {
            for (int i = 0; i < count; i++)
            {
                _buffer[_writeIndex] = samples[i];
                _writeIndex++;
                if (_writeIndex >= _buffer.Length) _writeIndex = 0;
            }
            return;
        }

        int bufLen = _buffer.Length;
        for (int i = 0; i < count; i++)
        {
            int readIndex = _writeIndex - delay;
            if (readIndex < 0) readIndex += bufLen;
            float delayed = _buffer[readIndex];
            _buffer[_writeIndex] = samples[i];
            samples[i] = delayed;
            _writeIndex++;
            if (_writeIndex >= bufLen) _writeIndex = 0;
        }
    }

    public void Reset()
    {
        Array.Clear(_buffer);
        _writeIndex = 0;
    }
}

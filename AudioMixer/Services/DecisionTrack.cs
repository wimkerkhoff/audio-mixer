using System.Globalization;
using System.IO;
using System.Text;

namespace AudioMixer.Services;

/// <summary>
/// What the automixer was doing, sampled alongside a recording and sharing its timestamp.
///
/// The recordings exist to answer three questions afterwards: did the selector pick the best mic, did
/// the leveler behave, was anything clipping. The audio alone answers the last one. It cannot answer
/// the first two, because the diag WAVs are tapped BEFORE the automix gain and before the bus — so
/// they show what each mic heard and nothing about what was done with it. Listening to the mix tells
/// you a choice was wrong but not what the alternative sounded like at that instant.
///
/// This closes that: one row per sample period with the leader on each bus, and each mic's level and
/// applied gain. Line it up against diag-input*.wav of the same stamp and "should it have picked mic
/// 3 at 12:04" becomes a question with an answer.
///
/// 10 Hz on purpose. The automixer ticks at 100 Hz and its hold is 200 ms, so 100 ms sampling cannot
/// miss a hand-off, and an hour costs about 2 MB against ~5 GB of audio.
/// </summary>
public sealed class DecisionTrack : IDisposable
{
    public const int SampleHz = 10;

    private readonly StreamWriter? _writer;
    private readonly int _inputs;
    private readonly int _outputs;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private long _lastWriteMs = -1000;

    public string? Path { get; }

    public DecisionTrack(string path, IReadOnlyList<string> inputNames, IReadOnlyList<string> outputNames)
    {
        _inputs = inputNames.Count;
        _outputs = outputNames.Count;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            _writer = new StreamWriter(path, append: false, Encoding.UTF8);
            Path = path;

            var header = new StringBuilder("ms");
            // The automix mode per bus, because winner = -1 has three causes (mode Off, the priority
            // duck, a silent room) and nothing else in the row separates the first from the third.
            // This column used to hold the scene, which was a CLAIM about the mode -- and a hand edit
            // made it a stale one. The mode itself cannot go stale.
            foreach (var o in outputNames) header.Append(",mode_").Append(Safe(o));
            foreach (var o in outputNames) header.Append(",winner_").Append(Safe(o));
            // Without this you can hear that the mix was levelled but not by how much, or whether the
            // leveler was working at all — which is half of "did the leveler behave".
            foreach (var o in outputNames) header.Append(",leveler_").Append(Safe(o));
            for (int i = 0; i < _inputs; i++)
            {
                var n = Safe(inputNames[i]);
                header.Append(",level_").Append(n);
                foreach (var o in outputNames) header.Append(",gain_").Append(n).Append('_').Append(Safe(o));
            }
            _writer.WriteLine(header.ToString());
        }
        catch (Exception ex)
        {
            // A decision track that throws would take down the recording it describes.
            Audio.AudioLog.Write($"Decision track unavailable: {ex.GetType().Name}: {ex.Message}");
            _writer = null;
        }
    }

    private static string Safe(string s)
    {
        var clean = new string(s.Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-').ToArray());
        return clean.Length == 0 ? "x" : clean;
    }

    /// <summary>
    /// Called from the meter tick. <paramref name="levelDb"/> is the post-fader level the selector
    /// actually compares, and <paramref name="gain"/> the automix gain applied for (input, output).
    /// </summary>
    public void Sample(
        Func<int, int> winner, Func<int, double> levelDb, Func<int, int, float> gain,
        Func<int, float> levelerGainDb, Func<int, string> mode)
    {
        if (_writer == null) return;
        long now = _clock.ElapsedMilliseconds;
        if (now - _lastWriteMs < 1000 / SampleHz) return;
        _lastWriteMs = now;

        var row = new StringBuilder();
        row.Append(now);
        for (int o = 0; o < _outputs; o++) row.Append(',').Append(Safe(mode(o)));
        for (int o = 0; o < _outputs; o++) row.Append(',').Append(winner(o));
        for (int o = 0; o < _outputs; o++)
            row.Append(',').Append(levelerGainDb(o).ToString("F1", CultureInfo.InvariantCulture));
        for (int i = 0; i < _inputs; i++)
        {
            row.Append(',').Append(levelDb(i).ToString("F1", CultureInfo.InvariantCulture));
            for (int o = 0; o < _outputs; o++)
                row.Append(',').Append(gain(i, o).ToString("F2", CultureInfo.InvariantCulture));
        }
        try { _writer.WriteLine(row.ToString()); } catch { }
    }

    public void Dispose()
    {
        try { _writer?.Flush(); _writer?.Dispose(); } catch { }
    }
}

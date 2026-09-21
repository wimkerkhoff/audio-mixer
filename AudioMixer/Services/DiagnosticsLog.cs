using AudioMixer.Audio;
using AudioMixer.ViewModels;

namespace AudioMixer.Services;

// The opt-in AudioLog side of the meter tick: talker hand-offs as they happen, plus a ~1 Hz dump of
// per-output and per-input health. Everything short-circuits when file logging is off, so a normal
// run doesn't build these strings 30x/second.
public sealed class DiagnosticsLog
{
    private readonly AudioEngine _engine;
    private readonly IReadOnlyList<ChannelViewModel> _channels;
    private readonly IReadOnlyList<OutputViewModel> _outputs;

    private long _lastLogTick = Environment.TickCount64;
    private readonly long[] _lastTotalSamples;
    private readonly int[] _lastAutoMixWinner;

    public DiagnosticsLog(
        AudioEngine engine, IReadOnlyList<ChannelViewModel> channels, IReadOnlyList<OutputViewModel> outputs)
    {
        _engine = engine;
        _channels = channels;
        _outputs = outputs;
        _lastTotalSamples = new long[outputs.Count];
        _lastAutoMixWinner = new int[outputs.Count];
        for (int o = 0; o < _lastAutoMixWinner.Length; o++) _lastAutoMixWinner[o] = -1;
    }

    public void Tick()
    {
        if (!AudioLog.Enabled) return;
        LogSelectionChanges();
        MaybeLogPeriodic();
    }

    // Not throttled — polled per meter tick so the trail captures fast switches.
    private void LogSelectionChanges()
    {
        for (int o = 0; o < _outputs.Count; o++)
        {
            int winner = _engine.AutoMixActiveInput(o);
            if (winner == _lastAutoMixWinner[o]) continue;
            int prev = _lastAutoMixWinner[o];
            _lastAutoMixWinner[o] = winner;
            AudioLog.Write(
                $"Output {OutputViewModel.Tag(o)} auto-mix: {Name(prev)} → {Name(winner)}");
        }

        string Name(int i) => i < 0 ? "none"
            : i < _channels.Count ? $"mic{i + 1} ('{_channels[i].CustomLabel}')" : $"mic{i + 1}";
    }

    private void MaybeLogPeriodic()
    {
        long now = Environment.TickCount64;
        long elapsedMs = now - _lastLogTick;
        if (elapsedMs <= 1000) return;
        _lastLogTick = now;

        for (int o = 0; o < _outputs.Count; o++)
        {
            var bus = _engine.Outputs[o];
            long total = bus.TotalSamplesRead;
            long delta = total - _lastTotalSamples[o];
            // A bus restart (input-count change) resets TotalSamplesRead, so a stale
            // baseline yields a bogus negative delta — count from restart instead.
            if (delta < 0) delta = total;
            _lastTotalSamples[o] = total;
            long samplesPerSec = delta * 1000 / elapsedMs;
            AudioLog.Write(
                $"Output {o}: playing={bus.IsPlaying} samplesPerSec={samplesPerSec} peakDb={_outputs[o].OutputPeakDb:F1} winner={_engine.AutoMixActiveInput(o)}");
        }

        for (int i = 0; i < _channels.Count; i++)
        {
            var ch = _channels[i];
            var dev = ch.SelectedDevice;
            if (dev == null) continue;
            var input = _engine.Inputs[i];
            string PerOutput(Func<int, string> value) =>
                string.Join(",", Enumerable.Range(0, _outputs.Count).Select(value));
            var bufMs = PerOutput(o => input.BufferedMs(o).ToString());
            var readSamples = PerOutput(o => input.ReadSamplesForOutput(o).ToString());
            var readCalls = PerOutput(o => input.ReadCallsForOutput(o).ToString());
            var gains = PerOutput(o => input.GetAutoMixGain(o).ToString("F2"));
            // Cumulative count of reads where the bus wanted more than the feed buffer held. The
            // buffer pads the shortfall with zeros, so an underrun is a silent hole that no meter and
            // no peak value shows — a rising count here is the only way to see the monitor feed
            // glitching, and the two outputs can differ wildly from the same input (the render clocks
            // are independent).
            var underruns = PerOutput(o => input.UnderrunsForOutput(o).ToString());
            // RF-link health (offline dongle-link diagnosis): high drops/silent while voiced is high =
            // wireless dropouts; elevated fluxCv corroborates. Only meaningful while the mic is voiced.
            var rf = input.SnapshotRfStats();
            // Gain calibration: the median of this mic's voiced buffers is the level every absolute-RMS
            // threshold in AutoMixer is fitted against, so a capture's own log has to carry the level it
            // was taken at — otherwise a retune from the fixture is guessing at the scale.
            var cal = input.SnapshotCalibration();
            string calSpeech = float.IsNaN(cal.SpeechDb) ? "-" : cal.SpeechDb.ToString("F0");
            string calFloor = float.IsNaN(cal.FloorDb) ? "-" : cal.FloorDb.ToString("F0");
            AudioLog.Write(
                $"Input {i} ('{dev.FriendlyName}'): inputDb={ch.InputPeakDb:F1} postDb={ch.PostPeakDb:F1} routes=[{string.Join(",", ch.Routes.Select(r => r.IsOn ? "1" : "0"))}] mute={ch.Muted} gains=[{gains}] fluxCv={input.CurrentFluxCv:F2} rf=[lvl={rf.MeanDb:F1} voiced={rf.VoicedPct:F0}% silent={rf.SilentPct:F0}% drops={rf.DropEdges}] cal=[speech={calSpeech} floor={calFloor} n={cal.TotalBuffers}] bufMs=[{bufMs}] under=[{underruns}] readCalls=[{readCalls}] readSamples=[{readSamples}]");
        }
    }
}

using System.Text.Json;
using AudioMixer.Audio;
using AudioMixer.ViewModels;

namespace AudioMixer.Services;

// Builds the full live JSON snapshot served by StateServer at /state. Deliberately exposes the
// selector's *reasoning* (env / crest / refCorr / fluxCv / winner / reference) and not just the
// visible mixer state — this is the fastest way to see why the automixer picked a mic without a GUI.
public static class StateSnapshot
{
    // Callers must already be on the UI thread (StateServer runs on its own).
    public static string Build(
        AudioEngine engine, IReadOnlyList<ChannelViewModel> channels,
        IReadOnlyList<OutputViewModel> outputs, int inputCount, string status,
        string? scene = null, IReadOnlyList<HealthAlert>? alertList = null)
    {
        var alerts = alertList ?? Array.Empty<HealthAlert>();
        static double ToDb(double lin) => lin <= 1e-6 ? -120.0 : Math.Round(20 * Math.Log10(lin), 1);
        var diag = engine.AutoMixSnapshot();

        var channelJson = new List<object>(channels.Count);
        for (int i = 0; i < channels.Count; i++)
        {
            var ch = channels[i];
            var input = engine.Inputs[i];
            var gains = new double[AudioEngine.OutputCount];
            for (int o = 0; o < AudioEngine.OutputCount; o++) gains[o] = Math.Round(input.GetAutoMixGain(o), 3);
            channelJson.Add(new
            {
                index = i,
                label = ch.CustomLabel,
                device = ch.SelectedDevice?.FriendlyName,
                inputDb = Math.Round(ch.InputPeakDb, 1),
                postDb = Math.Round(ch.PostPeakDb, 1),
                rmsDb = ToDb(input.CurrentLevelLinear),
                envDb = i < diag.Env.Length ? ToDb(diag.Env[i]) : (double?)null,
                crest = i < diag.Crest.Length ? Math.Round(diag.Crest[i], 2) : (double?)null,
                refCorr = i < diag.Corr.Length ? Math.Round(diag.Corr[i], 3) : (double?)null,
                fluxCv = i < diag.Cv.Length ? Math.Round(diag.Cv[i], 3) : (double?)null,
                clarity = ch.HasClarity ? Math.Round(ch.ClarityBar, 2) : (double?)null,
                routes = ch.Routes.Select(r => r.IsOn).ToArray(),
                muted = ch.Muted,
                volumePercent = Math.Round(ch.VolumePercent, 0),
                delayMs = ch.DelayMs,
                isPriority = ch.IsPriority,
                isDucking = input.IsDucking,
                isAutoMixActive = input.IsAutoMixActive,
                automixGain = gains,
            });
        }

        var outputJson = new List<object>(outputs.Count);
        for (int o = 0; o < outputs.Count; o++)
        {
            var op = outputs[o];
            outputJson.Add(new
            {
                index = o,
                label = op.CustomLabel,
                device = op.SelectedDevice?.FriendlyName,
                peakDb = Math.Round(op.OutputPeakDb, 1),
                volumePercent = Math.Round(op.VolumePercent, 0),
                recording = op.IsRecording,
                mode = o < diag.Mode.Length ? diag.Mode[o].ToString() : "Off",
                strengthPercent = Math.Round(op.StrengthPercent, 0),
                stableHandoff = op.StableHandoff,
                referenceGuided = op.ReferenceGuided,
                preferNatural = op.PreferNatural,
                winner = o < diag.Winner.Length ? diag.Winner[o] : -1,
                winnerHold = o < diag.WinnerHold.Length ? diag.WinnerHold[o] : 0,
                activeInput = o < diag.ActiveInput.Length ? diag.ActiveInput[o] : -1,
            });
        }

        // In replay the wall clock is meaningless — a golden baseline has to be keyed to the position
        // in the recording, otherwise two runs of the same fixture can't be compared line for line.
        var rig = engine.Replay;
        var root = new
        {
            ts = DateTime.Now.ToString("HH:mm:ss.fff"),
            inputCount,
            status,
            referenceInput = diag.ReferenceInput,
            scene,
            alerts = alerts.Select(a => new { a.Id, severity = a.Severity.ToString(), a.Message }).ToArray(),
            replay = rig == null ? null : new
            {
                stamp = rig.Stamp,
                positionSec = Math.Round(rig.Position.TotalSeconds, 2),
                durationSec = Math.Round(rig.Duration.TotalSeconds, 2),
                speed = rig.Speed,
                paused = rig.Paused,
            },
            channels = channelJson,
            outputs = outputJson,
        };
        return JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true });
    }
}

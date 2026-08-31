using AudioMixer.Audio;

namespace AudioMixer.ViewModels;

/// <summary>
/// One mic's row in the "why this mic?" table. Ranked by the metric the output is actually deciding
/// on, so the answer to "why isn't #2 winning" is readable without cross-referencing the /state JSON.
/// </summary>
public sealed class DiagnosticRow
{
    public required string Rank { get; init; }
    public required string Label { get; init; }
    public required string EnvDb { get; init; }
    public required string FluxCv { get; init; }
    public required string Corr { get; init; }
    public required string GainA { get; init; }
    public required string GainB { get; init; }
    public required string State { get; init; }

    /// <summary>Median level of this mic's voiced buffers — the number transmitter gain is set against.</summary>
    public required string Speech { get; init; }
    /// <summary>Median level of its non-voiced buffers: the room floor this mic actually hears.</summary>
    public required string Floor { get; init; }

    /// <summary>Winner rows are tinted so the current selection is findable at a glance.</summary>
    public string RowBackground { get; init; } = "#22222A";
    public string StateBrush { get; init; } = "#8A8A94";

    /// <summary>Green inside the calibration window, amber outside — makes the target hittable at a glance.</summary>
    public string SpeechBrush { get; init; } = "#8A8A94";

    // The level every absolute-RMS threshold in AutoMixer was fitted at (the Ankers' -24.6 dBFS p50),
    // and the tolerance the operator has to land inside. See RODE-PRO-RIG.md "Levelling".
    public const double SpeechTargetDb = -24.0;
    public const double SpeechToleranceDb = 3.0;

    public static DiagnosticRow Build(
        int index, ChannelViewModel ch, AutoMixDiag diag, InputChannel input, int outputCount, int rank)
    {
        static string Db(double lin) => lin <= 1e-6 ? "  -inf" : $"{20 * Math.Log10(lin),6:F1}";

        bool winner = diag.Winner.Contains(index);
        bool ducking = input.IsDucking;
        bool routed = ch.Routes.Any(r => r.IsOn);

        string state =
            !ch.HasDevice ? "no device" :
            ch.Muted ? "muted" :
            !routed ? "not routed" :
            ch.IsPriority ? "priority" :
            winner ? "WINNER" :
            ducking ? "ducked" : "open";

        string brush =
            state == "WINNER" ? "#3FB950" :
            state == "ducked" ? "#F2A93B" :
            state is "no device" or "muted" or "not routed" ? "#6A6A74" : "#B8B8C2";

        var cal = input.SnapshotCalibration();
        bool onTarget = !float.IsNaN(cal.SpeechDb)
            && Math.Abs(cal.SpeechDb - SpeechTargetDb) <= SpeechToleranceDb;

        return new DiagnosticRow
        {
            Speech = float.IsNaN(cal.SpeechDb) ? "     —" : $"{cal.SpeechDb,6:F0}",
            Floor = float.IsNaN(cal.FloorDb) ? "     —" : $"{cal.FloorDb,6:F0}",
            SpeechBrush = float.IsNaN(cal.SpeechDb) ? "#6A6A74" : onTarget ? "#3FB950" : "#F2A93B",
            Rank = routed && ch.HasDevice && !ch.Muted ? rank.ToString() : "—",
            Label = string.IsNullOrWhiteSpace(ch.CustomLabel) ? $"in{index + 1}" : ch.CustomLabel,
            EnvDb = index < diag.Env.Length ? Db(diag.Env[index]) : "     —",
            FluxCv = index < diag.Cv.Length && diag.Cv[index] > 0 ? $"{diag.Cv[index]:F3}" : "—",
            Corr = index < diag.Corr.Length && diag.Corr[index] != 0 ? $"{diag.Corr[index]:F3}" : "—",
            GainA = $"{input.GetAutoMixGain(0):F2}",
            GainB = outputCount > 1 ? $"{input.GetAutoMixGain(1):F2}" : "—",
            State = state,
            RowBackground = winner ? "#1E3324" : "#22222A",
            StateBrush = brush,
        };
    }
}

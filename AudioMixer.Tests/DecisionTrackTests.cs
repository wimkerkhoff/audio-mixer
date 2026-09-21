using System.IO;
using AudioMixer.Services;

namespace AudioMixer.Tests;

/// <summary>
/// The decision track is the file that makes a recording answerable. The diag WAVs are tapped before
/// the automix gain, so audio alone can never say which mic was chosen or what the alternative sounded
/// like at that instant — the session-review skill's whole step 4 reads this CSV. A malformed header or
/// a column that does not line up with its row makes the session unanalysable, and nobody finds out
/// until they sit down to review a service that already happened.
/// </summary>
public class DecisionTrackTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "AudioMixerTrack", Guid.NewGuid().ToString("N"));

    public DecisionTrackTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Path_(string name) => System.IO.Path.Combine(_dir, name);

    private static readonly string[] Mics = { "Lapel", "Rode L", "Rode R" };
    private static readonly string[] Buses = { "A", "B" };

    private static void Sample(DecisionTrack t, int winner = 0, float gain = 1f) =>
        t.Sample(_ => winner, i => -24.0 - i, (_, _) => gain, _ => 0f, "Prayer");

    private static string[] Lines(string path) =>
        File.ReadAllLines(path).Where(l => l.Length > 0).ToArray();

    [Fact]
    public void TheHeaderNamesEveryBusAndEveryMic()
    {
        var path = Path_("decisions.csv");
        using (var t = new DecisionTrack(path, Mics, Buses)) { }

        var header = Lines(path)[0];

        Assert.StartsWith("ms,scene,", header);
        Assert.Contains("winner_A", header);
        Assert.Contains("winner_B", header);
        Assert.Contains("leveler_A", header);
        Assert.Contains("level_Lapel", header);
        Assert.Contains("gain_Lapel_A", header);
        Assert.Contains("gain_RodeR_B", header);
    }

    /// <summary>
    /// Header and row must have identical arity or every column after the mismatch is misread — and
    /// silently, since the values are all plausible numbers.
    /// </summary>
    [Fact]
    public void EveryRowHasExactlyAsManyColumnsAsTheHeader()
    {
        var path = Path_("decisions.csv");
        using (var t = new DecisionTrack(path, Mics, Buses)) Sample(t);

        var lines = Lines(path);
        int expected = 2 + Buses.Length * 2 + Mics.Length * (1 + Buses.Length);   // 15

        Assert.Equal(2, lines.Length);
        Assert.Equal(expected, lines[0].Split(',').Length);
        Assert.Equal(expected, lines[1].Split(',').Length);
    }

    /// <summary>A name with a comma in it would otherwise shift every column to its right.</summary>
    [Fact]
    public void NamesAreSanitisedSoTheyCannotBreakTheColumns()
    {
        var path = Path_("decisions.csv");
        using (var t = new DecisionTrack(path, new[] { "Steve, lapel (L)" }, new[] { "OBS/Zoom" })) { }

        var header = Lines(path)[0];

        Assert.DoesNotContain("Steve,", header);
        Assert.Contains("level_Stevelapel", header);
        Assert.Contains("winner_OBSZoom", header);
        Assert.Equal(2 + 1 + 1 + 2, header.Split(',').Length);
    }

    /// <summary>A name of nothing but punctuation must still produce a usable column name.</summary>
    [Fact]
    public void ANameWithNoUsableCharactersStillYieldsAColumn()
    {
        var path = Path_("decisions.csv");
        using (var t = new DecisionTrack(path, new[] { "—" }, new[] { "!!" })) { }

        var header = Lines(path)[0];

        Assert.Contains("level_x", header);
        Assert.Contains("winner_x", header);
    }

    /// <summary>
    /// Sampled from the 30 Hz meter tick but throttled to 10 Hz — the automixer's hold is 200 ms, so
    /// 100 ms cannot miss a hand-off, and it keeps an hour at ~2 MB against ~5 GB of audio.
    ///
    /// Asserts only the property that is actually deterministic: a burst of calls within one window
    /// writes ONE row. The earlier version slept 140 ms and asserted a row count, which is a race on
    /// a loaded CI runner — and CI now exists, so it would have started flaking.
    /// </summary>
    [Fact]
    public void ABurstOfMeterTicksWritesASingleRow()
    {
        var path = Path_("decisions.csv");
        using (var t = new DecisionTrack(path, Mics, Buses))
        {
            for (int i = 0; i < 30; i++) Sample(t);      // a second's worth of meter ticks, instantly
        }

        Assert.Equal(2, Lines(path).Length);             // header + exactly one row
    }

    /// <summary>The throttle is a real interval, not "once ever": once the window has passed another
    /// row is written. Generous bounds on purpose — this is the half that touches the clock.</summary>
    [Fact]
    public async Task AfterTheWindowHasPassedSamplingResumes()
    {
        var path = Path_("decisions.csv");
        using (var t = new DecisionTrack(path, Mics, Buses))
        {
            Sample(t);
            await Task.Delay(1000 / DecisionTrack.SampleHz + 60);
            Sample(t);
        }

        Assert.Equal(3, Lines(path).Length);             // header + two rows
    }

    [Fact]
    public void TheWinnerIsWrittenAsTheRawIndexSoMinusOneSurvives()
    {
        var path = Path_("decisions.csv");
        using (var t = new DecisionTrack(path, Mics, Buses)) Sample(t, winner: -1, gain: 1f);

        var cells = Lines(path)[1].Split(',');

        Assert.Equal("-1", cells[2]);
        Assert.Equal("-1", cells[3]);
        Assert.Equal("1.00", cells[^1]);   // silent-room writes unity; a priority duck writes 0.00
    }

    /// <summary>
    /// It describes a recording, so it must never be able to take one down. An unopenable path is
    /// logged and the track goes inert rather than throwing into the meter tick.
    /// </summary>
    [Fact]
    public void AnUnwritablePathDisablesTheTrackInsteadOfThrowing()
    {
        var bad = Path_("nope\0bad.csv");

        using var t = new DecisionTrack(bad, Mics, Buses);
        Sample(t);

        Assert.Null(t.Path);
    }

    [Fact]
    public void DisposingTwiceIsHarmless()
    {
        var path = Path_("decisions.csv");
        var t = new DecisionTrack(path, Mics, Buses);
        Sample(t);
        t.Dispose();
        t.Dispose();

        Assert.Equal(2, Lines(path).Length);
    }
}

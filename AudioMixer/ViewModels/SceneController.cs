using AudioMixer.Audio;
using AudioMixer.Models;
using AudioMixer.Services;

namespace AudioMixer.ViewModels;

/// <summary>
/// Applies <see cref="SceneTransform"/> (pure rules) to the live view models, and reads the current
/// configuration back out as a plan. The split matters: all the judgement lives in the pure function
/// where it is unit-tested, and this class only marshals values, so a scene bug is a test failure
/// rather than something you discover mid-service.
/// </summary>
public sealed class SceneController : ViewModelBase
{
    private readonly IReadOnlyList<ChannelViewModel> _channels;
    private readonly IReadOnlyList<OutputViewModel> _outputs;

    public SceneController(IReadOnlyList<ChannelViewModel> channels, IReadOnlyList<OutputViewModel> outputs)
    {
        _channels = channels;
        _outputs = outputs;
    }

    public event Action<Scene>? SceneApplied;

    private Scene? _current;
    /// <summary>Null until a scene is applied — the mixer may be in a hand-configured state.</summary>
    public Scene? Current
    {
        get => _current;
        private set
        {
            if (SetField(ref _current, value)) RaiseAll();
        }
    }

    private VoiceSource _voiceSource = VoiceSource.Auto;
    public VoiceSource VoiceSource
    {
        get => _voiceSource;
        set
        {
            if (!SetField(ref _voiceSource, value)) return;
            RaiseAll();
            // The override is only meaningful inside a scene, and changing it must take effect at once
            // — an operator flipping to "Lapel" mid-sentence expects the lapel, not a pending change.
            if (_current is { } s && SupportsVoiceSource(s)) Apply(s);
        }
    }

    public static bool SupportsVoiceSource(Scene scene) => scene is Scene.Teaching or Scene.Singing;

    public bool ShowVoiceSource => _current is { } s && SupportsVoiceSource(s);

    public bool IsStandby => _current == Scene.Standby;
    public bool IsTeaching => _current == Scene.Teaching;
    public bool IsPrayer => _current == Scene.Prayer;
    public bool IsSinging => _current == Scene.Singing;

    public bool SourceIsLapel => _voiceSource == VoiceSource.Lapel;
    public bool SourceIsRoom => _voiceSource == VoiceSource.RoomMics;

    // "on"/"off" strings rather than bools: WPF trigger values are parsed as strings, and comparing
    // one against a boolean binding is unreliable enough that every button quietly renders unselected.
    public string StandbyState => IsStandby ? "on" : "off";
    public string TeachingState => IsTeaching ? "on" : "off";
    public string PrayerState => IsPrayer ? "on" : "off";
    public string SingingState => IsSinging ? "on" : "off";
    public string LapelState => SourceIsLapel ? "on" : "off";
    public string RoomState => SourceIsRoom ? "on" : "off";

    public string CurrentDescription =>
        _current is { } s ? SceneTransform.Describe(s, _voiceSource) : "No scene applied — mixer is hand-configured.";

    public string CurrentName => _current?.ToString() ?? "Custom";

    /// <summary>True when at least one channel is marked as the lapel, so the override is useful.</summary>
    public bool HasLapel => _channels.Any(c => c.Role == ChannelRole.Lapel);

    /// <summary>
    /// True while <see cref="Apply"/> is writing. The scene writes the very properties that otherwise
    /// mean "the operator hand-edited something", so without this guard applying a scene would
    /// immediately mark itself customised.
    /// </summary>
    public bool IsApplying { get; private set; }

    public void Apply(Scene scene)
    {
        IsApplying = true;
        try
        {
            var plan = SceneTransform.Apply(scene, _voiceSource, Read());
            Write(plan);
        }
        finally
        {
            IsApplying = false;
        }
        Current = scene;
        SceneApplied?.Invoke(scene);
    }

    /// <summary>Snapshot the live view models as a plan for the pure transform to work on.</summary>
    public MixerPlan Read()
    {
        var channels = _channels.Select((c, i) => new ChannelPlan(
            i, c.Role, c.Routes.Select(r => r.IsOn).ToArray(), c.Muted, c.IsPriority)).ToArray();

        var outputs = _outputs.Select((o, i) => new OutputPlan(
            i,
            (AutoMixMode)o.AutoMixModeIndex,
            o.PreferNatural,
            o.ReferenceGuided,
            o.StableHandoff,
            o.Muted)).ToArray();

        return new MixerPlan(channels, outputs);
    }

    private void Write(MixerPlan plan)
    {
        foreach (var cp in plan.Channels)
        {
            if (cp.Index >= _channels.Count) continue;
            var ch = _channels[cp.Index];
            ch.Muted = cp.Muted;
            ch.IsPriority = cp.IsPriority;
            for (int r = 0; r < ch.Routes.Length && r < cp.Routes.Length; r++) ch.Routes[r].IsOn = cp.Routes[r];
        }

        foreach (var op in plan.Outputs)
        {
            if (op.Index >= _outputs.Count) continue;
            var o = _outputs[op.Index];
            o.PreferNatural = op.PreferNatural;
            o.ReferenceGuided = op.ReferenceGuided;
            o.StableHandoff = op.StableHandoff;
            o.AutoMixModeIndex = (int)op.Mode;
            o.Muted = op.Muted;
        }
    }

    /// <summary>
    /// Clears the scene when the operator hand-edits something in Advanced, so the Simple-mode pill
    /// never claims a scene is active while the mixer has been changed out from under it.
    /// </summary>
    public void MarkCustomised()
    {
        if (_current != null) Current = null;
    }

    private void RaiseAll()
    {
        RaisePropertyChanged(nameof(IsStandby));
        RaisePropertyChanged(nameof(IsTeaching));
        RaisePropertyChanged(nameof(IsPrayer));
        RaisePropertyChanged(nameof(IsSinging));
        RaisePropertyChanged(nameof(ShowVoiceSource));
        RaisePropertyChanged(nameof(SourceIsLapel));
        RaisePropertyChanged(nameof(SourceIsRoom));
        RaisePropertyChanged(nameof(CurrentDescription));
        RaisePropertyChanged(nameof(CurrentName));
        RaisePropertyChanged(nameof(HasLapel));
        RaisePropertyChanged(nameof(StandbyState));
        RaisePropertyChanged(nameof(TeachingState));
        RaisePropertyChanged(nameof(PrayerState));
        RaisePropertyChanged(nameof(SingingState));
        RaisePropertyChanged(nameof(LapelState));
        RaisePropertyChanged(nameof(RoomState));
    }
}

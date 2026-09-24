namespace AudioMixer.Models;

/// <summary>
/// Which strip is the presenter's lapel -- the durable half of "the priority mic". The operator
/// picks it (MainViewModel.LapelIndex), which sets this and IsPriority together, so the two agree
/// by construction; this is the one that is read back to show the choice.
/// </summary>
public enum ChannelRole
{
    Room,
    Lapel,
}

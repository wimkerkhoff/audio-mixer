namespace AudioMixer.Audio;

// Per-output automixer controls. A view-model takes this one dependency instead of a delegate per
// option — otherwise every new automix toggle costs a field, a constructor parameter and a lambda
// at the call site on top of the engine/mixer/preset changes it already needs.
public interface IAutoMixControl
{
    void SetAutoMixMode(int output, AutoMixMode mode);
    void SetAutoMixStrength(int output, float strength);
    void SetAutoMixStableHandoff(int output, bool on);
    void SetAutoMixReferenceGuided(int output, bool on);
    void SetAutoMixPreferNatural(int output, bool on);
}

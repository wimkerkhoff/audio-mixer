using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace AudioMixer.Services;

/// <summary>
/// Tells the app when an audio endpoint appears or disappears.
///
/// Without this nothing ever re-enumerates: `RefreshDevices` was reachable only from the toolbar
/// button, so unplugging a receiver left a strip holding a dead endpoint and plugging it back in did
/// nothing at all. The operator had to notice, guess, and remap by hand every time — and the
/// reattach-on-replug logic could never fire, because it only runs during a refresh that never
/// happened.
///
/// Windows fires several notifications per physical plug event (added, then a state change per
/// interface, then a default-device change), so the callback is DEBOUNCED rather than acted on
/// directly: re-enumerating five times in 200 ms would tear down and rebuild captures mid-plug.
///
/// Callbacks arrive on a system MTA thread. Nothing here touches the engine or the UI — it raises an
/// event and the view model marshals.
/// </summary>
public sealed class DeviceWatcher : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly Client _client;
    private readonly System.Threading.Timer _debounce;
    private bool _registered;

    /// <summary>Long enough to swallow one plug event's burst, short enough to feel immediate.</summary>
    public const int DebounceMs = 700;

    /// <summary>Raised on a threadpool thread after the notifications settle.</summary>
    public event Action? DevicesChanged;

    public DeviceWatcher()
    {
        _debounce = new System.Threading.Timer(_ => DevicesChanged?.Invoke());
        _client = new Client(this);
        try
        {
            _enumerator.RegisterEndpointNotificationCallback(_client);
            _registered = true;
        }
        catch (Exception ex)
        {
            // Not fatal: the operator can still hit Refresh devices. Worth a line, because silently
            // losing hot-plug detection looks exactly like the bug this class fixes.
            Audio.AudioLog.Write($"Device notifications unavailable: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// The COM notification callbacks keep arriving after Dispose — WASAPI does not unregister
    /// synchronously — so this races the timer's disposal and would throw ObjectDisposedException onto
    /// a COM thread, where nothing catches it.
    /// </summary>
    private void Bump()
    {
        try { _debounce.Change(DebounceMs, System.Threading.Timeout.Infinite); }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        if (_registered)
        {
            try { _enumerator.UnregisterEndpointNotificationCallback(_client); } catch { }
            _registered = false;
        }
        _debounce.Dispose();
        _enumerator.Dispose();
    }

    private sealed class Client : IMMNotificationClient
    {
        private readonly DeviceWatcher _owner;
        public Client(DeviceWatcher owner) => _owner = owner;

        public void OnDeviceAdded(string deviceId) => _owner.Bump();
        public void OnDeviceRemoved(string deviceId) => _owner.Bump();
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => _owner.Bump();

        // A default-device change is not an endpoint appearing, but Windows raises it as part of a
        // plug event and acting on it costs nothing once debounced.
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => _owner.Bump();

        // Fires constantly for volume and format properties; re-enumerating on those would rebuild the
        // device lists whenever anyone touched a slider.
        public void OnPropertyValueChanged(string deviceId, PropertyKey key) { }
    }
}

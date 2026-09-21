namespace AudioMixer.Services;

/// <summary>How durable an endpoint's identity is — the thing that decides whether a preset can
/// re-find it after a replug, or whether two identical units can be told apart at all.</summary>
public enum IdentityKind
{
    /// <summary>No container id to read.</summary>
    Unknown,

    /// <summary>Soldered in (PCI/HDAUDIO). Never moves, so its endpoint GUID is stable anyway.</summary>
    Fixed,

    /// <summary>A software endpoint (VB-CABLE, VoiceMeeter, NDI). Stable, and never a microphone.</summary>
    Virtual,

    /// <summary>
    /// The device carries a hardware serial, so its identity survives a port change AND distinguishes
    /// two units of the same model. This is the good case, and the one multiple receivers need.
    /// </summary>
    Serial,

    /// <summary>
    /// Identity is the USB port. A different port mints a new endpoint GUID, a new container id, and
    /// loses the user's rename with it — so a preset cannot follow the device, and two identical units
    /// are distinguishable only by which port each is in. The Soundsync dongles are this.
    /// </summary>
    PortDerived,
}

/// <summary>
/// Reads how stable a device's identity is, from the AUDIO api alone.
///
/// The PnP route (`tools/device-identity.ps1`) walks endpoint → parent → USB composite and reads the
/// last segment of the instance id: no '&' means a hardware serial, an '&' means port-derived. That
/// works but needs WMI/SetupAPI, and `PKEY_Device_InstanceId` is not exposed on an endpoint's property
/// store — it reads empty for every device here, USB included.
///
/// `PKEY_Device_ContainerId` IS exposed, and it turns out to carry the same answer. Windows generates
/// a container id one of two ways, and says which in the UUID's own version nibble:
///
///   • **version 5** (SHA-1, name-based) — derived deterministically from the device's hardware
///     identity, i.e. its serial. Same device, any port, same GUID.
///   • **version 1** (time-based) — minted once when the device was first seen on that port and
///     stored. A different port mints a different one.
///
/// Measured 2026-09-21 on live hardware, cross-checked against the PnP tool on four devices, which
/// agreed on all four: Lync USB Headset v1 / PORT-DERIVED, Jabra PanaCast v5 / SERIAL, RØDE Wireless
/// PRO RX v5 / SERIAL (`USB\VID_19F7&amp;PID_0058\801D150D`), onboard Realtek v5 / FIXED.
///
/// **Not yet confirmed by an actual replug** — the inference is from the generation scheme and the
/// agreement with the PnP walk, not from watching a container id survive moving the cable. Treat
/// <see cref="IdentityKind.Serial"/> as "should follow the device" until that test is done.
/// </summary>
public static class DeviceIdentity
{
    /// <summary>Position 12 of the hex digits (after the dashes are removed) is the UUID version.</summary>
    public static int Version(Guid container)
    {
        var hex = container.ToString("N");
        return Uri.IsHexDigit(hex[12]) ? Convert.ToInt32(hex[12].ToString(), 16) : 0;
    }

    /// <summary>
    /// Classifies an endpoint. <paramref name="bus"/> is PKEY_Device_EnumeratorName (USB, HDAUDIO,
    /// BTHENUM, ROOT/MMDEVAPI for virtual devices) and is checked first, because a fixed or virtual
    /// endpoint's identity is stable for reasons that have nothing to do with a serial.
    /// </summary>
    public static IdentityKind Classify(Guid? container, string? bus)
    {
        if (bus != null)
        {
            if (bus.Equals("HDAUDIO", StringComparison.OrdinalIgnoreCase)
             || bus.Equals("PCI", StringComparison.OrdinalIgnoreCase)) return IdentityKind.Fixed;
            if (bus.Equals("ROOT", StringComparison.OrdinalIgnoreCase)
             || bus.Equals("SWD", StringComparison.OrdinalIgnoreCase)
             || bus.Equals("MMDEVAPI", StringComparison.OrdinalIgnoreCase)) return IdentityKind.Virtual;
        }
        if (container is not Guid g || g == Guid.Empty) return IdentityKind.Unknown;

        return Version(g) switch
        {
            5 or 3 => IdentityKind.Serial,        // name-based: derived from the hardware's own identity
            1 or 4 => IdentityKind.PortDerived,   // time-based or random: minted for this port
            _ => IdentityKind.Unknown,
        };
    }

    /// <summary>
    /// The key a preset should store to re-find this device later, or null when the device has no
    /// identity worth storing. Only a <see cref="IdentityKind.Serial"/> device gets one: for anything
    /// else the container id is no more durable than the endpoint GUID already saved, and writing it
    /// would invite a match that looks authoritative and is not.
    /// </summary>
    public static string? StableKey(Guid? container, string? bus) =>
        Classify(container, bus) == IdentityKind.Serial && container is Guid g ? g.ToString("D") : null;

    /// <summary>Plain-language, for the Diagnostics devices table.</summary>
    public static string Describe(IdentityKind kind) => kind switch
    {
        IdentityKind.Serial => "serial — follows the device between ports",
        IdentityKind.PortDerived => "port — a different USB port looks like a new device",
        IdentityKind.Fixed => "built in",
        IdentityKind.Virtual => "virtual",
        _ => "unknown",
    };
}

# Does an audio endpoint keep its identity when it moves to another USB port?
#
# The WASAPI endpoint GUID is regenerated on every re-enumeration, so a preset re-binds by friendly
# name. That works until you own TWO IDENTICAL receivers: they share a friendly name, and matching by
# name then binds an arbitrary one -- with each receiver covering a different part of the room, that
# is the wrong mic in the wrong place (the lesson the Ankers taught: never greedy-fill).
#
# The answer is in the PnP tree, not in the audio API: walk endpoint -> parent interface -> the USB
# composite device, and read the LAST segment of its instance id.
#     ...\00277408030        no '&'  -> a real hardware SERIAL. Stable across ports and reboots,
#                                       and unique between two units of the same model.
#     ...\6&1ff22f3e&0&4     has '&' -> PORT-DERIVED. A different port mints a new one, taking the
#                                       endpoint GUID and any rename with it.
#
# Run it with every receiver plugged in. SERIAL on all of them means multiple receivers can be told
# apart permanently; PORT-DERIVED means they cannot, and each unit needs its own labelled port.

param([string]$Match = ".")

Get-PnpDevice -Class AudioEndpoint -Status OK -ErrorAction SilentlyContinue |
  Where-Object { $_.FriendlyName -match $Match } |
  Sort-Object FriendlyName |
  ForEach-Object {
    $parent = (Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName 'DEVPKEY_Device_Parent' -ErrorAction SilentlyContinue).Data
    $device = $null
    if ($parent) { $device = (Get-PnpDeviceProperty -InstanceId $parent -KeyName 'DEVPKEY_Device_Parent' -ErrorAction SilentlyContinue).Data }

    # Split on a literal backslash via its char code: -split takes a regex, where '\' is invalid.
    $tail = if ($device) { ($device.Split([char]92))[-1] } else { "" }
    $kind = if (-not $device)        { "unknown" }
            elseif ($device -like 'HTREE*' -or $device -like 'ROOT*') { "VIRTUAL" }
            elseif ($device -like 'HDAUDIO*' -or $device -like 'PCI*') { "FIXED" }
            elseif ($tail -match '&') { "PORT-DERIVED" }
            else                      { "SERIAL" }

    [pscustomobject]@{
      Endpoint = $_.FriendlyName
      Identity = $kind
      Key      = $tail
      Device   = $device
    }
  } | Format-Table -AutoSize -Wrap

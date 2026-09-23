using System;
using System.Runtime.InteropServices;

namespace FanControl.LianLi.Transport;

/// <summary>
/// Mirrors the native <c>DEVPROPKEY</c>: a property-set GUID plus an id within that set, naming one
/// property of a device node or device interface in the configuration manager.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly struct DevicePropertyKey {
    // DEVPKEY_Device_InstanceId {78c34fc8-104a-4aca-9ea4-524d52996e57} pid 256: the device-instance
    // string ("HID\VID_...\...") an interface path belongs to, the input to the device-node lookup.
    public static readonly DevicePropertyKey InstanceId =
        new DevicePropertyKey(new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"), 256);

    // DEVPKEY_Device_ContainerId {8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c} pid 2: the physical-device GUID.
    public static readonly DevicePropertyKey ContainerId =
        new DevicePropertyKey(new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 2);

    // Field order and types are the native layout: a 16-byte GUID, then a 32-bit id.
    private readonly Guid _formatId;
    private readonly uint _propertyId;

    public DevicePropertyKey(Guid formatId, uint propertyId) {
        _formatId = formatId;
        _propertyId = propertyId;
    }

    /// <summary>The property set the property belongs to.</summary>
    public Guid FormatId => _formatId;

    /// <summary>The property's id within its set.</summary>
    public uint PropertyId => _propertyId;
}

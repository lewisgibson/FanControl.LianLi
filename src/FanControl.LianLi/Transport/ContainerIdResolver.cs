using System;
using System.Text;
using System.Threading;

namespace FanControl.LianLi.Transport;

/// <summary>
/// Resolves the Windows <em>ContainerId</em> of a device-interface path. The ContainerId is the
/// GUID Windows assigns to one physical device: every HID interface a single controller exposes
/// shares it, and it differs across physical controllers. That makes it the reliable
/// de-duplication key for the Lian Li Uni family, whose units all report the same firmware-fixed USB
/// serial ("6243168001" on the SL-Infinity) - the serial cannot tell two interfaces of one
/// controller from two separate controllers, but the ContainerId can (see
/// <see cref="HidDeviceDeduplicator"/>).
/// </summary>
internal sealed class ContainerIdResolver {
    private const int CrSuccess = 0;

    // CR_BUFFER_SMALL: what a sizing call with no buffer returns alongside the size it needs.
    private const int CrBufferSmall = 26;

    // CM_LOCATE_DEVNODE_NORMAL: only a device node that is present.
    private const uint LocateDeviceNodeNormal = 0;

    // A ContainerId is a GUID, stored as its 16 raw bytes.
    private const int GuidLength = 16;

    private readonly IConfigurationManagerApi _configurationManager;

    public ContainerIdResolver(IConfigurationManagerApi configurationManager) {
        _configurationManager = configurationManager ?? throw new ArgumentNullException(nameof(configurationManager));
    }

    /// <summary>
    /// Resolve the ContainerId of the physical device behind an interface path, formatted as a
    /// lowercased "{guid}". Returns null when the path has no resolvable device node or the device
    /// reports no real container (the all-zero GUID), so the caller falls back to the per-interface
    /// device path - the safe direction that never collapses distinct devices. Runs inside the bounded
    /// device scan, so it checks <paramref name="token"/> before each configuration-manager call.
    /// </summary>
    public string? Resolve(string deviceInterfacePath, CancellationToken token) {
        if (deviceInterfacePath is null) {
            throw new ArgumentNullException(nameof(deviceInterfacePath));
        }

        string? instanceId = GetInterfaceInstanceId(deviceInterfacePath, token);
        if (instanceId is null || instanceId.Length == 0) {
            return null;
        }

        token.ThrowIfCancellationRequested();
        if (_configurationManager.LocateDeviceNode(out uint deviceInstance, instanceId, LocateDeviceNodeNormal) != CrSuccess) {
            return null;
        }

        byte[]? raw = GetDeviceNodeProperty(deviceInstance, DevicePropertyKey.ContainerId, token);
        if (raw is null || raw.Length != GuidLength) {
            return null;
        }

        var container = new Guid(raw);
        // A device with no real container reports the all-zero GUID; treat that as "unknown" so such
        // devices fall back to the device-path key instead of all collapsing onto one zero id.
        if (container == Guid.Empty) {
            return null;
        }

        return container.ToString("B").ToLowerInvariant();
    }

    private string? GetInterfaceInstanceId(string deviceInterfacePath, CancellationToken token) {
        // First call sizes the buffer: a null buffer makes it report the required byte count in size
        // and return CR_BUFFER_SMALL. Anything else (or a zero size) means there is nothing to read.
        uint size = 0;
        token.ThrowIfCancellationRequested();
        int sizing = _configurationManager.GetDeviceInterfaceProperty(
            deviceInterfacePath, DevicePropertyKey.InstanceId, null, ref size);
        if (!IsSized(sizing, size)) {
            return null;
        }

        var buffer = new byte[size];
        token.ThrowIfCancellationRequested();
        if (_configurationManager.GetDeviceInterfaceProperty(
                deviceInterfacePath, DevicePropertyKey.InstanceId, buffer, ref size) != CrSuccess) {
            return null;
        }

        // The property is a UTF-16 string with its terminator counted in the size.
        return Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(size, (uint)buffer.Length)).TrimEnd('\0');
    }

    private byte[]? GetDeviceNodeProperty(uint deviceInstance, DevicePropertyKey key, CancellationToken token) {
        uint size = 0;
        token.ThrowIfCancellationRequested();
        int sizing = _configurationManager.GetDeviceNodeProperty(deviceInstance, key, null, ref size);
        if (!IsSized(sizing, size)) {
            return null;
        }

        var buffer = new byte[size];
        token.ThrowIfCancellationRequested();
        if (_configurationManager.GetDeviceNodeProperty(deviceInstance, key, buffer, ref size) != CrSuccess) {
            return null;
        }

        return buffer;
    }

    // A sizing call succeeded when it reported a size, whether it said so with CR_BUFFER_SMALL (the
    // documented answer to a null buffer) or plain success.
    private static bool IsSized(int result, uint size)
        => (result == CrSuccess || result == CrBufferSmall) && size > 0;
}

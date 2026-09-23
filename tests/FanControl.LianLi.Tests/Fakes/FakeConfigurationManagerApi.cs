using System;
using System.Collections.Generic;
using System.Text;
using FanControl.LianLi.Transport;

namespace FanControl.LianLi.Tests.Fakes;

/// <summary>
/// A small in-memory configuration manager and registry: device instances, their nodes, the values in
/// each node's hardware key, the interfaces registered per class and instance, and the two properties
/// the plugin reads. Lengths and sizing follow the real functions (characters for the lists, bytes for
/// values and properties), and each call's result code can be overridden to reach a failure path.
/// Property keys are matched against the Windows SDK's literal DEVPKEY values, not the plugin's own
/// constants, so a wrong key reads as the missing property it would be on Windows. <see cref="OnCall"/>
/// runs with each member's name as it is entered, so a test can land a deadline inside any one call.
/// </summary>
internal sealed class FakeConfigurationManagerApi : IConfigurationManagerApi {
    public const int CrSuccess = 0;
    public const int CrNoSuchDeviceNode = 13;
    public const int CrFailure = 19;
    public const int CrBufferSmall = 26;
    public const int CrNoSuchValue = 37;
    public const int ErrorFileNotFound = 2;
    public const int ErrorMoreData = 234;

    private static readonly Guid InstanceIdFormat = new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57");
    private static readonly Guid ContainerIdFormat = new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c");

    private readonly Dictionary<IntPtr, uint> _openKeys = new Dictionary<IntPtr, uint>();

    public Action<string>? OnCall { get; set; }

    public List<string> DeviceIds { get; } = new List<string>();

    public int DeviceIdListSizeResult { get; set; } = CrSuccess;

    /// <summary>Results of the successive list fills; an empty queue fills successfully.</summary>
    public Queue<int> DeviceIdListResults { get; } = new Queue<int>();

    public List<string> DeviceIdListQueries { get; } = new List<string>();

    public Dictionary<string, uint> DeviceNodes { get; } = new Dictionary<string, uint>(StringComparer.Ordinal);

    public List<uint> LocateFlags { get; } = new List<uint>();

    /// <summary>The values in each device node's hardware key, as (registry type, raw characters).</summary>
    public Dictionary<uint, Dictionary<string, (int Type, char[] Data)>> HardwareKeys { get; } =
        new Dictionary<uint, Dictionary<string, (int Type, char[] Data)>>();

    public List<string> KeyOpens { get; } = new List<string>();

    public List<uint> ClosedKeys { get; } = new List<uint>();

    public int? QueryValueSizingResult { get; set; }

    public int? QueryValueReadResult { get; set; }

    /// <summary>Interface paths per (interface class, device instance); a null instance is the whole class.</summary>
    public Dictionary<(Guid, string?), string[]> Interfaces { get; } = new Dictionary<(Guid, string?), string[]>();

    public List<string> InterfaceListQueries { get; } = new List<string>();

    public int? InterfaceListSizeResult { get; set; }

    public Queue<int> InterfaceListResults { get; } = new Queue<int>();

    /// <summary>The device instance id each interface path belongs to.</summary>
    public Dictionary<string, string> InterfaceInstances { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

    public int InterfacePropertySizingResult { get; set; } = CrBufferSmall;

    public int InterfacePropertyReadResult { get; set; } = CrSuccess;

    /// <summary>The raw ContainerId property bytes of each device node.</summary>
    public Dictionary<uint, byte[]> ContainerIds { get; } = new Dictionary<uint, byte[]>();

    public int DeviceNodePropertySizingResult { get; set; } = CrBufferSmall;

    public int DeviceNodePropertyReadResult { get; set; } = CrSuccess;

    /// <summary>A length to report from the id-list sizing call in place of the real one.</summary>
    public uint? DeviceIdListLengthOverride { get; set; }

    /// <summary>A length to report from the interface-list sizing call in place of the real one.</summary>
    public uint? InterfaceListLengthOverride { get; set; }

    // A configuration-manager list: each entry null-terminated, then one more null. An empty list is
    // the lone terminator.
    public static char[] MultiString(params string[] entries)
        => entries.Length == 0 ? new[] { '\0' } : (string.Join("\0", entries) + "\0\0").ToCharArray();

    public int GetDeviceIdListSize(out uint length, string filter, uint flags) {
        OnCall?.Invoke("GetDeviceIdListSize");
        DeviceIdListQueries.Add(string.Format("{0} 0x{1:X8}", filter, flags));
        length = DeviceIdListLengthOverride ?? (uint)MultiString(DeviceIds.ToArray()).Length;
        return DeviceIdListSizeResult;
    }

    public int GetDeviceIdList(string filter, char[] buffer, uint bufferLength, uint flags) {
        OnCall?.Invoke("GetDeviceIdList");
        int result = DeviceIdListResults.Count > 0 ? DeviceIdListResults.Dequeue() : CrSuccess;
        if (result == CrSuccess) {
            char[] list = MultiString(DeviceIds.ToArray());
            Array.Copy(list, buffer, Math.Min(list.Length, (int)bufferLength));
        }

        return result;
    }

    public int LocateDeviceNode(out uint deviceInstance, string deviceId, uint flags) {
        OnCall?.Invoke("LocateDeviceNode");
        LocateFlags.Add(flags);
        return DeviceNodes.TryGetValue(deviceId, out deviceInstance) ? CrSuccess : CrNoSuchDeviceNode;
    }

    public int OpenDeviceNodeKey(
        uint deviceInstance, int samDesired, uint hardwareProfile, uint disposition, out IntPtr key, uint flags) {
        OnCall?.Invoke("OpenDeviceNodeKey");
        KeyOpens.Add(string.Format("{0} sam=0x{1:X} profile={2} disposition={3} flags={4}", deviceInstance, samDesired, hardwareProfile, disposition, flags));
        if (!HardwareKeys.ContainsKey(deviceInstance)) {
            key = IntPtr.Zero;
            return CrNoSuchValue;
        }

        key = new IntPtr(1000 + deviceInstance);
        _openKeys[key] = deviceInstance;
        return CrSuccess;
    }

    public int QueryValue(IntPtr key, string name, out int type, char[]? data, ref uint dataSize) {
        OnCall?.Invoke("QueryValue");
        Dictionary<string, (int Type, char[] Data)> values = HardwareKeys[_openKeys[key]];
        if (!values.TryGetValue(name, out (int Type, char[] Data) value)) {
            type = 0;
            return ErrorFileNotFound;
        }

        type = value.Type;
        uint bytes = (uint)(value.Data.Length * sizeof(char));
        if (data is null) {
            dataSize = bytes;
            return QueryValueSizingResult ?? 0;
        }

        if (QueryValueReadResult is int failure) {
            return failure;
        }

        Array.Copy(value.Data, data, value.Data.Length);
        dataSize = bytes;
        return 0;
    }

    public void CloseKey(IntPtr key) {
        OnCall?.Invoke("CloseKey");
        ClosedKeys.Add(_openKeys[key]);
        _openKeys.Remove(key);
    }

    public int GetDeviceInterfaceListSize(out uint length, Guid interfaceClass, string? deviceId, uint flags) {
        OnCall?.Invoke("GetDeviceInterfaceListSize");
        InterfaceListQueries.Add(string.Format("{0} {1} 0x{2:X8}", interfaceClass, deviceId, flags));
        length = InterfaceListLengthOverride ?? (uint)MultiString(PathsFor(interfaceClass, deviceId)).Length;
        return InterfaceListSizeResult ?? CrSuccess;
    }

    public int GetDeviceInterfaceList(Guid interfaceClass, string? deviceId, char[] buffer, uint bufferLength, uint flags) {
        OnCall?.Invoke("GetDeviceInterfaceList");
        int result = InterfaceListResults.Count > 0 ? InterfaceListResults.Dequeue() : CrSuccess;
        if (result == CrSuccess) {
            char[] list = MultiString(PathsFor(interfaceClass, deviceId));
            Array.Copy(list, buffer, Math.Min(list.Length, (int)bufferLength));
        }

        return result;
    }

    public int GetDeviceInterfaceProperty(string deviceInterface, DevicePropertyKey key, byte[]? buffer, ref uint bufferSize) {
        OnCall?.Invoke("GetDeviceInterfaceProperty");
        if (key.FormatId != InstanceIdFormat || key.PropertyId != 256
            || !InterfaceInstances.TryGetValue(deviceInterface, out string? instanceId)) {
            bufferSize = 0;
            return CrNoSuchValue;
        }

        byte[] value = Encoding.Unicode.GetBytes(instanceId + "\0");
        return Property(value, buffer, ref bufferSize, InterfacePropertySizingResult, InterfacePropertyReadResult);
    }

    public int GetDeviceNodeProperty(uint deviceInstance, DevicePropertyKey key, byte[]? buffer, ref uint bufferSize) {
        OnCall?.Invoke("GetDeviceNodeProperty");
        if (key.FormatId != ContainerIdFormat || key.PropertyId != 2
            || !ContainerIds.TryGetValue(deviceInstance, out byte[]? value)) {
            bufferSize = 0;
            return CrNoSuchValue;
        }

        return Property(value, buffer, ref bufferSize, DeviceNodePropertySizingResult, DeviceNodePropertyReadResult);
    }

    private static int Property(byte[] value, byte[]? buffer, ref uint bufferSize, int sizingResult, int readResult) {
        if (buffer is null) {
            bufferSize = (uint)value.Length;
            return sizingResult;
        }

        if (readResult != CrSuccess) {
            return readResult;
        }

        Array.Copy(value, buffer, value.Length);
        bufferSize = (uint)value.Length;
        return CrSuccess;
    }

    private string[] PathsFor(Guid interfaceClass, string? deviceId)
        => Interfaces.TryGetValue((interfaceClass, deviceId), out string[]? paths) ? paths : Array.Empty<string>();
}

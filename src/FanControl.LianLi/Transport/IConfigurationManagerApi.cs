using System;

namespace FanControl.LianLi.Transport;

/// <summary>
/// The Windows configuration manager (<c>cfgmgr32</c>) and registry (<c>advapi32</c>) calls the device
/// locators make, one member per native function, so the decisions built on them - which flags are
/// asked for, which return codes are accepted, how a buffer is sized and split, which interface is
/// chosen - are tested without Windows. Every member returns the native function's own status code
/// unchanged: a <c>CONFIGRET</c> for the configuration-manager calls, a Win32 error for the registry.
/// </summary>
internal interface IConfigurationManagerApi {
    /// <summary><c>CM_Get_Device_ID_List_SizeW</c>: the length, in characters, of the id list.</summary>
    int GetDeviceIdListSize(out uint length, string filter, uint flags);

    /// <summary><c>CM_Get_Device_ID_ListW</c>: the null-separated device instance ids.</summary>
    int GetDeviceIdList(string filter, char[] buffer, uint bufferLength, uint flags);

    /// <summary><c>CM_Locate_DevNodeW</c>: the device node of a device instance id.</summary>
    int LocateDeviceNode(out uint deviceInstance, string deviceId, uint flags);

    /// <summary><c>CM_Open_DevNode_Key</c>: open one of a device node's registry keys.</summary>
    int OpenDeviceNodeKey(
        uint deviceInstance, int samDesired, uint hardwareProfile, uint disposition, out IntPtr key, uint flags);

    /// <summary><c>RegQueryValueExW</c>: size (a null <paramref name="data"/>) or read a string value.</summary>
    int QueryValue(IntPtr key, string name, out int type, char[]? data, ref uint dataSize);

    /// <summary><c>RegCloseKey</c>: release a key <see cref="OpenDeviceNodeKey"/> opened.</summary>
    void CloseKey(IntPtr key);

    /// <summary><c>CM_Get_Device_Interface_List_SizeW</c>: the length, in characters, of the interface list.</summary>
    int GetDeviceInterfaceListSize(out uint length, Guid interfaceClass, string? deviceId, uint flags);

    /// <summary><c>CM_Get_Device_Interface_ListW</c>: the null-separated interface paths.</summary>
    int GetDeviceInterfaceList(Guid interfaceClass, string? deviceId, char[] buffer, uint bufferLength, uint flags);

    /// <summary>
    /// <c>CM_Get_Device_Interface_PropertyW</c>: size (a null <paramref name="buffer"/>) or read one
    /// property of a device interface, in bytes.
    /// </summary>
    int GetDeviceInterfaceProperty(string deviceInterface, DevicePropertyKey key, byte[]? buffer, ref uint bufferSize);

    /// <summary>
    /// <c>CM_Get_DevNode_PropertyW</c>: size (a null <paramref name="buffer"/>) or read one property of
    /// a device node, in bytes.
    /// </summary>
    int GetDeviceNodeProperty(uint deviceInstance, DevicePropertyKey key, byte[]? buffer, ref uint bufferSize);
}

using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace FanControl.LianLi.Transport;

/// <summary>
/// <see cref="IConfigurationManagerApi"/> over <c>cfgmgr32</c> and <c>advapi32</c>.
/// </summary>
// Excluded from coverage: nothing here but the P/Invoke calls themselves. Every decision made on
// their results is ContainerIdResolver's or WinUsbDeviceLocator's, unit-tested through a fake.
[ExcludeFromCodeCoverage]
internal sealed class WindowsConfigurationManagerApi : IConfigurationManagerApi {
    /// <summary>The one instance; it holds no state.</summary>
    public static readonly WindowsConfigurationManagerApi Instance = new WindowsConfigurationManagerApi();

    private WindowsConfigurationManagerApi() {
    }

    public int GetDeviceIdListSize(out uint length, string filter, uint flags)
        => NativeMethods.CM_Get_Device_ID_List_SizeW(out length, filter, flags);

    public int GetDeviceIdList(string filter, char[] buffer, uint bufferLength, uint flags)
        => NativeMethods.CM_Get_Device_ID_ListW(filter, buffer, bufferLength, flags);

    public int LocateDeviceNode(out uint deviceInstance, string deviceId, uint flags)
        => NativeMethods.CM_Locate_DevNodeW(out deviceInstance, deviceId, flags);

    public int OpenDeviceNodeKey(
        uint deviceInstance, int samDesired, uint hardwareProfile, uint disposition, out IntPtr key, uint flags)
        => NativeMethods.CM_Open_DevNode_Key(deviceInstance, samDesired, hardwareProfile, disposition, out key, flags);

    public int QueryValue(IntPtr key, string name, out int type, char[]? data, ref uint dataSize)
        => NativeMethods.RegQueryValueExW(key, name, IntPtr.Zero, out type, data, ref dataSize);

    // A key that fails to close leaks one registry handle and nothing more: there is nothing the
    // caller could do about it, and the read it was opened for has already succeeded or failed.
    public void CloseKey(IntPtr key) => _ = NativeMethods.RegCloseKey(key);

    public int GetDeviceInterfaceListSize(out uint length, Guid interfaceClass, string? deviceId, uint flags)
        => NativeMethods.CM_Get_Device_Interface_List_SizeW(out length, ref interfaceClass, deviceId, flags);

    public int GetDeviceInterfaceList(Guid interfaceClass, string? deviceId, char[] buffer, uint bufferLength, uint flags)
        => NativeMethods.CM_Get_Device_Interface_ListW(ref interfaceClass, deviceId, buffer, bufferLength, flags);

    public int GetDeviceInterfaceProperty(string deviceInterface, DevicePropertyKey key, byte[]? buffer, ref uint bufferSize)
        => NativeMethods.CM_Get_Device_Interface_PropertyW(deviceInterface, ref key, out _, buffer, ref bufferSize, 0);

    public int GetDeviceNodeProperty(uint deviceInstance, DevicePropertyKey key, byte[]? buffer, ref uint bufferSize)
        => NativeMethods.CM_Get_DevNode_PropertyW(deviceInstance, ref key, out _, buffer, ref bufferSize, 0);

    private static class NativeMethods {
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int CM_Get_Device_ID_List_SizeW(out uint length, string filter, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int CM_Get_Device_ID_ListW(string filter, char[] buffer, uint bufferLength, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int CM_Locate_DevNodeW(out uint deviceInstance, string deviceId, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int CM_Open_DevNode_Key(
            uint deviceInstance, int samDesired, uint hardwareProfile, uint disposition, out IntPtr key, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int CM_Get_Device_Interface_List_SizeW(
            out uint length, ref Guid interfaceClassGuid, string? deviceId, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int CM_Get_Device_Interface_ListW(
            ref Guid interfaceClassGuid, string? deviceId, char[] buffer, uint bufferLength, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int CM_Get_Device_Interface_PropertyW(
            string deviceInterface, ref DevicePropertyKey propertyKey, out uint propertyType,
            byte[]? propertyBuffer, ref uint propertyBufferSize, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int CM_Get_DevNode_PropertyW(
            uint deviceInstance, ref DevicePropertyKey propertyKey, out uint propertyType,
            byte[]? propertyBuffer, ref uint propertyBufferSize, uint flags);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int RegQueryValueExW(
            IntPtr key, string name, IntPtr reserved, out int type, char[]? data, ref uint dataSize);

        [DllImport("advapi32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int RegCloseKey(IntPtr key);
    }
}

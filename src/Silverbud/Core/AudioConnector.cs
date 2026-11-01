using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Silverbud.Core;

/// <summary>
/// Connects or disconnects a paired Bluetooth headset's audio the same way the Connect button in
/// Windows Settings does: by sending the Bluetooth audio driver a one-shot KS property.
/// </summary>
public static class AudioConnector
{
    private static readonly Guid KsPropSetBtAudio = new("7FA06C40-B8F6-4C7E-8556-E8C33A12E54D");
    private static readonly PropertyKey ContainerIdKey = new(new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), 2);
    private const uint OneShotReconnect = 0;
    private const uint OneShotDisconnect = 1;
    private const uint KsPropertyTypeGet = 0x1;
    private const uint ClsCtxAll = 0x17;
    private const uint DeviceStateMaskAll = 0xF;
    private const uint DeviceStateActive = 0x1;
    private const int DataFlowAll = 2;
    private const ushort VtClsid = 72;

    /// <returns>How many Bluetooth audio drivers (A2DP / hands-free) accepted the request.</returns>
    public static Task<int> ConnectAsync(string address, Guid? containerId) =>
        Task.Run(() => Signal(address, containerId, OneShotReconnect));

    public static Task<int> DisconnectAsync(string address, Guid? containerId) =>
        Task.Run(() => Signal(address, containerId, OneShotDisconnect));

    /// <summary>True when Windows has a working (active) audio endpoint for the buds, LE Audio or classic.</summary>
    public static Task<bool> IsAudioActiveAsync(Guid? containerId) => Task.Run(() =>
    {
        if (containerId is not Guid id)
            return false;
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (enumerator.EnumAudioEndpoints(DataFlowAll, DeviceStateActive, out var endpoints) != 0)
                return false;
            endpoints.GetCount(out uint count);
            for (uint i = 0; i < count; i++)
                if (endpoints.Item(i, out var endpoint) == 0 && ReadContainerId(endpoint) == id)
                    return true;
        }
        catch (Exception ex)
        {
            Log.Write($"Audio state check failed: {ex.Message}");
        }
        return false;
    });

    private static int Signal(string address, Guid? containerId, uint property)
    {
        string addr = address.Replace(":", "").ToUpperInvariant();
        int accepted = 0;
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (enumerator.EnumAudioEndpoints(DataFlowAll, DeviceStateMaskAll, out var endpoints) != 0)
                return 0;
            endpoints.GetCount(out uint count);
            var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (uint i = 0; i < count; i++)
            {
                try
                {
                    if (endpoints.Item(i, out var endpoint) != 0)
                        continue;
                    string? filterId = GetKsFilterId(endpoint);
                    if (filterId is null)
                        continue;

                    bool sameDevice = containerId is Guid g && ReadContainerId(endpoint) == g;
                    bool addressInId = addr.Length == 12 && filterId.Contains(addr, StringComparison.OrdinalIgnoreCase);
                    if ((!sameDevice && !addressInId) || !done.Add(filterId))
                        continue;

                    if (enumerator.GetDevice(filterId, out var filterDevice) != 0)
                        continue;
                    var iid = typeof(IKsControl).GUID;
                    if (filterDevice.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out var ksObject) != 0)
                        continue;

                    var request = new KsProperty { Set = KsPropSetBtAudio, Id = property, Flags = KsPropertyTypeGet };
                    int hr = ((IKsControl)ksObject).KsProperty(ref request, (uint)Marshal.SizeOf<KsProperty>(), IntPtr.Zero, 0, out _);
                    Log.Write($"Audio {(property == OneShotReconnect ? "connect" : "disconnect")} -> {filterId}: 0x{hr:X8}");
                    if (hr >= 0)
                        accepted++;
                }
                catch (Exception ex)
                {
                    Log.Write($"Audio endpoint {i}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Audio connector failed: {ex.Message}");
        }
        return accepted;
    }

    /// <summary>Diagnostics (--probe-audio): lists every endpoint with its driver filter and container id.</summary>
    public static List<string> Describe()
    {
        var lines = new List<string>();
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        if (enumerator.EnumAudioEndpoints(DataFlowAll, DeviceStateMaskAll, out var endpoints) != 0)
            return lines;
        endpoints.GetCount(out uint count);
        for (uint i = 0; i < count; i++)
        {
            try
            {
                if (endpoints.Item(i, out var endpoint) != 0)
                    continue;
                endpoint.GetState(out uint state);
                lines.Add($"state={state} container={ReadContainerId(endpoint)} filter={GetKsFilterId(endpoint)}");
            }
            catch (Exception ex)
            {
                lines.Add($"endpoint {i}: {ex.Message}");
            }
        }
        return lines;
    }

    private static string? GetKsFilterId(IMMDevice endpoint)
    {
        var iid = typeof(IDeviceTopology).GUID;
        if (endpoint.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out var obj) != 0)
            return null;
        var topology = (IDeviceTopology)obj;
        if (topology.GetConnector(0, out var connector) != 0)
            return null;
        return connector.GetDeviceIdConnectedTo(out var id) == 0 ? id : null;
    }

    private static Guid? ReadContainerId(IMMDevice endpoint)
    {
        if (endpoint.OpenPropertyStore(0, out var store) != 0)
            return null;
        var key = ContainerIdKey;
        if (store.GetValue(ref key, out var value) != 0)
            return null;
        try
        {
            return value.vt == VtClsid && value.pointer != IntPtr.Zero ? Marshal.PtrToStructure<Guid>(value.pointer) : null;
        }
        finally
        {
            PropVariantClear(ref value);
        }
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject;

    [StructLayout(LayoutKind.Sequential)]
    private struct KsProperty
    {
        public Guid Set;
        public uint Id;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid fmtid, uint pid)
    {
        public Guid fmtid = fmtid;
        public uint pid = pid;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pointer;
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("2A07407E-6497-4A18-9787-32F79BD0D98F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeviceTopology
    {
        [PreserveSig] int GetConnectorCount(out uint count);
        [PreserveSig] int GetConnector(uint index, out IConnector connector);
        [PreserveSig] int GetSubunitCount(out uint count);
        [PreserveSig] int GetSubunit(uint index, out IntPtr subunit);
        [PreserveSig] int GetPartById(uint id, out IntPtr part);
        [PreserveSig] int GetDeviceId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetSignalPath(IntPtr from, IntPtr to, [MarshalAs(UnmanagedType.Bool)] bool rejectMixedPaths, out IntPtr parts);
    }

    [ComImport, Guid("9C2C4058-23F5-41DE-877A-DF3AF236A09E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IConnector
    {
        [PreserveSig] int GetType(out int type);
        [PreserveSig] int GetDataFlow(out int flow);
        [PreserveSig] int ConnectTo(IConnector other);
        [PreserveSig] int Disconnect();
        [PreserveSig] int IsConnected([MarshalAs(UnmanagedType.Bool)] out bool connected);
        [PreserveSig] int GetConnectedTo(out IConnector other);
        [PreserveSig] int GetConnectorIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetDeviceIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("28F54685-06FD-11D2-B27A-00A0C9223196"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IKsControl
    {
        [PreserveSig] int KsProperty(ref KsProperty property, uint propertyLength, IntPtr data, uint dataLength, out uint bytesReturned);
        [PreserveSig] int KsMethod(IntPtr method, uint methodLength, IntPtr data, uint dataLength, out uint bytesReturned);
        [PreserveSig] int KsEvent(IntPtr ksEvent, uint eventLength, IntPtr data, uint dataLength, out uint bytesReturned);
    }
}

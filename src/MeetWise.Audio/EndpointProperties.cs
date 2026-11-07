using System.Runtime.InteropServices;

namespace MeetWise.Audio;

/// <summary>
/// Reads and writes the display name of an audio endpoint through the Windows property store (what the Sound control panel's
/// Rename button does). Writing needs an elevated process; the installer helper runs elevated, the normal app never writes.
/// </summary>
public static class EndpointProperties
{
    // PKEY_Device_DeviceDesc: the part of the name shown before the adapter name in brackets.
    private static readonly PropKey DeviceDesc = new() { Format = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), Id = 2 };
    private const int StgmRead = 0, StgmWrite = 1, VtLpwstr = 31;

    public static string? ReadDescription(string endpointId)
    {
        var store = Open(endpointId, StgmRead);
        try
        {
            var key = DeviceDesc;
            if (store.GetValue(ref key, out var v) != 0) return null;
            try { return v.Vt == VtLpwstr && v.Pointer != IntPtr.Zero ? Marshal.PtrToStringUni(v.Pointer) : null; }
            finally { PropVariantClear(ref v); }
        }
        finally { Marshal.ReleaseComObject(store); }
    }

    /// <summary>Sets the endpoint's name. Returns an empty string on success, otherwise the reason it failed.</summary>
    public static string WriteDescription(string endpointId, string name)
    {
        IPropertyStore? store = null;
        var ptr = IntPtr.Zero;
        try
        {
            store = Open(endpointId, StgmWrite);
            ptr = Marshal.StringToCoTaskMemUni(name);
            var key = DeviceDesc;
            var value = new PropVariantRaw { Vt = VtLpwstr, Pointer = ptr };
            int hr = store.SetValue(ref key, ref value);
            if (hr != 0) return $"Windows refused the rename (0x{hr:X8}); administrator rights are required.";
            hr = store.Commit();
            return hr == 0 ? "" : $"Windows could not save the new name (0x{hr:X8}).";
        }
        catch (Exception ex) { return ex.Message; }
        finally
        {
            if (ptr != IntPtr.Zero) Marshal.FreeCoTaskMem(ptr);
            if (store is not null) Marshal.ReleaseComObject(store);
        }
    }

    private static IPropertyStore Open(string endpointId, int access)
    {
        // Created by CLSID (not 'new' on a [ComImport] class): NAudio declares the same CLSID, and .NET would hand back NAudio's wrapper type.
        var enumerator = (IMMDeviceEnumeratorCom)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))!)!;
        try
        {
            Marshal.ThrowExceptionForHR(enumerator.GetDevice(endpointId, out var device));
            try
            {
                Marshal.ThrowExceptionForHR(device.OpenPropertyStore(access, out var store));
                return store;
            }
            finally { Marshal.ReleaseComObject(device); }
        }
        finally { Marshal.ReleaseComObject(enumerator); }
    }

    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariantRaw value);

    [StructLayout(LayoutKind.Sequential)] private struct PropKey { public Guid Format; public uint Id; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariantRaw
    {
        public ushort Vt; public ushort R1, R2, R3;
        public IntPtr Pointer; public IntPtr Pointer2;
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumeratorCom
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDeviceCom endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDeviceCom device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCom
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropKey key);
        [PreserveSig] int GetValue(ref PropKey key, out PropVariantRaw value);
        [PreserveSig] int SetValue(ref PropKey key, ref PropVariantRaw value);
        [PreserveSig] int Commit();
    }
}

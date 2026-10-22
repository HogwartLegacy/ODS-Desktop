using System.Runtime.InteropServices;
using Ods.Core.Platform;

namespace Ods.Core.Hardware;

/// <summary>
/// Display adapters through DXGI (IDXGIFactory1::EnumAdapters1 and
/// IDXGIAdapter1::GetDesc1), called through the COM vtables directly. DXGI
/// reports 64-bit memory sizes, unlike WMI's 32-bit AdapterRAM.
/// </summary>
internal static unsafe class DxgiAdapters
{
    private static readonly Guid IidFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private static readonly Guid IidDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    private const int ErrorNotFound = unchecked((int)0x887A0002);
    private const uint AdapterFlagSoftware = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterDesc1
    {
        public fixed char Description[128];
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public uint LuidLowPart;
        public int LuidHighPart;
        public uint Flags;
    }

    public static List<GpuAdapter> Enumerate()
    {
        var adapters = new List<GpuAdapter>();
        var hr = NativeMethods.CreateDXGIFactory1(IidFactory1, out var factory);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        try
        {
            var factoryTable = *(nint**)factory;
            var enumAdapters1 = (delegate* unmanaged<nint, uint, nint*, int>)factoryTable[12];
            for (uint index = 0; ; index++)
            {
                nint adapter;
                hr = enumAdapters1(factory, index, &adapter);
                if (hr == ErrorNotFound) break;
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                try
                {
                    var described = Describe(adapter);
                    if (described is not null) adapters.Add(described);
                }
                finally
                {
                    Release(adapter);
                }
            }
        }
        finally
        {
            Release(factory);
        }
        return adapters;
    }

    private static GpuAdapter? Describe(nint adapter)
    {
        var table = *(nint**)adapter;
        var getDesc1 = (delegate* unmanaged<nint, AdapterDesc1*, int>)table[10];
        AdapterDesc1 desc;
        if (getDesc1(adapter, &desc) < 0) return null;

        var checkInterfaceSupport = (delegate* unmanaged<nint, Guid*, long*, int>)table[9];
        var iid = IidDevice;
        long umdVersion = 0;
        var driver = checkInterfaceSupport(adapter, &iid, &umdVersion) >= 0 ? FormatDriverVersion(umdVersion) : "";

        var name = new string(desc.Description).TrimEnd('\0').Trim();
        return new GpuAdapter(
            name,
            desc.VendorId,
            desc.DeviceId,
            (long)desc.DedicatedVideoMemory,
            (long)desc.SharedSystemMemory,
            driver,
            (desc.Flags & AdapterFlagSoftware) != 0 || desc.VendorId == GpuAdapter.MicrosoftVendor);
    }

    /// <summary>The user-mode driver version as Windows shows it, e.g. 32.0.15.6094.</summary>
    internal static string FormatDriverVersion(long umd)
    {
        var high = (int)(umd >> 32);
        var low = (int)(umd & 0xFFFFFFFF);
        return $"{(high >> 16) & 0xFFFF}.{high & 0xFFFF}.{(low >> 16) & 0xFFFF}.{low & 0xFFFF}";
    }

    private static void Release(nint unknown)
    {
        var table = *(nint**)unknown;
        ((delegate* unmanaged<nint, uint>)table[2])(unknown);
    }
}

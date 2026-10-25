using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace XRay.App.Services.Metrics;

/// <summary>
/// Vendor-neutral GPU monitoring using only components that ship with Windows:
/// <list type="bullet">
/// <item>DXGI for adapter names and dedicated memory size,</item>
/// <item>the "GPU Engine" / "GPU Adapter Memory" performance counters (WDDM 2.0+) for utilisation and memory use,</item>
/// <item>D3DKMTQueryAdapterInfo(ADAPTERPERFDATA) for temperature, which only some drivers report (WDDM 2.4+).</item>
/// </list>
/// Each source fails independently; missing data is reported as null with a reason, never as zero.
/// </summary>
internal sealed class GpuMonitor
{
    private const int KmtQaiTypeAdapterPerfData = 62;
    private const uint DxgiAdapterFlagSoftware = 2;

    private readonly List<Adapter> _adapters = [];
    private readonly Dictionary<string, CounterSample> _lastEngineSamples = new(StringComparer.Ordinal);
    private PerformanceCounterCategory? _engineCategory;
    private PerformanceCounterCategory? _memoryCategory;

    public string? UnavailableReason { get; private set; }

    public void Initialize()
    {
        try
        {
            EnumerateAdapters();
        }
        catch (Exception ex) when (ex is COMException or DllNotFoundException or EntryPointNotFoundException or InvalidCastException)
        {
            UnavailableReason = "Graphics adapters could not be enumerated through DXGI.";
            return;
        }

        if (_adapters.Count == 0)
        {
            UnavailableReason = "No hardware graphics adapter was found (only software rendering is available).";
            return;
        }

        try
        {
            if (PerformanceCounterCategory.Exists("GPU Engine"))
            {
                _engineCategory = new PerformanceCounterCategory("GPU Engine");
            }

            if (PerformanceCounterCategory.Exists("GPU Adapter Memory"))
            {
                _memoryCategory = new PerformanceCounterCategory("GPU Adapter Memory");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or UnauthorizedAccessException)
        {
            // Leave utilisation unavailable; adapter names and temperature may still work.
        }

        foreach (Adapter adapter in _adapters)
        {
            adapter.TemperatureNote = ProbeTemperature(adapter) is null
                ? "Temperature is not reported by this graphics driver."
                : null;
        }
    }

    public IReadOnlyList<GpuInfo> Sample()
    {
        if (_adapters.Count == 0)
        {
            return [];
        }

        Dictionary<string, double>? utilisation = ReadUtilisation();
        Dictionary<string, ulong>? dedicated = ReadDedicatedUsage();
        var list = new List<GpuInfo>(_adapters.Count);
        foreach (Adapter a in _adapters)
        {
            double? util = utilisation is null ? null : utilisation.GetValueOrDefault(a.LuidKey, 0);
            ulong? used = dedicated is not null && dedicated.TryGetValue(a.LuidKey, out ulong u) ? u : null;
            double? temp = a.TemperatureNote is null ? ProbeTemperature(a) : null;
            list.Add(new GpuInfo(a.Name, util, temp, used, a.DedicatedBytes,
                a.TemperatureNote ?? (temp is null ? "Temperature reading failed." : null)));
        }

        return list;
    }

    private void EnumerateAdapters()
    {
        Guid iid = typeof(IDXGIFactory1).GUID;
        if (CreateDXGIFactory1(ref iid, out IDXGIFactory1? factory) < 0 || factory is null)
        {
            throw new COMException("CreateDXGIFactory1 failed");
        }

        try
        {
            var seen = new HashSet<string>();
            for (uint i = 0; factory.EnumAdapters1(i, out IDXGIAdapter1? adapter) >= 0 && adapter is not null; i++)
            {
                try
                {
                    if (adapter.GetDesc1(out DxgiAdapterDesc1 desc) < 0 || (desc.Flags & DxgiAdapterFlagSoftware) != 0)
                    {
                        continue;
                    }

                    // Microsoft Basic Render Driver.
                    if (desc.VendorId == 0x1414 && desc.DeviceId == 0x8c)
                    {
                        continue;
                    }

                    string key = LuidKey(desc.AdapterLuid.HighPart, desc.AdapterLuid.LowPart);
                    if (seen.Add(key))
                    {
                        _adapters.Add(new Adapter(desc.Description.Trim(), desc.AdapterLuid, key, (ulong)desc.DedicatedVideoMemory));
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(adapter);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }

    /// <summary>Sums engine utilisation per adapter and engine type, then takes the busiest engine type (like Task Manager).</summary>
    private Dictionary<string, double>? ReadUtilisation()
    {
        if (_engineCategory is null)
        {
            return null;
        }

        InstanceDataCollectionCollection data;
        try
        {
            data = _engineCategory.ReadCategory();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or UnauthorizedAccessException)
        {
            return null;
        }

        InstanceDataCollection? utilisation = FindCounter(data, "Utilization Percentage");
        if (utilisation is null)
        {
            return null;
        }

        var perEngineType = new Dictionary<(string Luid, string Engine), double>();
        var current = new HashSet<string>(StringComparer.Ordinal);
        foreach (InstanceData instance in utilisation.Values)
        {
            string name = instance.InstanceName;
            current.Add(name);
            CounterSample sample = instance.Sample;
            if (_lastEngineSamples.TryGetValue(name, out CounterSample previous))
            {
                float value = CounterSample.Calculate(previous, sample);
                if (value > 0 && TryParseEngineInstance(name, out string luid, out string engine))
                {
                    perEngineType[(luid, engine)] = perEngineType.GetValueOrDefault((luid, engine)) + value;
                }
            }

            _lastEngineSamples[name] = sample;
        }

        foreach (string stale in _lastEngineSamples.Keys.Where(k => !current.Contains(k)).ToList())
        {
            _lastEngineSamples.Remove(stale);
        }

        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var ((luid, _), value) in perEngineType)
        {
            result[luid] = Math.Max(result.GetValueOrDefault(luid), Math.Min(100, value));
        }

        return result;
    }

    private Dictionary<string, ulong>? ReadDedicatedUsage()
    {
        if (_memoryCategory is null)
        {
            return null;
        }

        try
        {
            InstanceDataCollection? usage = FindCounter(_memoryCategory.ReadCategory(), "Dedicated Usage");
            if (usage is null)
            {
                return null;
            }

            var result = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
            foreach (InstanceData instance in usage.Values)
            {
                if (TryParseLuid(instance.InstanceName, out string luid))
                {
                    result[luid] = result.GetValueOrDefault(luid) + (ulong)Math.Max(0, instance.RawValue);
                }
            }

            return result;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static InstanceDataCollection? FindCounter(InstanceDataCollectionCollection data, string counter)
    {
        foreach (string key in data.Keys)
        {
            if (string.Equals(key, counter, StringComparison.OrdinalIgnoreCase))
            {
                return data[key];
            }
        }

        return null;
    }

    /// <summary>Parses "pid_1234_luid_0x00000000_0x0000C7B1_phys_0_eng_0_engtype_3D".</summary>
    internal static bool TryParseEngineInstance(string name, out string luid, out string engine)
    {
        engine = string.Empty;
        if (!TryParseLuid(name, out luid))
        {
            return false;
        }

        int idx = name.IndexOf("engtype_", StringComparison.OrdinalIgnoreCase);
        engine = idx >= 0 ? name[(idx + 8)..] : "unknown";
        return true;
    }

    internal static bool TryParseLuid(string name, out string luid)
    {
        luid = string.Empty;
        int idx = name.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
        if (idx < 0 || name.Length < idx + 5 + 21)
        {
            return false;
        }

        // "0x00000000_0x0000C7B1" = high part, low part.
        string[] parts = name.Substring(idx + 5, 21).Split('_');
        if (parts.Length != 2 ||
            !uint.TryParse(parts[0].AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint high) ||
            !uint.TryParse(parts[1].AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint low))
        {
            return false;
        }

        luid = LuidKey((int)high, low);
        return true;
    }

    private static string LuidKey(int high, uint low) => $"{(uint)high:X8}_{low:X8}";

    private static double? ProbeTemperature(Adapter adapter)
    {
        try
        {
            var open = new D3dkmtOpenAdapterFromLuid { AdapterLuid = adapter.Luid };
            if (D3DKMTOpenAdapterFromLuid(ref open) != 0)
            {
                return null;
            }

            try
            {
                int size = Marshal.SizeOf<D3dkmtAdapterPerfData>();
                IntPtr buffer = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(new D3dkmtAdapterPerfData(), buffer, false);
                    var query = new D3dkmtQueryAdapterInfo
                    {
                        hAdapter = open.hAdapter,
                        Type = KmtQaiTypeAdapterPerfData,
                        pPrivateDriverData = buffer,
                        PrivateDriverDataSize = (uint)size,
                    };
                    if (D3DKMTQueryAdapterInfo(ref query) != 0)
                    {
                        return null;
                    }

                    var perf = Marshal.PtrToStructure<D3dkmtAdapterPerfData>(buffer);
                    // Reported in tenths of a degree Celsius; 0 means "not supported".
                    double celsius = perf.Temperature / 10.0;
                    return celsius is > 0 and < 150 ? celsius : null;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                var close = new D3dkmtCloseAdapter { hAdapter = open.hAdapter };
                D3DKMTCloseAdapter(ref close);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private sealed class Adapter(string name, Luid luid, string luidKey, ulong dedicatedBytes)
    {
        public string Name { get; } = name;

        public Luid Luid { get; } = luid;

        public string LuidKey { get; } = luidKey;

        public ulong DedicatedBytes { get; } = dedicatedBytes;

        public string? TemperatureNote { get; set; }
    }

    // ---- Interop ----

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DxgiAdapterDesc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public Luid AdapterLuid;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3dkmtOpenAdapterFromLuid
    {
        public Luid AdapterLuid;
        public uint hAdapter;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3dkmtCloseAdapter
    {
        public uint hAdapter;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3dkmtQueryAdapterInfo
    {
        public uint hAdapter;
        public int Type;
        public IntPtr pPrivateDriverData;
        public uint PrivateDriverDataSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3dkmtAdapterPerfData
    {
        public uint PhysicalAdapterIndex;
        public ulong MemoryFrequency;
        public ulong MaxMemoryFrequency;
        public ulong MaxMemoryFrequencyOC;
        public ulong MemoryBandwidth;
        public ulong PCIEBandwidth;
        public uint FanRPM;
        public uint Power;
        public uint Temperature;
        public byte PowerStateOverride;
    }

    [ComImport]
    [Guid("770aae78-f26f-4dba-a829-253c83d1b387")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        [PreserveSig] int SetPrivateData(ref Guid name, uint size, IntPtr data);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint size, IntPtr data);
        [PreserveSig] int GetParent(ref Guid riid, out IntPtr parent);
        [PreserveSig] int EnumAdapters(uint index, out IntPtr adapter);
        [PreserveSig] int MakeWindowAssociation(IntPtr hwnd, uint flags);
        [PreserveSig] int GetWindowAssociation(out IntPtr hwnd);
        [PreserveSig] int CreateSwapChain(IntPtr device, IntPtr desc, out IntPtr swapChain);
        [PreserveSig] int CreateSoftwareAdapter(IntPtr module, out IntPtr adapter);
        [PreserveSig] int EnumAdapters1(uint index, out IDXGIAdapter1? adapter);
        [PreserveSig] int IsCurrent();
    }

    [ComImport]
    [Guid("29038f61-3839-4626-91fd-086879011a05")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        [PreserveSig] int SetPrivateData(ref Guid name, uint size, IntPtr data);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint size, IntPtr data);
        [PreserveSig] int GetParent(ref Guid riid, out IntPtr parent);
        [PreserveSig] int EnumOutputs(uint index, out IntPtr output);
        [PreserveSig] int GetDesc(IntPtr desc);
        [PreserveSig] int CheckInterfaceSupport(ref Guid name, out long umdVersion);
        [PreserveSig] int GetDesc1(out DxgiAdapterDesc1 desc);
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IDXGIFactory1? factory);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTOpenAdapterFromLuid(ref D3dkmtOpenAdapterFromLuid open);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTQueryAdapterInfo(ref D3dkmtQueryAdapterInfo query);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTCloseAdapter(ref D3dkmtCloseAdapter close);
}

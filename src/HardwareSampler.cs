using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace CodexTaskbarMonitor
{
    /// <summary>
    /// Read-only, dependency-free Windows hardware sampling. Call Sample on a worker
    /// thread: PDH and graphics-driver calls must never run on the UI thread.
    /// CPU is elapsed busy time (GetSystemTimes), not frequency-weighted utility.
    /// </summary>
    public sealed class HardwareSampler : IDisposable
    {
        private readonly object sync = new object();
        private readonly List<Adapter> adapters = new List<Adapter>();
        private bool disposed;
        private bool initialized;
        private bool hasCpuBaseline;
        private ulong previousIdle, previousKernel, previousUser;
        private DateTime nextDiscoveryUtc;
        private string discoveryError;
        private Nvml nvml;
        private GpuCounters counters;

        public HardwareSampler()
        {
            Native.FileTime idle, kernel, user;
            hasCpuBaseline = Native.GetSystemTimes(out idle, out kernel, out user);
            if (hasCpuBaseline)
            {
                previousIdle = idle.Value;
                previousKernel = kernel.Value;
                previousUser = user.Value;
            }
        }

        public HardwareSnapshot Sample()
        {
            lock (sync)
            {
                if (disposed) throw new ObjectDisposedException("HardwareSampler");
                HardwareSnapshot result = new HardwareSnapshot();
                result.TimestampUtc = DateTime.UtcNow;
                List<string> errors = new List<string>();
                result.CpuPercent = ReadCpu(errors);
                Native.MemoryStatus memory = new Native.MemoryStatus();
                memory.Length = (uint)Marshal.SizeOf(typeof(Native.MemoryStatus));
                if (Native.GlobalMemoryStatusEx(ref memory))
                {
                    result.MemoryTotalBytes = ToLong(memory.TotalPhys);
                    result.MemoryAvailableBytes = ToLong(memory.AvailPhys);
                    if (memory.TotalPhys > 0)
                        result.MemoryPercent = Clamp(100.0 * (memory.TotalPhys - memory.AvailPhys) / memory.TotalPhys);
                }
                else errors.Add("GlobalMemoryStatusEx: " + Marshal.GetLastWin32Error());

                if (!initialized || result.TimestampUtc >= nextDiscoveryUtc)
                    DiscoverAdapters();
                if (!String.IsNullOrEmpty(discoveryError)) errors.Add(discoveryError);

                CounterSnapshot values = counters == null ? new CounterSnapshot() : counters.Sample();
                foreach (Adapter adapter in adapters)
                {
                    GpuSnapshot gpu = new GpuSnapshot();
                    gpu.Id = adapter.Id;
                    gpu.Name = adapter.Name;
                    gpu.DedicatedTotalBytes = adapter.DedicatedBytes > 0 ? (long?)adapter.DedicatedBytes : null;
                    List<string> sources = new List<string>();
                    bool nvmlCapacity = false;

                    if (nvml != null && adapter.NvmlDevice != IntPtr.Zero)
                    {
                        int utilizationStatus, memoryStatus;
                        gpu.UtilizationPercent = nvml.ReadUtilization(adapter.NvmlDevice, out utilizationStatus);
                        long? used, total;
                        nvml.ReadMemory(adapter.NvmlDevice, out used, out total, out memoryStatus);
                        if (used.HasValue) gpu.DedicatedUsedBytes = used;
                        if (total.HasValue) { gpu.DedicatedTotalBytes = total; nvmlCapacity = true; }
                        sources.Add("NVIDIA NVML");
                        if (utilizationStatus != 0) sources.Add("utilization unavailable (NVML " + utilizationStatus + ")");
                        if (memoryStatus != 0) sources.Add("VRAM unavailable (NVML " + memoryStatus + ")");
                    }

                    if (!gpu.UtilizationPercent.HasValue)
                    {
                        double percent;
                        if (values.Utilization.TryGetValue(adapter.LuidKey, out percent))
                        {
                            gpu.UtilizationPercent = Clamp(percent);
                            sources.Add("Windows GPU Engine: busiest engine after summing processes");
                        }
                        else sources.Add("Windows GPU Engine unavailable" + Suffix(values.EngineError));
                    }
                    if (!gpu.DedicatedUsedBytes.HasValue)
                    {
                        double used;
                        if (values.DedicatedUsage.TryGetValue(adapter.LuidKey, out used) && used >= 0)
                            gpu.DedicatedUsedBytes = used > Int64.MaxValue ? Int64.MaxValue : (long)used;
                        else sources.Add("dedicated usage unavailable" + Suffix(values.MemoryError));
                    }
                    if (gpu.DedicatedTotalBytes.HasValue && !nvmlCapacity)
                        sources.Add("dedicated capacity: DXGI");
                    if (!gpu.DedicatedTotalBytes.HasValue)
                        sources.Add("dedicated capacity unavailable / unified memory");
                    if (adapter.IdentityFallback) sources.Add("identity fallback: DXGI hardware + adapter ordinal");
                    gpu.Source = String.Join("; ", sources.ToArray());
                    result.Gpus.Add(gpu);
                }
                if (result.Gpus.Count == 0) errors.Add("No hardware DXGI adapters were available.");
                result.Error = errors.Count == 0 ? null : String.Join("; ", errors.ToArray());
                return result;
            }
        }

        private double? ReadCpu(List<string> errors)
        {
            Native.FileTime idle, kernel, user;
            if (!Native.GetSystemTimes(out idle, out kernel, out user))
            {
                hasCpuBaseline = false;
                errors.Add("GetSystemTimes: " + Marshal.GetLastWin32Error());
                return null;
            }
            double? value = null;
            if (hasCpuBaseline && idle.Value >= previousIdle && kernel.Value >= previousKernel && user.Value >= previousUser)
            {
                ulong busyAndIdle = (kernel.Value - previousKernel) + (user.Value - previousUser);
                ulong idleDelta = idle.Value - previousIdle;
                // Kernel time includes idle time. No interval means unknown, not 0%.
                if (busyAndIdle > 0 && busyAndIdle >= idleDelta)
                    value = Clamp(100.0 * (busyAndIdle - idleDelta) / busyAndIdle);
            }
            previousIdle = idle.Value;
            previousKernel = kernel.Value;
            previousUser = user.Value;
            hasCpuBaseline = true;
            return value;
        }

        private void DiscoverAdapters()
        {
            initialized = true;
            nextDiscoveryUtc = DateTime.UtcNow.AddSeconds(60);
            discoveryError = null;
            Nvml replacementNvml = null;
            try
            {
                List<Adapter> found = Dxgi.Enumerate();
                replacementNvml = Nvml.TryOpen();
                if (replacementNvml != null) replacementNvml.Attach(found);
                if (nvml != null) nvml.Dispose();
                nvml = replacementNvml;
                replacementNvml = null;
                adapters.Clear();
                adapters.AddRange(found);
                if (counters == null) counters = new GpuCounters();
            }
            catch (Exception ex)
            {
                discoveryError = "GPU discovery: " + ex.Message;
            }
            finally { if (replacementNvml != null) replacementNvml.Dispose(); }
        }

        private static long ToLong(ulong value) { return value > Int64.MaxValue ? Int64.MaxValue : (long)value; }
        private static double Clamp(double value) { return Math.Max(0, Math.Min(100, value)); }
        private static string Suffix(string value) { return String.IsNullOrEmpty(value) ? " (no fresh matching samples)" : " (" + value + ")"; }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                if (counters != null) counters.Dispose();
                if (nvml != null) nvml.Dispose();
            }
        }

        private sealed class Adapter
        {
            public string Id, Name, LuidKey;
            public uint VendorId, DeviceId, SubSystemId;
            public uint? Bus, Device, Function;
            public long DedicatedBytes;
            public IntPtr NvmlDevice;
            public bool IdentityFallback;
        }

        private static class Dxgi
        {
            [StructLayout(LayoutKind.Sequential)]
            private struct Luid { public uint Low; public int High; }
            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            private struct AdapterDesc
            {
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
                public uint VendorId, DeviceId, SubSystemId, Revision;
                public UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
                public Luid AdapterLuid;
                public uint Flags;
            }
            [StructLayout(LayoutKind.Sequential)] private struct OpenAdapter { public Luid Luid; public uint Handle; }
            [StructLayout(LayoutKind.Sequential)] private struct QueryAdapter { public uint Handle; public int Type; public IntPtr Data; public uint Size; }
            [StructLayout(LayoutKind.Sequential)] private struct AdapterAddress { public uint Bus, Device, Function; }
            [StructLayout(LayoutKind.Sequential)] private struct CloseAdapter { public uint Handle; }
            [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EnumAdapters1(IntPtr self, uint index, out IntPtr adapter);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetDesc1(IntPtr self, out AdapterDesc desc);
            [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);
            [DllImport("gdi32.dll")] private static extern int D3DKMTOpenAdapterFromLuid(ref OpenAdapter data);
            [DllImport("gdi32.dll")] private static extern int D3DKMTQueryAdapterInfo(ref QueryAdapter data);
            [DllImport("gdi32.dll")] private static extern int D3DKMTCloseAdapter(ref CloseAdapter data);

            public static List<Adapter> Enumerate()
            {
                List<Adapter> result = new List<Adapter>();
                Guid iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
                IntPtr factory;
                int hr = CreateDXGIFactory1(ref iid, out factory);
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                try
                {
                    EnumAdapters1 enumerate = (EnumAdapters1)DelegateFor(factory, 12, typeof(EnumAdapters1));
                    for (uint index = 0; index < 64; index++)
                    {
                        IntPtr handle;
                        hr = enumerate(factory, index, out handle);
                        if (hr == unchecked((int)0x887A0002)) break; // DXGI_ERROR_NOT_FOUND
                        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                        try
                        {
                            AdapterDesc desc;
                            GetDesc1 describe = (GetDesc1)DelegateFor(handle, 10, typeof(GetDesc1));
                            hr = describe(handle, out desc);
                            if (hr < 0 || (desc.Flags & 2) != 0) continue; // skip software renderer
                            Adapter adapter = new Adapter();
                            adapter.Name = (desc.Description ?? "GPU").TrimEnd('\0');
                            adapter.VendorId = desc.VendorId;
                            adapter.DeviceId = desc.DeviceId;
                            adapter.SubSystemId = desc.SubSystemId;
                            adapter.DedicatedBytes = ToLong(desc.DedicatedVideoMemory.ToUInt64() + desc.DedicatedSystemMemory.ToUInt64());
                            adapter.LuidKey = String.Format(CultureInfo.InvariantCulture, "luid_0x{0:x8}_0x{1:x8}", unchecked((uint)desc.AdapterLuid.High), desc.AdapterLuid.Low);
                            AdapterAddress address;
                            if (TryAddress(desc.AdapterLuid, out address))
                            {
                                adapter.Bus = address.Bus;
                                adapter.Device = address.Device;
                                adapter.Function = address.Function;
                                // PCI location + hardware IDs survives process/app/OS restarts.
                                // Moving hardware to another slot can change a PCI identity.
                                adapter.Id = String.Format(CultureInfo.InvariantCulture, "pci:{0:x4}:{1:x4}:{2:x8}:{3:x2}:{4:x2}.{5}", desc.VendorId, desc.DeviceId, desc.SubSystemId, address.Bus, address.Device, address.Function);
                            }
                            else
                            {
                                adapter.IdentityFallback = true;
                                adapter.Id = String.Format(CultureInfo.InvariantCulture, "dxgi:{0:x4}:{1:x4}:{2:x8}:{3}", desc.VendorId, desc.DeviceId, desc.SubSystemId, index);
                            }
                            result.Add(adapter);
                        }
                        finally { Marshal.Release(handle); }
                    }
                }
                finally { Marshal.Release(factory); }
                return result;
            }

            private static Delegate DelegateFor(IntPtr obj, int slot, Type type)
            {
                IntPtr table = Marshal.ReadIntPtr(obj);
                return Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(table, slot * IntPtr.Size), type);
            }

            private static bool TryAddress(Luid luid, out AdapterAddress address)
            {
                address = new AdapterAddress();
                OpenAdapter open = new OpenAdapter();
                open.Luid = luid;
                try
                {
                    if (D3DKMTOpenAdapterFromLuid(ref open) != 0) return false;
                    IntPtr data = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(AdapterAddress)));
                    try
                    {
                        QueryAdapter query = new QueryAdapter();
                        query.Handle = open.Handle;
                        query.Type = 6; // KMTQAITYPE_ADAPTERADDRESS (read-only)
                        query.Data = data;
                        query.Size = (uint)Marshal.SizeOf(typeof(AdapterAddress));
                        if (D3DKMTQueryAdapterInfo(ref query) != 0) return false;
                        address = (AdapterAddress)Marshal.PtrToStructure(data, typeof(AdapterAddress));
                        return true;
                    }
                    finally { Marshal.FreeHGlobal(data); }
                }
                catch (EntryPointNotFoundException) { return false; }
                finally
                {
                    if (open.Handle != 0)
                    {
                        CloseAdapter close = new CloseAdapter();
                        close.Handle = open.Handle;
                        D3DKMTCloseAdapter(ref close);
                    }
                }
            }
        }

        private sealed class Nvml : IDisposable
        {
            [StructLayout(LayoutKind.Sequential)] private struct Utilization { public uint Gpu, Memory; }
            [StructLayout(LayoutKind.Sequential)] private struct Memory { public ulong Total, Free, Used; }
            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
            private struct PciInfo
            {
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)] public string LegacyBusId;
                public uint Domain, Bus, Device, PciDeviceId, PciSubSystemId;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string BusId;
            }
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Init();
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Shutdown();
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Count(out uint count);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int HandleByIndex(uint index, out IntPtr device);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetString(IntPtr device, StringBuilder text, uint length);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetPci(IntPtr device, out PciInfo info);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetUtilization(IntPtr device, out Utilization utilization);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetMemory(IntPtr device, out Memory memory);
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibrary(string path);
            [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
            [DllImport("kernel32.dll")] private static extern bool FreeLibrary(IntPtr module);
            private IntPtr library;
            private Shutdown shutdown;
            private Count count;
            private HandleByIndex byIndex;
            private GetString name, uuid;
            private GetPci pci;
            private GetUtilization utilization;
            private GetMemory memory;
            private bool active;

            public static Nvml TryOpen()
            {
                Nvml result = new Nvml();
                try
                {
                    string system = Path.Combine(Environment.SystemDirectory, "nvml.dll");
                    string legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"NVIDIA Corporation\NVSMI\nvml.dll");
                    string path = File.Exists(system) ? system : (File.Exists(legacy) ? legacy : null);
                    if (path == null) return null;
                    result.library = LoadLibrary(path); // Absolute driver path; never a DLL beside the application.
                    if (result.library == IntPtr.Zero) return null;
                    Init init = (Init)result.Load("nvmlInit_v2", typeof(Init));
                    result.shutdown = (Shutdown)result.Load("nvmlShutdown", typeof(Shutdown));
                    result.count = (Count)result.Load("nvmlDeviceGetCount_v2", typeof(Count));
                    result.byIndex = (HandleByIndex)result.Load("nvmlDeviceGetHandleByIndex_v2", typeof(HandleByIndex));
                    result.name = (GetString)result.Load("nvmlDeviceGetName", typeof(GetString));
                    result.uuid = (GetString)result.Load("nvmlDeviceGetUUID", typeof(GetString));
                    result.pci = (GetPci)result.Load("nvmlDeviceGetPciInfo_v3", typeof(GetPci));
                    result.utilization = (GetUtilization)result.Load("nvmlDeviceGetUtilizationRates", typeof(GetUtilization));
                    result.memory = (GetMemory)result.Load("nvmlDeviceGetMemoryInfo", typeof(GetMemory));
                    if (init() != 0) { result.Dispose(); return null; }
                    result.active = true;
                    return result;
                }
                catch { result.Dispose(); return null; }
            }

            private Delegate Load(string export, Type type)
            {
                IntPtr function = GetProcAddress(library, export);
                if (function == IntPtr.Zero) throw new EntryPointNotFoundException(export);
                return Marshal.GetDelegateForFunctionPointer(function, type);
            }

            public void Attach(List<Adapter> adapters)
            {
                uint number;
                if (count(out number) != 0) return;
                for (uint index = 0; index < number; index++)
                {
                    IntPtr device;
                    if (byIndex(index, out device) != 0) continue;
                    PciInfo info;
                    if (pci(device, out info) != 0) continue;
                    Adapter matched = null;
                    foreach (Adapter adapter in adapters)
                    {
                        if (adapter.VendorId == 0x10DE && adapter.Bus == info.Bus && adapter.Device == info.Device && adapter.DeviceId == (info.PciDeviceId >> 16))
                        { matched = adapter; break; }
                    }
                    if (matched == null)
                    {
                        // If PCI lookup is unavailable, attach only an unambiguous
                        // matching hardware model; never guess between identical GPUs.
                        foreach (Adapter adapter in adapters)
                        {
                            if (adapter.VendorId == 0x10DE && adapter.DeviceId == (info.PciDeviceId >> 16) && !adapter.Bus.HasValue)
                            {
                                if (matched != null) { matched = null; break; }
                                matched = adapter;
                            }
                        }
                    }
                    if (matched == null) continue;
                    matched.NvmlDevice = device;
                    StringBuilder text = new StringBuilder(128);
                    if (name(device, text, (uint)text.Capacity) == 0) matched.Name = text.ToString();
                    text.Length = 0;
                    if (uuid(device, text, (uint)text.Capacity) == 0 && text.Length > 0)
                    {
                        matched.Id = "nvml:" + text.ToString();
                        matched.IdentityFallback = false;
                    }
                }
            }

            public double? ReadUtilization(IntPtr device, out int status)
            {
                Utilization value;
                status = utilization(device, out value);
                return status == 0 && value.Gpu <= 100 ? (double?)value.Gpu : null;
            }

            public void ReadMemory(IntPtr device, out long? used, out long? total, out int status)
            {
                Memory value;
                status = memory(device, out value);
                used = status == 0 && value.Used != UInt64.MaxValue ? (long?)ToLong(value.Used) : null;
                total = status == 0 && value.Total > 0 && value.Total != UInt64.MaxValue ? (long?)ToLong(value.Total) : null;
            }

            public void Dispose()
            {
                if (active) { shutdown(); active = false; }
                if (library != IntPtr.Zero) { FreeLibrary(library); library = IntPtr.Zero; }
            }
        }

        private sealed class CounterSnapshot
        {
            public readonly Dictionary<string, double> Utilization = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, double> DedicatedUsage = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            public string EngineError, MemoryError;
        }

        private sealed class GpuCounters : IDisposable
        {
            [StructLayout(LayoutKind.Explicit, Size = 16)]
            private struct FormattedValue { [FieldOffset(0)] public uint Status; [FieldOffset(8)] public double Value; }
            [StructLayout(LayoutKind.Sequential)] private struct FormattedItem { public IntPtr Name; public FormattedValue Value; }
            [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQuery(string source, UIntPtr userData, out IntPtr query);
            [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounter(IntPtr query, string path, UIntPtr userData, out IntPtr counter);
            [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
            [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bytes, out uint count, IntPtr buffer);
            [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
            private static readonly Regex Instance = new Regex(@"(?<gpu>luid_0x[0-9a-f]+_0x[0-9a-f]+)_phys_(?<physical>\d+)(?:_eng_(?<engine>\d+))?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            private IntPtr query, engineCounter, memoryCounter;
            private IntPtr engineBuffer, memoryBuffer;
            private int engineCapacity, memoryCapacity;
            private string engineSetupError, memorySetupError;
            private bool firstSample = true;

            public GpuCounters()
            {
                uint status = PdhOpenQuery(null, UIntPtr.Zero, out query);
                if (status != 0) { engineSetupError = memorySetupError = Hex(status); return; }
                status = PdhAddEnglishCounter(query, @"\GPU Engine(*)\Utilization Percentage", UIntPtr.Zero, out engineCounter);
                if (status != 0) engineSetupError = Hex(status);
                status = PdhAddEnglishCounter(query, @"\GPU Adapter Memory(*)\Dedicated Usage", UIntPtr.Zero, out memoryCounter);
                if (status != 0) memorySetupError = Hex(status);
                PdhCollectQueryData(query); // prime rate counters; no fabricated first value
            }

            public CounterSnapshot Sample()
            {
                CounterSnapshot result = new CounterSnapshot();
                result.EngineError = engineSetupError;
                result.MemoryError = memorySetupError;
                if (query == IntPtr.Zero) return result;
                uint status = PdhCollectQueryData(query);
                if (status != 0)
                {
                    result.EngineError = result.MemoryError = "PDH collect " + Hex(status);
                    return result;
                }
                List<KeyValuePair<string, double>> engines;
                if (firstSample)
                {
                    // Discovery and the first UI update can happen in one tick.
                    // A meaningful rate needs the following sampling interval.
                    firstSample = false;
                    engines = new List<KeyValuePair<string, double>>();
                    if (engineCounter != IntPtr.Zero) result.EngineError = "warming up; waiting for next interval";
                }
                else engines = Read(engineCounter, ref engineBuffer, ref engineCapacity, ref result.EngineError);
                AggregateEngineReadings(engines, result.Utilization);
                List<KeyValuePair<string, double>> memory = Read(memoryCounter, ref memoryBuffer, ref memoryCapacity, ref result.MemoryError);
                foreach (KeyValuePair<string, double> reading in memory)
                {
                    Match match = Instance.Match(reading.Key);
                    if (!match.Success || reading.Value < 0) continue;
                    string gpu = match.Groups["gpu"].Value;
                    double current;
                    result.DedicatedUsage.TryGetValue(gpu, out current);
                    result.DedicatedUsage[gpu] = current + reading.Value;
                }
                return result;
            }

            private static void AggregateEngineReadings(IEnumerable<KeyValuePair<string, double>> engines, Dictionary<string, double> output)
            {
                // A counter is per PROCESS and engine. Sum every PID sharing the
                // same physical engine, then choose the busiest hardware engine.
                Dictionary<string, Dictionary<string, double>> byGpu = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, double> reading in engines)
                {
                    Match match = Instance.Match(reading.Key);
                    if (!match.Success || !match.Groups["engine"].Success || reading.Value < 0) continue;
                    string gpu = match.Groups["gpu"].Value;
                    string engine = match.Groups["physical"].Value + ":" + match.Groups["engine"].Value;
                    Dictionary<string, double> grouped;
                    if (!byGpu.TryGetValue(gpu, out grouped))
                    {
                        grouped = new Dictionary<string, double>();
                        byGpu.Add(gpu, grouped);
                    }
                    double current;
                    grouped.TryGetValue(engine, out current);
                    grouped[engine] = current + reading.Value;
                }
                foreach (KeyValuePair<string, Dictionary<string, double>> gpu in byGpu)
                {
                    double busiest = 0;
                    foreach (double value in gpu.Value.Values) busiest = Math.Max(busiest, value);
                    output[gpu.Key] = Clamp(busiest);
                }
            }

            private static List<KeyValuePair<string, double>> Read(IntPtr counter, ref IntPtr buffer, ref int capacity, ref string error)
            {
                List<KeyValuePair<string, double>> result = new List<KeyValuePair<string, double>>();
                if (counter == IntPtr.Zero) return result;
                uint count, bytes = 0;
                const uint format = 0x200 | 0x8000; // PDH_FMT_DOUBLE | PDH_FMT_NOCAP100
                uint status = PdhGetFormattedCounterArray(counter, format, ref bytes, out count, IntPtr.Zero);
                if (status != 0x800007D2 && status != 0) { error = Hex(status); return result; } // PDH_MORE_DATA
                if (bytes == 0) return result;
                // Instances can change between the size query and the copy. Retry
                // from a fresh size query instead of trusting a failed buffer size.
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    if (bytes > capacity)
                    {
                        if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                        capacity = checked((int)bytes + 4096);
                        buffer = Marshal.AllocHGlobal(capacity);
                    }
                    uint size = (uint)capacity;
                    status = PdhGetFormattedCounterArray(counter, format, ref size, out count, buffer);
                    if (status == 0)
                    {
                        int itemSize = Marshal.SizeOf(typeof(FormattedItem));
                        if ((long)count * itemSize > capacity) { error = "invalid PDH array length"; return result; }
                        for (uint i = 0; i < count; i++)
                        {
                            FormattedItem item = (FormattedItem)Marshal.PtrToStructure(IntPtr.Add(buffer, checked((int)i * itemSize)), typeof(FormattedItem));
                            if (item.Name != IntPtr.Zero && item.Value.Status <= 1 && !Double.IsNaN(item.Value.Value) && !Double.IsInfinity(item.Value.Value))
                                result.Add(new KeyValuePair<string, double>(Marshal.PtrToStringUni(item.Name), item.Value.Value));
                        }
                        error = null;
                        return result;
                    }
                    if (status != 0x800007D2 && status != 0xC0000BBD) break;
                    bytes = 0;
                    PdhGetFormattedCounterArray(counter, format, ref bytes, out count, IntPtr.Zero);
                }
                error = Hex(status);
                return result;
            }

            private static string Hex(uint status) { return "0x" + status.ToString("X8", CultureInfo.InvariantCulture); }
            public void Dispose()
            {
                if (query != IntPtr.Zero) { PdhCloseQuery(query); query = IntPtr.Zero; }
                if (engineBuffer != IntPtr.Zero) { Marshal.FreeHGlobal(engineBuffer); engineBuffer = IntPtr.Zero; }
                if (memoryBuffer != IntPtr.Zero) { Marshal.FreeHGlobal(memoryBuffer); memoryBuffer = IntPtr.Zero; }
            }
        }

        private static class Native
        {
            [StructLayout(LayoutKind.Sequential)]
            public struct FileTime
            {
                public uint Low, High;
                public ulong Value { get { return ((ulong)High << 32) | Low; } }
            }
            [StructLayout(LayoutKind.Sequential)]
            public struct MemoryStatus
            {
                public uint Length, MemoryLoad;
                public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
            }
            [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);
            [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
        }
    }
}

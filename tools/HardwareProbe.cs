using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Threading;
using CodexTaskbarMonitor;

internal static class HardwareProbe
{
    private static string Number(double? value) { return value.HasValue ? value.Value.ToString("F2", CultureInfo.InvariantCulture) : "null"; }
    private static string Bytes(long? value) { return value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "null"; }
    public static int Main(string[] args)
    {
        CheckEngineAggregation();
        int samples = 8;
        bool windowsOnly = false;
        foreach (string arg in args)
        {
            int parsed;
            if (Int32.TryParse(arg, out parsed)) samples = parsed;
            if (arg == "--windows-only") windowsOnly = true;
        }
        samples = Math.Max(2, Math.Min(120, samples));
        double totalMs = 0, maximumMs = 0;
        Process process = Process.GetCurrentProcess();
        TimeSpan startCpu = process.TotalProcessorTime;
        Stopwatch elapsed = Stopwatch.StartNew();
        using (HardwareSampler sampler = new HardwareSampler())
        {
            for (int i = 0; i < samples; i++)
            {
                Stopwatch watch = Stopwatch.StartNew();
                HardwareSnapshot sample = sampler.Sample();
                watch.Stop();
                totalMs += watch.Elapsed.TotalMilliseconds;
                maximumMs = Math.Max(maximumMs, watch.Elapsed.TotalMilliseconds);
                Console.WriteLine("sample={0} elapsed_ms={1:F3} cpu={2} ram={3} ram_total={4} error={5}", i, watch.Elapsed.TotalMilliseconds, Number(sample.CpuPercent), Number(sample.MemoryPercent), sample.MemoryTotalBytes, sample.Error ?? "none");
                foreach (GpuSnapshot gpu in sample.Gpus)
                {
                    Console.WriteLine("  id={0} name={1} gpu={2} dedicated_used={3} dedicated_total={4} source={5}", gpu.Id, gpu.Name, Number(gpu.UtilizationPercent), Bytes(gpu.DedicatedUsedBytes), Bytes(gpu.DedicatedTotalBytes), gpu.Source);
                }
                if (i == 0 && windowsOnly)
                {
                    // Exercise the real Windows fallback on an NVIDIA machine,
                    // without introducing a production configuration/test switch.
                    FieldInfo field = typeof(HardwareSampler).GetField("nvml", BindingFlags.NonPublic | BindingFlags.Instance);
                    IDisposable driver = field.GetValue(sampler) as IDisposable;
                    if (driver != null) driver.Dispose();
                    field.SetValue(sampler, null);
                    Console.WriteLine("probe: NVML detached for Windows-counter fallback validation");
                }
                if (i + 1 < samples) Thread.Sleep(1000);
            }
        }
        process.Refresh();
        Console.WriteLine("summary samples={0} average_ms={1:F3} maximum_ms={2:F3} process_cpu_ms={3:F3} wall_ms={4:F3} private_bytes={5}", samples, totalMs / samples, maximumMs, (process.TotalProcessorTime - startCpu).TotalMilliseconds, elapsed.Elapsed.TotalMilliseconds, process.PrivateMemorySize64);
        return 0;
    }

    private static void CheckEngineAggregation()
    {
        // Two processes sharing engine 0 use 30% + 25%. A separate engine uses
        // 40%. Correct GPU utilization is 55%, neither max-per-PID 40% nor 95%.
        string a = "luid_0x00000000_0x00000001";
        string b = "luid_0x00000000_0x00000002";
        List<KeyValuePair<string, double>> readings = new List<KeyValuePair<string, double>>();
        readings.Add(new KeyValuePair<string, double>("pid_10_" + a + "_phys_0_eng_0_engtype_3D", 30));
        readings.Add(new KeyValuePair<string, double>("pid_20_" + a + "_phys_0_eng_0_engtype_3D", 25));
        readings.Add(new KeyValuePair<string, double>("pid_10_" + a + "_phys_0_eng_1_engtype_Copy", 40));
        readings.Add(new KeyValuePair<string, double>("pid_30_" + a + "_phys_1_eng_0_engtype_3D", 12));
        readings.Add(new KeyValuePair<string, double>("pid_10_" + b + "_phys_0_eng_0_engtype_3D", 70));
        readings.Add(new KeyValuePair<string, double>("pid_20_" + b + "_phys_0_eng_0_engtype_3D", 60));
        Dictionary<string, double> output = new Dictionary<string, double>();
        Type counters = typeof(HardwareSampler).GetNestedType("GpuCounters", BindingFlags.NonPublic);
        MethodInfo aggregate = counters.GetMethod("AggregateEngineReadings", BindingFlags.NonPublic | BindingFlags.Static);
        aggregate.Invoke(null, new object[] { readings, output });
        if (output.Count != 2 || Math.Abs(output[a] - 55) > 0.0001 || Math.Abs(output[b] - 100) > 0.0001)
            throw new InvalidOperationException("GPU engine aggregation failed.");
        Console.WriteLine("aggregation_test=PASS (sum per PID, separate physical engines/adapters, cap 100%)");
    }
}

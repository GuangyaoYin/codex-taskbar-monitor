using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using CodexTaskbarMonitor;

internal static class NetworkProbe
{
    public static int Main(string[] args)
    {
        try
        {
            CheckSyntheticScenarios();
            int count = 8;
            string downloadUrl = null;
            for (int i = 0; i < args.Length; i++)
            {
                int parsed;
                if (Int32.TryParse(args[i], out parsed)) count = parsed;
                if (args[i] == "--download" && i + 1 < args.Length) downloadUrl = args[++i];
            }
            count = Math.Max(2, Math.Min(120, count));
            WindowsNetworkSampleSource inventory = new WindowsNetworkSampleSource();
            NetworkRouteState routes = inventory.GetRoutes();
            foreach (NetworkAdapterInfo adapter in inventory.GetAdapters())
            {
                uint metric;
                string route = routes.Ipv4DefaultMetrics.TryGetValue(adapter.Ipv4Index, out metric) ? metric.ToString(CultureInfo.InvariantCulture) : "none";
                Console.WriteLine("candidate name={0} ipv4_index={1} ipv6_index={2} physical={3} default_metric={4}", adapter.Name, adapter.Ipv4Index, adapter.Ipv6Index, adapter.IsPhysical, route);
            }
            Console.WriteLine("route_best ipv4_index={0} ipv6_index={1}", routes.BestIpv4Index, routes.BestIpv6Index);
            Process process = Process.GetCurrentProcess();
            Stopwatch wall = Stopwatch.StartNew();
            double maximum = 0, total = 0;
            double accumulatedDown = 0, accumulatedUp = 0;
            long lastTimestamp = 0;
            long transferBytes = -1;
            long transferReceivedDelta = -1, transferSentDelta = -1;
            double transferMs = 0;
            string transferError = null;
            Thread transfer = null;
            TimeSpan steadyCpu = TimeSpan.Zero;
            double steadyStartMs = 0;
            using (NetworkSampler sampler = new NetworkSampler())
            {
                for (int i = 0; i < count; i++)
                {
                    Stopwatch watch = Stopwatch.StartNew();
                    NetworkSnapshot sample = sampler.Sample();
                    long timestamp = Stopwatch.GetTimestamp();
                    watch.Stop();
                    maximum = Math.Max(maximum, watch.Elapsed.TotalMilliseconds);
                    total += watch.Elapsed.TotalMilliseconds;
                    if (lastTimestamp != 0 && sample.DownloadBytesPerSecond.HasValue)
                    {
                        double seconds = (timestamp - lastTimestamp) / (double)Stopwatch.Frequency;
                        accumulatedDown += sample.DownloadBytesPerSecond.Value * seconds;
                        accumulatedUp += sample.UploadBytesPerSecond.Value * seconds;
                    }
                    lastTimestamp = timestamp;
                    Console.WriteLine("sample={0} elapsed_ms={1:F3} interface={2} down_Bps={3} up_Bps={4} status={5}", i, watch.Elapsed.TotalMilliseconds, sample.InterfaceName, Number(sample.DownloadBytesPerSecond), Number(sample.UploadBytesPerSecond), sample.StatusMessage);
                    if (i == 0)
                    {
                        process.Refresh();
                        steadyCpu = process.TotalProcessorTime;
                        steadyStartMs = wall.Elapsed.TotalMilliseconds;
                    }
                    if (i == 1 && downloadUrl != null)
                    {
                        // Explicit probe-only traffic: one caller-selected public
                        // small ZIP, retained nowhere and never executed.
                        transfer = new Thread(delegate()
                        {
                            try
                            {
                                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                                NetworkAdapterInfo measured = NetworkSampler.ChooseAdapter(inventory.GetAdapters(), inventory.GetRoutes());
                                NetworkByteCounters before = inventory.GetCounters(measured);
                                Stopwatch transferWatch = Stopwatch.StartNew();
                                using (WebClient client = new WebClient())
                                {
                                    client.Headers[HttpRequestHeader.UserAgent] = "CodexTaskbarMonitor-NetworkValidation";
                                    byte[] payload = client.DownloadData(downloadUrl);
                                    transferBytes = payload.LongLength;
                                }
                                transferMs = transferWatch.Elapsed.TotalMilliseconds;
                                NetworkByteCounters after = inventory.GetCounters(measured);
                                transferReceivedDelta = after.Received - before.Received;
                                transferSentDelta = after.Sent - before.Sent;
                            }
                            catch (Exception ex) { transferError = ex.Message; }
                        });
                        transfer.IsBackground = true;
                        transfer.Start();
                    }
                    if (i + 1 < count) Thread.Sleep(1000);
                }
            }
            if (transfer != null) transfer.Join(5000);
            process.Refresh();
            Console.WriteLine("summary samples={0} average_ms={1:F3} maximum_ms={2:F3} steady_process_cpu_ms={3:F3} steady_wall_ms={4:F3} private_bytes={5} observed_download_bytes={6:F0} observed_upload_bytes={7:F0}", count, total / count, maximum, (process.TotalProcessorTime - steadyCpu).TotalMilliseconds, wall.Elapsed.TotalMilliseconds - steadyStartMs, process.PrivateMemorySize64, accumulatedDown, accumulatedUp);
            if (downloadUrl != null) Console.WriteLine("download_validation url={0} payload_bytes={1} completed={2} transfer_ms={3:F3} physical_received_delta={4} physical_sent_delta={5} error={6}", downloadUrl, transferBytes, transfer != null && !transfer.IsAlive, transferMs, transferReceivedDelta, transferSentDelta, transferError ?? "none");
            return transferError == null && (downloadUrl == null || transferBytes > 0) ? 0 : 2;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static string Number(double? value) { return value.HasValue ? value.Value.ToString("F2", CultureInfo.InvariantCulture) : "null"; }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private static void CheckSyntheticScenarios()
    {
        FakeSource source = new FakeSource();
        NetworkAdapterInfo physical = source.Add("Ethernet", 18, true, 25);
        NetworkAdapterInfo vpn = source.Add("VPN", 8, false, 0);
        source.Routes.BestIpv4Index = 8;
        using (NetworkSampler sampler = new NetworkSampler(source, false))
        {
            NetworkSnapshot first = sampler.Sample();
            Require(first.InterfaceName == "Ethernet" && !first.DownloadBytesPerSecond.HasValue, "First frame/physical-over-VPN selection failed.");
            source.Advance(1, 4096, 2048);
            NetworkSnapshot rate = sampler.Sample();
            Require(rate.DownloadBytesPerSecond == 4096 && rate.UploadBytesPerSecond == 2048, "Byte deltas were not converted to rates.");
            source.Advance(0.25, 1024, 512);
            rate = sampler.Sample();
            Require(rate.DownloadBytesPerSecond == 4096 && rate.UploadBytesPerSecond == 2048, "Actual elapsed interval was ignored.");
            source.Received = 1; source.Sent = 1; source.Advance(1, 0, 0);
            Require(!sampler.Sample().DownloadBytesPerSecond.HasValue, "Counter reset produced a spike.");
            source.Advance(1, 1000, 500);
            Require(sampler.Sample().DownloadBytesPerSecond == 1000, "Counter reset did not recover.");
            source.Advance(60, 100000000, 50000000);
            Require(!sampler.Sample().DownloadBytesPerSecond.HasValue, "Long pause produced a spike.");
            source.Advance(1, 100, 50);
            Require(sampler.Sample().DownloadBytesPerSecond == 100, "Long-pause recovery failed.");
            source.Adapters.Clear(); source.Advance(1, 0, 0);
            Require(!sampler.Sample().DownloadBytesPerSecond.HasValue, "Disconnect produced 0 rather than unknown.");
            source.Adapters.Add(vpn); source.Advance(1, 1000000, 1000000);
            rate = sampler.Sample();
            Require(rate.InterfaceName == "VPN" && !rate.DownloadBytesPerSecond.HasValue && rate.StatusMessage.Contains("回退"), "Virtual-only fallback/reconnect baseline failed.");
            source.Adapters.Add(physical); source.Advance(1, 1000000, 1000000);
            Require(!sampler.Sample().DownloadBytesPerSecond.HasValue, "Adapter switch produced a spike.");
            source.Advance(1, 0, 0);
            Require(sampler.Sample().DownloadBytesPerSecond == 0, "A real idle interval should report 0.");
        }
        Require(WindowsNetworkSampleSource.LooksPhysical(NetworkInterfaceType.Ethernet, "Ethernet", "Intel Ethernet"), "Physical NIC classification failed.");
        Require(WindowsNetworkSampleSource.LooksPhysical(NetworkInterfaceType.Wireless80211, "Wi-Fi", "Intel Wi-Fi 6E"), "Wi-Fi NIC classification failed.");
        Require(!WindowsNetworkSampleSource.LooksPhysical(NetworkInterfaceType.Ethernet, "vEthernet", "Hyper-V Virtual Ethernet Adapter"), "Virtual NIC classified as physical.");
        Require(!WindowsNetworkSampleSource.LooksPhysical((NetworkInterfaceType)53, "Tailscale", "Tailscale Tunnel"), "VPN classified as physical.");
        Console.WriteLine("synthetic_tests=PASS (baseline, real interval, reset, disconnect/reconnect, switch, idle zero, pause, VPN double-count prevention)");
    }

    private sealed class FakeSource : INetworkSampleSource
    {
        public readonly List<NetworkAdapterInfo> Adapters = new List<NetworkAdapterInfo>();
        public readonly NetworkRouteState Routes = new NetworkRouteState();
        public long Received = 10000, Sent = 5000;
        private long timestamp = 100000000;
        private DateTime utc = new DateTime(2026, 10, 30, 0, 0, 0, DateTimeKind.Utc);
        public long Frequency { get { return 10000000; } }
        public DateTime UtcNow { get { return utc; } }
        public long GetTimestamp() { return timestamp; }
        public List<NetworkAdapterInfo> GetAdapters() { return new List<NetworkAdapterInfo>(Adapters); }
        public NetworkRouteState GetRoutes() { return Routes; }
        public NetworkByteCounters GetCounters(NetworkAdapterInfo adapter) { return new NetworkByteCounters { Connected = true, Received = Received, Sent = Sent }; }
        public NetworkAdapterInfo Add(string name, int index, bool physical, uint metric)
        {
            NetworkAdapterInfo value = new NetworkAdapterInfo { Name = name, Key = name + index, Ipv4Index = index, Ipv6Index = index, IsPhysical = physical };
            Adapters.Add(value); Routes.Ipv4DefaultMetrics[index] = metric; return value;
        }
        public void Advance(double seconds, long down, long up)
        { timestamp += (long)(seconds * Frequency); utc = utc.AddSeconds(seconds); Received += down; Sent += up; }
    }
}

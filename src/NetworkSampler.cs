using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;

namespace CodexTaskbarMonitor
{
    public sealed class NetworkSnapshot
    {
        public DateTime TimestampUtc;
        public double? DownloadBytesPerSecond, UploadBytesPerSecond;
        public string InterfaceName;
        public string StatusMessage;
    }

    /// <summary>
    /// Samples one outward-facing interface, never a sum of VPN and physical NICs.
    /// Call on a worker thread. Reads counters and local routing metadata only;
    /// the route lookups do not send packets, resolve DNS, or inspect connections.
    /// </summary>
    public sealed class NetworkSampler : IDisposable
    {
        private readonly object sync = new object();
        private readonly INetworkSampleSource source;
        private readonly bool subscribed;
        private readonly NetworkRateState rates = new NetworkRateState();
        private NetworkRouteState routes;
        private long lastRouteTimestamp;
        private int refreshRoutes = 1;
        private int resetBaseline;
        private string selectedKey;
        private bool disposed;

        public NetworkSampler() : this(new WindowsNetworkSampleSource(), true) { }

        internal NetworkSampler(INetworkSampleSource sampleSource, bool subscribe)
        {
            if (sampleSource == null) throw new ArgumentNullException("sampleSource");
            source = sampleSource;
            if (subscribe)
            {
                NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
                NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
                subscribed = true;
            }
        }

        public NetworkSnapshot Sample()
        {
            lock (sync)
            {
                if (disposed) throw new ObjectDisposedException("NetworkSampler");
                NetworkSnapshot result = new NetworkSnapshot();
                result.TimestampUtc = source.UtcNow;
                result.InterfaceName = String.Empty;
                try
                {
                    // NetworkInterface properties are snapshots. Re-enumerate so
                    // a disconnected selected NIC is not mistaken for an idle NIC.
                    List<NetworkAdapterInfo> adapters = source.GetAdapters();
                    long now = source.GetTimestamp();
                    bool selectedStillPresent = selectedKey == null;
                    foreach (NetworkAdapterInfo item in adapters)
                        if (item.Key == selectedKey) selectedStillPresent = true;
                    bool refreshRequested = Interlocked.Exchange(ref refreshRoutes, 0) != 0;
                    if (routes == null || refreshRequested ||
                        !selectedStillPresent || now < lastRouteTimestamp ||
                        (now - lastRouteTimestamp) / (double)source.Frequency >= 5)
                    {
                        routes = source.GetRoutes();
                        lastRouteTimestamp = now;
                    }
                    NetworkAdapterInfo selected = ChooseAdapter(adapters, routes);
                    if (selected == null)
                    {
                        selectedKey = null;
                        rates.Reset();
                        result.StatusMessage = "没有已连接的外联网卡" + Warning(routes);
                        return result;
                    }
                    selectedKey = selected.Key;
                    result.InterfaceName = selected.Name;
                    NetworkByteCounters counters = source.GetCounters(selected);
                    long timestamp = source.GetTimestamp();
                    DateTime utc = source.UtcNow;
                    result.TimestampUtc = utc;
                    // Windows can reuse an index/name after a reconnect. An
                    // address-change event invalidates that old counter baseline
                    // even when the displayed friendly name did not change.
                    if (Interlocked.Exchange(ref resetBaseline, 0) != 0) rates.Reset();
                    string samplingStatus;
                    rates.Update(selected.Key, counters.Connected, counters.Received, counters.Sent,
                        timestamp, source.Frequency, utc, out result.DownloadBytesPerSecond,
                        out result.UploadBytesPerSecond, out samplingStatus);
                    string policy = selected.IsPhysical
                        ? "物理外联网卡；单接口统计（含局域网和VPN封装流量）"
                        : "虚拟外联网卡回退；未与物理网卡重复累加";
                    result.StatusMessage = policy + (samplingStatus == null ? "" : "；" + samplingStatus) + Warning(routes);
                    if (!counters.Connected) Interlocked.Exchange(ref refreshRoutes, 1);
                }
                catch (Exception ex)
                {
                    rates.Reset();
                    selectedKey = null;
                    Interlocked.Exchange(ref refreshRoutes, 1);
                    result.StatusMessage = "网速采样暂不可用：" + ex.Message;
                }
                return result;
            }
        }

        private static string Warning(NetworkRouteState state)
        {
            return state != null && !String.IsNullOrEmpty(state.Warning) ? "；" + state.Warning : "";
        }

        internal static NetworkAdapterInfo ChooseAdapter(List<NetworkAdapterInfo> adapters, NetworkRouteState state)
        {
            if (state == null) return null;
            List<NetworkAdapterInfo> physical = new List<NetworkAdapterInfo>();
            List<NetworkAdapterInfo> other = new List<NetworkAdapterInfo>();
            foreach (NetworkAdapterInfo adapter in adapters)
                (adapter.IsPhysical ? physical : other).Add(adapter);
            // Prefer a physical NIC that has the ordinary default route. If a VPN
            // wins the OS route lookup, keep measuring its real physical transport.
            NetworkAdapterInfo selected = DefaultChoice(physical, state);
            if (selected != null) return selected;
            selected = FindIndex(physical, state.BestIpv4Index, false);
            if (selected != null) return selected;
            selected = FindIndex(physical, state.BestIpv6Index, true);
            if (selected != null) return selected;
            // IPv6-only uplinks and virtual-only bridge/PPP configurations still
            // work, but are explicitly identified as a fallback in the details.
            selected = FindIndex(other, state.BestIpv4Index, false);
            if (selected != null) return selected;
            selected = DefaultChoice(other, state);
            if (selected != null) return selected;
            return FindIndex(other, state.BestIpv6Index, true);
        }

        private static NetworkAdapterInfo DefaultChoice(List<NetworkAdapterInfo> adapters, NetworkRouteState state)
        {
            NetworkAdapterInfo best = null;
            uint metric = UInt32.MaxValue;
            foreach (NetworkAdapterInfo adapter in adapters)
            {
                uint candidate;
                if (adapter.Ipv4Index <= 0 || !state.Ipv4DefaultMetrics.TryGetValue(adapter.Ipv4Index, out candidate)) continue;
                bool preferred = adapter.Ipv4Index == state.BestIpv4Index;
                bool bestPreferred = best != null && best.Ipv4Index == state.BestIpv4Index;
                if (best == null || (preferred && !bestPreferred) ||
                    (preferred == bestPreferred && (candidate < metric ||
                    (candidate == metric && adapter.Ipv4Index < best.Ipv4Index))))
                { best = adapter; metric = candidate; }
            }
            return best;
        }

        private static NetworkAdapterInfo FindIndex(List<NetworkAdapterInfo> adapters, int index, bool ipv6)
        {
            if (index <= 0) return null;
            foreach (NetworkAdapterInfo adapter in adapters)
                if ((ipv6 ? adapter.Ipv6Index : adapter.Ipv4Index) == index) return adapter;
            return null;
        }

        private void OnNetworkAddressChanged(object sender, EventArgs args)
        {
            Interlocked.Exchange(ref refreshRoutes, 1);
            Interlocked.Exchange(ref resetBaseline, 1);
        }
        private void OnNetworkAvailabilityChanged(object sender, NetworkAvailabilityEventArgs args)
        {
            Interlocked.Exchange(ref refreshRoutes, 1);
            Interlocked.Exchange(ref resetBaseline, 1);
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                if (subscribed)
                {
                    NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
                    NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
                }
                rates.Reset();
            }
        }
    }

    // Kept independent of the Windows reader so reset/reconnect/switch behavior
    // can be tested without changing any real network interfaces or settings.
    internal sealed class NetworkRateState
    {
        private string key;
        private long received, sent, timestamp;
        private DateTime utc;

        public void Reset() { key = null; }

        public void Update(string interfaceKey, bool connected, long currentReceived, long currentSent,
            long now, long frequency, DateTime nowUtc, out double? download, out double? upload, out string status)
        {
            download = upload = null;
            status = null;
            if (!connected || currentReceived < 0 || currentSent < 0 || frequency <= 0)
            {
                Reset();
                status = connected ? "计数器暂不可用" : "网卡已断开";
                return;
            }
            double seconds = (now - timestamp) / (double)frequency;
            bool same = key == interfaceKey;
            if (key == null) status = "等待下一次采样";
            else if (!same) status = "网卡已切换，重新建立基线";
            else if (currentReceived < received || currentSent < sent) status = "计数器已重置，重新建立基线";
            else if (now <= timestamp || seconds > 10 || (nowUtc - utc).TotalSeconds > 10)
                status = "采样暂停后重新建立基线";
            else if (seconds < 0.05)
            {
                // Do not replace the baseline on an accidental rapid duplicate
                // call; let bytes accumulate until there is a useful interval.
                status = "采样间隔过短";
                return;
            }
            else
            {
                download = (currentReceived - received) / seconds;
                upload = (currentSent - sent) / seconds;
            }
            key = interfaceKey;
            received = currentReceived;
            sent = currentSent;
            timestamp = now;
            utc = nowUtc;
        }
    }

    internal sealed class NetworkAdapterInfo
    {
        public string Name, Key;
        public int Ipv4Index, Ipv6Index;
        public bool IsPhysical;
        public NetworkInterface Native;
    }

    internal sealed class NetworkRouteState
    {
        public readonly Dictionary<int, uint> Ipv4DefaultMetrics = new Dictionary<int, uint>();
        public int BestIpv4Index, BestIpv6Index;
        public string Warning;
    }

    internal struct NetworkByteCounters
    {
        public bool Connected;
        public long Received, Sent;
    }

    internal interface INetworkSampleSource
    {
        List<NetworkAdapterInfo> GetAdapters();
        NetworkRouteState GetRoutes();
        NetworkByteCounters GetCounters(NetworkAdapterInfo adapter);
        long GetTimestamp();
        long Frequency { get; }
        DateTime UtcNow { get; }
    }

    internal sealed class WindowsNetworkSampleSource : INetworkSampleSource
    {
        [DllImport("iphlpapi.dll")] private static extern uint GetIpForwardTable(IntPtr table, ref uint bytes, [MarshalAs(UnmanagedType.Bool)] bool sorted);
        [DllImport("iphlpapi.dll")] private static extern uint GetBestInterfaceEx([In] byte[] address, out uint index);
        public long GetTimestamp() { return Stopwatch.GetTimestamp(); }
        public long Frequency { get { return Stopwatch.Frequency; } }
        public DateTime UtcNow { get { return DateTime.UtcNow; } }

        public List<NetworkAdapterInfo> GetAdapters()
        {
            List<NetworkAdapterInfo> result = new List<NetworkAdapterInfo>();
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                int v4 = 0, v6 = 0;
                try
                {
                    IPInterfaceProperties properties = nic.GetIPProperties();
                    try { IPv4InterfaceProperties p = properties.GetIPv4Properties(); if (p != null) v4 = p.Index; } catch (NetworkInformationException) { }
                    try { IPv6InterfaceProperties p = properties.GetIPv6Properties(); if (p != null) v6 = p.Index; } catch (NetworkInformationException) { }
                }
                catch (NetworkInformationException) { continue; }
                if (v4 <= 0 && v6 <= 0) continue;
                NetworkAdapterInfo item = new NetworkAdapterInfo();
                item.Name = nic.Name;
                item.Ipv4Index = v4;
                item.Ipv6Index = v6;
                // Interface indexes and friendly names suffice for a per-process
                // baseline. Do not read GUID IDs, MACs, IPs, or user/account data.
                item.Key = v4 + ":" + v6 + ":" + item.Name;
                item.IsPhysical = LooksPhysical(nic.NetworkInterfaceType, nic.Name, nic.Description);
                item.Native = nic;
                result.Add(item);
            }
            return result;
        }

        internal static bool LooksPhysical(NetworkInterfaceType type, string name, string description)
        {
            int kind = (int)type;
            if (kind != 6 && kind != 26 && kind != 62 && kind != 69 && kind != 71 && kind != 117) return false;
            string text = ((name ?? "") + " " + (description ?? "")).ToLowerInvariant();
            string[] virtualNames = { "virtual", "hyper-v", "vethernet", "vmware", "virtualbox", "tailscale", "tunnel", "tap-windows", "wintun", "wireguard", "vpn", "zerotier", "loopback", "miniport", "wfp", "packet scheduler", "lightweight filter", "筛选", "虚拟" };
            foreach (string marker in virtualNames) if (text.IndexOf(marker, StringComparison.Ordinal) >= 0) return false;
            return true;
        }

        public NetworkByteCounters GetCounters(NetworkAdapterInfo adapter)
        {
            IPInterfaceStatistics statistics = adapter.Native.GetIPStatistics();
            NetworkByteCounters result = new NetworkByteCounters();
            result.Connected = adapter.Native.OperationalStatus == OperationalStatus.Up;
            result.Received = statistics.BytesReceived;
            result.Sent = statistics.BytesSent;
            return result;
        }

        public NetworkRouteState GetRoutes()
        {
            NetworkRouteState result = new NetworkRouteState();
            uint bytes = 0;
            uint error = GetIpForwardTable(IntPtr.Zero, ref bytes, false);
            if ((error == 122 || error == 0) && bytes >= 4 && bytes <= 16 * 1024 * 1024)
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    IntPtr buffer = Marshal.AllocHGlobal(checked((int)bytes));
                    uint capacity = bytes;
                    try
                    {
                        error = GetIpForwardTable(buffer, ref bytes, false);
                        if (error == 122) continue;
                        if (error != 0) break;
                        uint count = unchecked((uint)Marshal.ReadInt32(buffer));
                        const int rowBytes = 56;
                        if (4L + (long)count * rowBytes > capacity) { error = 13; break; }
                        for (uint i = 0; i < count; i++)
                        {
                            IntPtr row = IntPtr.Add(buffer, 4 + checked((int)i * rowBytes));
                            // Read only default-route markers, interface index,
                            // validity and metric. Never inspect gateway addresses.
                            if (Marshal.ReadInt32(row, 0) != 0 || Marshal.ReadInt32(row, 4) != 0 || Marshal.ReadInt32(row, 20) == 2) continue;
                            int index = Marshal.ReadInt32(row, 16);
                            uint metric = unchecked((uint)Marshal.ReadInt32(row, 36));
                            uint old;
                            if (!result.Ipv4DefaultMetrics.TryGetValue(index, out old) || metric < old)
                                result.Ipv4DefaultMetrics[index] = metric;
                        }
                        break;
                    }
                    finally { Marshal.FreeHGlobal(buffer); }
                }
            }
            if (error != 0 && error != 232) result.Warning = "默认路由读取暂不可用（" + error + "）";

            // Pure local route lookups for public destinations; no packets or DNS.
            uint best;
            byte[] ipv4 = new byte[16];
            ipv4[0] = 2; // AF_INET
            ipv4[4] = ipv4[5] = ipv4[6] = ipv4[7] = 1;
            if (GetBestInterfaceEx(ipv4, out best) == 0) result.BestIpv4Index = unchecked((int)best);
            byte[] ipv6 = new byte[28];
            ipv6[0] = 23; // AF_INET6
            byte[] address = IPAddress.Parse("2606:4700:4700::1111").GetAddressBytes();
            Array.Copy(address, 0, ipv6, 8, address.Length);
            if (GetBestInterfaceEx(ipv6, out best) == 0) result.BestIpv6Index = unchecked((int)best);
            return result;
        }
    }
}

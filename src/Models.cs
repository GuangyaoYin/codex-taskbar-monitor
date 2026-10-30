using System;
using System.Collections.Generic;

namespace CodexTaskbarMonitor
{
    public sealed class GpuSnapshot
    {
        public string Id;
        public string Name;
        public string Source;
        public double? UtilizationPercent;
        public long? DedicatedUsedBytes;
        public long? DedicatedTotalBytes;
    }

    public sealed class HardwareSnapshot
    {
        public DateTime TimestampUtc;
        public double? CpuPercent;
        public double? MemoryPercent;
        public long MemoryTotalBytes;
        public long MemoryAvailableBytes;
        public List<GpuSnapshot> Gpus = new List<GpuSnapshot>();
        public string Error;
    }

    public enum CodexStatus { Ready, Missing, LoginRequired, Error }

    public sealed class QuotaWindow
    {
        public double UsedPercent;
        public int WindowDurationMins;
        public DateTime? ResetsAtUtc;
    }

    public sealed class QuotaBucket
    {
        public string Id;
        public string Name;
        public List<QuotaWindow> Windows = new List<QuotaWindow>();
    }

    public sealed class CodexSnapshot
    {
        public CodexStatus Status;
        public string StatusMessage;
        public string CliPath;
        public DateTime AttemptUtc;
        public DateTime? LastSuccessUtc;
        public List<QuotaBucket> Buckets = new List<QuotaBucket>();
        public bool IsStale;
    }
}

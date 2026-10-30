using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace CodexTaskbarMonitor
{
    // This client uses only the documented account/rateLimits/read RPC. It never
    // starts a thread/turn or opens the CLI credential or conversation files.
    public sealed class CodexQuotaClient : IDisposable
    {
        private const int ReadTimeoutMilliseconds = 18000;
        private const int InitializeTimeoutMilliseconds = 5000;
        private readonly object readGate = new object();
        private readonly object processGate = new object();
        private volatile bool disposed;
        private RpcProcess activeProcess;
        private List<QuotaBucket> lastBuckets = new List<QuotaBucket>();
        private DateTime? lastSuccessUtc;
        private string legacyParamsPath;
        internal string LastDiagnostics { get; private set; }

        public CodexSnapshot Read(string customPath)
        {
            lock (readGate)
            {
                LastDiagnostics = "phase=discovery;attempts=0";
                CodexSnapshot snapshot = new CodexSnapshot();
                snapshot.AttemptUtc = DateTime.UtcNow;
                snapshot.LastSuccessUtc = lastSuccessUtc;
                snapshot.Buckets = CloneBuckets(lastBuckets);
                snapshot.IsStale = lastSuccessUtc.HasValue;
                snapshot.CliPath = FindCodexPath(customPath);

                if (disposed)
                    return Fail(snapshot, CodexStatus.Error, "额度读取模块已关闭。");
                if (String.IsNullOrEmpty(snapshot.CliPath))
                    return Fail(snapshot, CodexStatus.Missing, String.IsNullOrWhiteSpace(customPath)
                        ? "未找到 Codex CLI；可查看官方安装说明或手动选择路径。"
                        : "所选 Codex CLI 路径不存在或不是可用的 exe 文件。");

                DateTime deadlineUtc = DateTime.UtcNow.AddMilliseconds(ReadTimeoutMilliseconds);
                for (int attempt = 1; attempt <= 2; attempt++)
                {
                    RpcProcess rpc = null;
                    string phase = "launch";
                    long launchMs = 0, initializeMs = 0, quotaMs = 0;
                    Stopwatch phaseWatch = Stopwatch.StartNew();
                    bool retry = false;
                    try
                    {
                        rpc = new RpcProcess(snapshot.CliPath);
                        launchMs = phaseWatch.ElapsedMilliseconds;
                        lock (processGate)
                        {
                            if (disposed)
                                throw new QuotaException(CodexStatus.Error, "额度读取模块已关闭。");
                            activeProcess = rpc;
                        }
                        phase = "initialize";
                        phaseWatch.Restart();
                        rpc.Send("{\"method\":\"initialize\",\"id\":1,\"params\":{\"clientInfo\":{\"name\":\"codex_taskbar_monitor\",\"title\":\"Codex Taskbar Monitor\",\"version\":\"0.1.0\"}}}");
                        DateTime initializationDeadline = DateTime.UtcNow.AddMilliseconds(InitializeTimeoutMilliseconds);
                        if (initializationDeadline > deadlineUtc) initializationDeadline = deadlineUtc;
                        rpc.ReceiveResult(1, initializationDeadline);
                        initializeMs = phaseWatch.ElapsedMilliseconds;
                        rpc.Send("{\"method\":\"initialized\",\"params\":{}}");
                        phase = "quota";
                        phaseWatch.Restart();
                        bool enhanced = !String.Equals(legacyParamsPath, snapshot.CliPath, StringComparison.OrdinalIgnoreCase);
                        rpc.Send("{\"method\":\"account/rateLimits/read\",\"id\":2"
                            + (enhanced ? ",\"params\":{\"excludeResetCreditDetails\":true}" : "") + "}");
                        Dictionary<string, object> result;
                        try { result = rpc.ReceiveResult(2, deadlineUtc); }
                        catch (QuotaException error)
                        {
                            if (!enhanced || !error.UnsupportedParams) throw;
                            // Older servers can reject the optional optimization.
                            // Retry the same read once without parameters and
                            // remember this capability for the selected CLI path.
                            legacyParamsPath = snapshot.CliPath;
                            rpc.Send("{\"method\":\"account/rateLimits/read\",\"id\":3}");
                            result = rpc.ReceiveResult(3, deadlineUtc);
                        }
                        quotaMs = phaseWatch.ElapsedMilliseconds;
                        List<QuotaBucket> buckets = ParseBuckets(result);
                        if (buckets.Count == 0)
                            throw new QuotaException(CodexStatus.Error, "账户未返回可用额度窗口；请稍后重试。");

                        lastBuckets = CloneBuckets(buckets);
                        lastSuccessUtc = DateTime.UtcNow;
                        snapshot.Status = CodexStatus.Ready;
                        snapshot.StatusMessage = "额度已更新。";
                        snapshot.Buckets = buckets;
                        snapshot.LastSuccessUtc = lastSuccessUtc;
                        snapshot.IsStale = false;
                        RecordDiagnostics(attempt, "complete", launchMs, initializeMs, quotaMs, rpc);
                        return snapshot;
                    }
                    catch (QuotaException exception)
                    {
                        if (phase == "initialize") initializeMs = phaseWatch.ElapsedMilliseconds;
                        if (phase == "quota") quotaMs = phaseWatch.ElapsedMilliseconds;
                        RecordDiagnostics(attempt, phase, launchMs, initializeMs, quotaMs, rpc);
                        // Do not lengthen the previous total 18-second budget.
                        // Only fast transient quota failures get one retry; a
                        // full timeout is left to the host's scheduled retry.
                        retry = phase == "quota" && exception.Retryable && attempt == 1 && !disposed
                            && (deadlineUtc - DateTime.UtcNow).TotalMilliseconds >= 4000;
                        if (!retry)
                        {
                            string message = exception.SafeMessage;
                            if (exception.TimedOut)
                                message = phase == "initialize" ? "Codex 初始化超时，请检查 CLI 或稍后重试。"
                                    : "额度服务器响应超时，请稍后重试。";
                            return Fail(snapshot, exception.Status, message);
                        }
                    }
                    catch (Exception)
                    {
                        // Never expose raw RPC/stderr/exception messages.
                        RecordDiagnostics(attempt, phase, launchMs, initializeMs, phaseWatch.ElapsedMilliseconds, rpc);
                        return Fail(snapshot, rpc != null && rpc.LoginHint ? CodexStatus.LoginRequired : CodexStatus.Error,
                            rpc != null && rpc.LoginHint ? "请使用 Codex 的官方登录流程重新登录 ChatGPT。"
                            : "无法启动 Codex 或读取额度；请检查 CLI 路径、版本及网络。");
                    }
                    finally
                    {
                        lock (processGate)
                        {
                            if (Object.ReferenceEquals(activeProcess, rpc)) activeProcess = null;
                        }
                        if (rpc != null) rpc.Dispose();
                    }
                    if (retry)
                    {
                        for (int pause = 0; pause < 6 && !disposed; pause++) Thread.Sleep(25);
                        if (disposed) return Fail(snapshot, CodexStatus.Error, "额度读取模块已关闭。");
                    }
                }
                return Fail(snapshot, CodexStatus.Error, "额度查询暂不可用，请稍后重试。");
            }
        }

        private void RecordDiagnostics(int attempts, string phase, long launchMs, long initializeMs, long quotaMs, RpcProcess rpc)
        {
            LastDiagnostics = "phase=" + phase + ";attempts=" + attempts + ";launchMs=" + launchMs
                + ";initializeMs=" + initializeMs + ";quotaMs=" + quotaMs + ";legacyParams="
                + !String.IsNullOrEmpty(legacyParamsPath) + ";" + (rpc == null ? "noProcess" : rpc.SafeDiagnostics);
        }

        public static string FindCodexPath(string customPath)
        {
            // An invalid explicit selection is reported rather than silently
            // querying a different installation/account.
            if (!String.IsNullOrWhiteSpace(customPath))
            {
                try
                {
                    string selected = Environment.ExpandEnvironmentVariables(customPath.Trim().Trim('"'));
                    if (Directory.Exists(selected))
                    {
                        string direct = ExistingExe(Path.Combine(selected, "codex.exe"));
                        if (direct.Length > 0) return direct;
                        return ExistingExe(Path.Combine(selected, "bin", "codex.exe"));
                    }
                    return ExistingExe(selected);
                }
                catch (Exception) { return String.Empty; }
            }

            List<string> candidates = new List<string>();
            string path = Environment.GetEnvironmentVariable("PATH") ?? String.Empty;
            foreach (string rawDirectory in path.Split(Path.PathSeparator))
            {
                try
                {
                    string directory = Environment.ExpandEnvironmentVariables(rawDirectory.Trim().Trim('"'));
                    if (directory.Length == 0) continue;
                    candidates.Add(Path.Combine(directory, "codex.exe"));
                    AddNpmCandidates(candidates, directory);
                }
                catch (Exception) { }
            }

            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            candidates.Add(Path.Combine(local, "Programs", "OpenAI", "Codex", "bin", "codex.exe"));
            candidates.Add(Path.Combine(local, "OpenAI", "Codex", "bin", "codex.exe"));
            AddVersionedCandidates(candidates, Path.Combine(local, "OpenAI", "Codex", "bin"));
            candidates.Add(Path.Combine(local, "Microsoft", "WindowsApps", "codex.exe"));
            AddNpmCandidates(candidates, Path.Combine(roaming, "npm"));
            string codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (String.IsNullOrWhiteSpace(codexHome)) codexHome = Path.Combine(profile, ".codex");
            candidates.Add(Path.Combine(codexHome, "packages", "standalone", "current", "bin", "codex.exe"));
            candidates.Add(Path.Combine(codexHome, "packages", "standalone", "current", "codex.exe"));

            foreach (string candidate in candidates)
            {
                string existing = ExistingExe(candidate);
                if (existing.Length > 0) return existing;
            }
            return String.Empty;
        }

        private static void AddVersionedCandidates(List<string> candidates, string directory)
        {
            try
            {
                DirectoryInfo[] versions = new DirectoryInfo(directory).GetDirectories();
                Array.Sort(versions, delegate(DirectoryInfo left, DirectoryInfo right)
                { return right.LastWriteTimeUtc.CompareTo(left.LastWriteTimeUtc); });
                foreach (DirectoryInfo version in versions)
                    candidates.Add(Path.Combine(version.FullName, "codex.exe"));
            }
            catch (Exception) { }
        }

        private static void AddNpmCandidates(List<string> candidates, string directory)
        {
            const string target = "x86_64-pc-windows-msvc";
            string package = Path.Combine(directory, "node_modules", "@openai", "codex");
            candidates.Add(Path.Combine(package, "vendor", target, "codex", "codex.exe"));
            candidates.Add(Path.Combine(package, "node_modules", "@openai", "codex-win32-x64", "vendor", target, "codex", "codex.exe"));
            candidates.Add(Path.Combine(directory, "node_modules", "@openai", "codex-win32-x64", "vendor", target, "codex", "codex.exe"));
        }

        private static string ExistingExe(string candidate)
        {
            try
            {
                if (!String.Equals(Path.GetExtension(candidate), ".exe", StringComparison.OrdinalIgnoreCase))
                    return String.Empty;
                return File.Exists(candidate) ? Path.GetFullPath(candidate) : String.Empty;
            }
            catch (Exception) { return String.Empty; }
        }

        private static CodexSnapshot Fail(CodexSnapshot snapshot, CodexStatus status, string message)
        {
            snapshot.Status = status;
            snapshot.StatusMessage = message;
            snapshot.IsStale = true;
            return snapshot;
        }

        private static List<QuotaBucket> ParseBuckets(Dictionary<string, object> result)
        {
            List<QuotaBucket> buckets = new List<QuotaBucket>();
            Dictionary<string, object> byId = AsObject(Value(result, "rateLimitsByLimitId"));
            if (byId != null)
            {
                foreach (KeyValuePair<string, object> entry in byId)
                    AddBucket(buckets, AsObject(entry.Value), entry.Key);
            }
            if (buckets.Count == 0) AddBucket(buckets, AsObject(Value(result, "rateLimits")), "codex");
            buckets.Sort(delegate(QuotaBucket left, QuotaBucket right)
            {
                if (left.Id == "codex" && right.Id != "codex") return -1;
                if (right.Id == "codex" && left.Id != "codex") return 1;
                return StringComparer.Ordinal.Compare(left.Id, right.Id);
            });
            return buckets;
        }

        private static void AddBucket(List<QuotaBucket> buckets, Dictionary<string, object> source, string fallbackId)
        {
            if (source == null) return;
            QuotaBucket bucket = new QuotaBucket();
            bucket.Id = Text(Value(source, "limitId"));
            if (String.IsNullOrWhiteSpace(bucket.Id)) bucket.Id = fallbackId;
            bucket.Name = Text(Value(source, "limitName"));
            if (String.IsNullOrWhiteSpace(bucket.Name)) bucket.Name = bucket.Id;
            AddWindow(bucket.Windows, AsObject(Value(source, "primary")));
            AddWindow(bucket.Windows, AsObject(Value(source, "secondary")));
            if (bucket.Windows.Count > 0) buckets.Add(bucket);
        }

        private static void AddWindow(List<QuotaWindow> windows, Dictionary<string, object> source)
        {
            if (source == null) return;
            double used;
            if (!Number(Value(source, "usedPercent"), out used)) return;
            QuotaWindow window = new QuotaWindow();
            window.UsedPercent = Math.Max(0, Math.Min(100, used));
            double duration;
            if (Number(Value(source, "windowDurationMins"), out duration) && duration > 0
                && duration <= Int32.MaxValue && duration == Math.Truncate(duration))
                window.WindowDurationMins = (int)duration;
            // A zero duration means an unknown window, never an assumed 5h/week.
            double reset;
            if (Number(Value(source, "resetsAt"), out reset))
            {
                try { window.ResetsAtUtc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(reset); }
                catch (ArgumentOutOfRangeException) { }
            }
            windows.Add(window);
        }

        private static List<QuotaBucket> CloneBuckets(List<QuotaBucket> source)
        {
            List<QuotaBucket> copy = new List<QuotaBucket>();
            foreach (QuotaBucket bucket in source)
            {
                QuotaBucket cloned = new QuotaBucket { Id = bucket.Id, Name = bucket.Name };
                foreach (QuotaWindow window in bucket.Windows)
                    cloned.Windows.Add(new QuotaWindow { UsedPercent = window.UsedPercent,
                        WindowDurationMins = window.WindowDurationMins, ResetsAtUtc = window.ResetsAtUtc });
                copy.Add(cloned);
            }
            return copy;
        }

        private static object Value(Dictionary<string, object> source, string key)
        {
            object value;
            return source != null && source.TryGetValue(key, out value) ? value : null;
        }
        private static Dictionary<string, object> AsObject(object value) { return value as Dictionary<string, object>; }
        private static string Text(object value) { return value as string; }
        private static bool Number(object value, out double number)
        {
            number = 0;
            if (value == null || value is bool) return false;
            return Double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float,
                CultureInfo.InvariantCulture, out number) && !Double.IsNaN(number) && !Double.IsInfinity(number);
        }
        private static bool IsLoginError(string message)
        {
            if (String.IsNullOrEmpty(message)) return false;
            string lower = message.ToLowerInvariant();
            string[] markers = { "not logged in", "not signed in", "not authenticated", "unauthorized",
                "authentication required", "requires chatgpt", "chatgpt authentication", "login required",
                "please log in", "please login", "codex login", "no access token", "invalid_grant", "token expired" };
            foreach (string marker in markers) if (lower.Contains(marker)) return true;
            return false;
        }

        public void Dispose()
        {
            disposed = true;
            lock (processGate) { if (activeProcess != null) activeProcess.Dispose(); }
        }

        private sealed class QuotaException : Exception
        {
            public readonly CodexStatus Status;
            public readonly string SafeMessage;
            public readonly bool Retryable, UnsupportedParams, TimedOut;
            public QuotaException(CodexStatus status, string safeMessage, bool retryable = false,
                bool unsupportedParams = false, bool timedOut = false)
            { Status = status; SafeMessage = safeMessage; Retryable = retryable; UnsupportedParams = unsupportedParams; TimedOut = timedOut; }
        }

        private sealed class RpcProcess : IDisposable
        {
            private Process process;
            private AnonymousPipeServerStream inputPipe, outputPipe, errorPipe;
            private StreamWriter input;
            private StreamReader output, errorOutput;
            private Thread outputThread, errorThread;
            private readonly Queue<string> lines = new Queue<string>();
            private readonly object lineGate = new object();
            private readonly AutoResetEvent changed = new AutoResetEvent(false);
            private readonly JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = 1048576, RecursionLimit = 64 };
            private IntPtr job;
            private int isDisposed;
            private bool stdoutClosed;
            private volatile bool loginHint;
            private volatile bool networkHint, tlsHint, proxyHint, configurationHint;
            private volatile bool resetDetailsTimeoutHint;
            private int stderrLineCount;
            public bool LoginHint { get { return loginHint; } }
            public string SafeDiagnostics
            {
                get { return "stderrLines=" + Interlocked.CompareExchange(ref stderrLineCount, 0, 0)
                    + ";auth=" + loginHint + ";network=" + networkHint + ";tls=" + tlsHint
                    + ";proxy=" + proxyHint + ";configuration=" + configurationHint
                    + ";resetDetailsTimeout=" + resetDetailsTimeoutHint; }
            }

            public RpcProcess(string executable)
            {
                NativeProcessInformation native = new NativeProcessInformation();
                try
                {
                    job = CreateJobObject(IntPtr.Zero, null);
                    if (job == IntPtr.Zero) throw new InvalidOperationException();
                    JobExtendedLimitInformation limits = new JobExtendedLimitInformation();
                    limits.BasicLimitInformation.LimitFlags = 0x00002000; // KILL_ON_JOB_CLOSE
                    if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf(typeof(JobExtendedLimitInformation))))
                        throw new InvalidOperationException();
                    inputPipe = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
                    outputPipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
                    errorPipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
                    NativeStartupInfo startup = new NativeStartupInfo();
                    startup.Size = Marshal.SizeOf(typeof(NativeStartupInfo));
                    startup.Flags = 0x00000100; // STARTF_USESTDHANDLES
                    startup.StandardInput = inputPipe.ClientSafePipeHandle.DangerousGetHandle();
                    startup.StandardOutput = outputPipe.ClientSafePipeHandle.DangerousGetHandle();
                    startup.StandardError = errorPipe.ClientSafePipeHandle.DangerousGetHandle();
                    // Suspended startup closes the launcher race: no child code
                    // can create a helper before the whole tree belongs to our job.
                    StringBuilder command = new StringBuilder("\"" + executable + "\" app-server --listen stdio://");
                    if (!CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, true,
                        0x08000004, IntPtr.Zero, Path.GetTempPath(), ref startup, out native))
                        throw new InvalidOperationException(); // CREATE_NO_WINDOW | CREATE_SUSPENDED
                    process = Process.GetProcessById(native.ProcessId);
                    IntPtr processHandle = process.Handle; // Open our own handle before resuming.
                    if (!AssignProcessToJobObject(job, processHandle)) throw new InvalidOperationException();
                    inputPipe.DisposeLocalCopyOfClientHandle();
                    outputPipe.DisposeLocalCopyOfClientHandle();
                    errorPipe.DisposeLocalCopyOfClientHandle();
                    input = new StreamWriter(inputPipe, new UTF8Encoding(false));
                    input.AutoFlush = true;
                    output = new StreamReader(outputPipe, new UTF8Encoding(false));
                    errorOutput = new StreamReader(errorPipe, new UTF8Encoding(false));
                    if (ResumeThread(native.ThreadHandle) == UInt32.MaxValue) throw new InvalidOperationException();
                    outputThread = new Thread(ReadOutput) { IsBackground = true, Name = "CodexQuota stdout" };
                    errorThread = new Thread(ReadError) { IsBackground = true, Name = "CodexQuota stderr" };
                    outputThread.Start();
                    errorThread.Start();
                }
                catch
                {
                    // Assignment failure still leaves a suspended, owned child;
                    // terminate it even if it never entered the job.
                    if (native.ProcessHandle != IntPtr.Zero) TerminateProcess(native.ProcessHandle, 1);
                    Dispose();
                    throw;
                }
                finally
                {
                    if (native.ThreadHandle != IntPtr.Zero) CloseHandle(native.ThreadHandle);
                    if (native.ProcessHandle != IntPtr.Zero) CloseHandle(native.ProcessHandle);
                }
            }

            private void Signal()
            {
                try { changed.Set(); } catch (ObjectDisposedException) { }
            }
            private void ReadOutput()
            {
                try
                {
                    string line;
                    while ((line = output.ReadLine()) != null)
                    {
                        lock (lineGate)
                        {
                            if (line.Length <= 1048576)
                            {
                                if (lines.Count >= 256) lines.Dequeue();
                                lines.Enqueue(line);
                            }
                        }
                        Signal();
                    }
                }
                catch (Exception) { }
                finally
                {
                    lock (lineGate) { stdoutClosed = true; }
                    Signal();
                }
            }
            private void ReadError()
            {
                // Retain only a category flag, never stderr text or credentials.
                try
                {
                    string line;
                    while ((line = errorOutput.ReadLine()) != null)
                    {
                        Interlocked.Increment(ref stderrLineCount);
                        if (IsLoginError(line)) loginHint = true;
                        string lower = line.ToLowerInvariant();
                        if (lower.Contains("timed out") || lower.Contains("timeout") || lower.Contains("error sending request")
                            || lower.Contains("failed to connect") || lower.Contains("dns error") || lower.Contains("connection reset")
                            || lower.Contains("connection closed") || lower.Contains("network is unreachable")) networkHint = true;
                        if (lower.Contains("tls") || lower.Contains("certificate")) tlsHint = true;
                        if (lower.Contains("proxy")) proxyHint = true;
                        if (lower.Contains("failed to load config") || lower.Contains("invalid config")) configurationHint = true;
                        if (lower.Contains("reset credit detail") && (lower.Contains("timed out") || lower.Contains("timeout")))
                            resetDetailsTimeoutHint = true;
                    }
                }
                catch (Exception) { }
            }

            public void Send(string message)
            {
                input.WriteLine(message);
                input.Flush();
            }

            public Dictionary<string, object> ReceiveResult(int requestId, DateTime deadlineUtc)
            {
                while (true)
                {
                    if (DateTime.UtcNow >= deadlineUtc)
                        throw new QuotaException(loginHint ? CodexStatus.LoginRequired : CodexStatus.Error,
                            loginHint ? "请使用 Codex 的官方登录流程重新登录 ChatGPT。" : "额度查询超时，请稍后重试。",
                            false, false, !loginHint);
                    string line = null;
                    bool closed;
                    lock (lineGate)
                    {
                        if (lines.Count > 0) line = lines.Dequeue();
                        closed = stdoutClosed;
                    }
                    if (line != null)
                    {
                        Dictionary<string, object> message;
                        try { message = AsObject(serializer.DeserializeObject(line)); }
                        catch (Exception) { continue; }
                        double id;
                        if (message == null || !Number(Value(message, "id"), out id) || id != requestId) continue;
                        Dictionary<string, object> error = AsObject(Value(message, "error"));
                        if (error != null)
                        {
                            double code;
                            string messageText = Text(Value(error, "message")) ?? String.Empty;
                            string lower = messageText.ToLowerInvariant();
                            bool numericCode = Number(Value(error, "code"), out code);
                            bool authentication = IsLoginError(messageText)
                                || (Number(Value(error, "code"), out code) && code == 401);
                            bool unsupported = !authentication && (numericCode && code == -32602
                                || lower.Contains("unknown field") || lower.Contains("unknown parameter")
                                || lower.Contains("invalid params") || lower.Contains("invalid parameter"));
                            bool transient = !authentication && !unsupported && (numericCode && (code == 502 || code == 503 || code == 504)
                                || lower.Contains("error sending request") || lower.Contains("failed to connect")
                                || lower.Contains("connection reset") || lower.Contains("connection closed")
                                || lower.Contains("timed out") || lower.Contains("temporarily unavailable")
                                || lower.Contains("502 bad gateway") || lower.Contains("503 service unavailable")
                                || lower.Contains("504 gateway timeout"));
                            throw new QuotaException(authentication ? CodexStatus.LoginRequired : CodexStatus.Error,
                                authentication ? "请使用 Codex 的官方登录流程重新登录 ChatGPT。" : "Codex 未能返回额度；请检查版本或稍后重试。",
                                transient, unsupported);
                        }
                        Dictionary<string, object> result = AsObject(Value(message, "result"));
                        if (result == null) throw new QuotaException(CodexStatus.Error, "Codex 返回了无法识别的额度数据。");
                        return result;
                    }
                    if (closed || Interlocked.CompareExchange(ref isDisposed, 0, 0) != 0)
                        throw new QuotaException(loginHint ? CodexStatus.LoginRequired : CodexStatus.Error,
                            loginHint ? "请使用 Codex 的官方登录流程重新登录 ChatGPT。" : "Codex 额度进程提前退出；请检查 CLI 版本。");
                    int remaining = (int)Math.Ceiling((deadlineUtc - DateTime.UtcNow).TotalMilliseconds);
                    if (remaining <= 0)
                        throw new QuotaException(loginHint ? CodexStatus.LoginRequired : CodexStatus.Error,
                            loginHint ? "请使用 Codex 的官方登录流程重新登录 ChatGPT。" : "额度查询超时，请稍后重试。",
                            false, false, !loginHint);
                    changed.WaitOne(Math.Min(remaining, 250));
                }
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref isDisposed, 1) != 0) return;
                Signal();
                try { if (job != IntPtr.Zero) CloseHandle(job); } catch (Exception) { }
                job = IntPtr.Zero;
                try
                {
                    if (process != null && !process.HasExited)
                    {
                        if (!process.WaitForExit(300)) { process.Kill(); process.WaitForExit(200); }
                    }
                }
                catch (Exception) { }
                CloseResource(input);
                CloseResource(output);
                CloseResource(errorOutput);
                CloseResource(inputPipe);
                CloseResource(outputPipe);
                CloseResource(errorPipe);
                if (outputThread != null && outputThread.IsAlive) outputThread.Join(50);
                if (errorThread != null && errorThread.IsAlive) errorThread.Join(50);
                CloseResource(process);
                changed.Dispose();
            }

            private static void CloseResource(IDisposable resource)
            {
                try { if (resource != null) resource.Dispose(); } catch (Exception) { }
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NativeStartupInfo
        {
            public int Size;
            public string Reserved, Desktop, Title;
            public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
            public ushort ShowWindow, ReservedSize;
            public IntPtr ReservedData, StandardInput, StandardOutput, StandardError;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeProcessInformation
        {
            public IntPtr ProcessHandle, ThreadHandle;
            public int ProcessId, ThreadId;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateProcess(string applicationName, StringBuilder commandLine,
            IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
            uint creationFlags, IntPtr environment, string currentDirectory, ref NativeStartupInfo startup,
            out NativeProcessInformation processInformation);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateProcess(IntPtr process, uint exitCode);

        [StructLayout(LayoutKind.Sequential)]
        private struct JobBasicLimitInformation
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct JobExtendedLimitInformation
        {
            public JobBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr jobAttributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(IntPtr job, int informationClass, ref JobExtendedLimitInformation information, uint length);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}

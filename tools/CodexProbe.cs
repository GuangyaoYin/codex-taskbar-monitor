using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using CodexTaskbarMonitor;

// Standalone validation harness. Fake mode is selected only in this test
// executable's process environment; it never changes persistent user settings.
internal static class CodexProbe
{
    private const string ScenarioVariable = "CODEX_MONITOR_PROBE_SCENARIO";
    private const string MarkerVariable = "CODEX_MONITOR_PROBE_MARKER";
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static int passed;

    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "app-server") return FakeServer();
        if (args.Length > 0 && args[0] == "--helper") { Thread.Sleep(60000); return 0; }
        try
        {
            if (args.Length > 0 && args[0] == "--real") return RealProbe();
            RunSuite();
            Console.WriteLine("PASS " + passed + " fixture assertions");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL " + error.Message);
            return 1;
        }
        finally
        {
            Environment.SetEnvironmentVariable(ScenarioVariable, null);
            Environment.SetEnvironmentVariable(MarkerVariable, null);
        }
    }

    private static void Check(bool value, string name)
    {
        if (!value) throw new InvalidOperationException(name);
        passed++;
        Console.WriteLine("PASS " + name);
    }

    private static string Self { get { return Assembly.GetExecutingAssembly().Location; } }
    private static void Scenario(string scenario) { Environment.SetEnvironmentVariable(ScenarioVariable, scenario); }

    private static void RunSuite()
    {
        Check(CodexQuotaClient.FindCodexPath(Self) == Self, "explicit executable path");
        Check(CodexQuotaClient.FindCodexPath(Self + ".missing") == String.Empty, "invalid explicit path does not fall back");
        using (CodexQuotaClient client = new CodexQuotaClient())
        {
            CodexSnapshot firstFailure = client.Read(Self + ".missing");
            Check(firstFailure.Status == CodexStatus.Missing && firstFailure.IsStale
                && !firstFailure.LastSuccessUtc.HasValue && firstFailure.Buckets.Count == 0,
                "first failure is marked stale without fabricating a prior success");
            Scenario("week");
            CodexSnapshot week = client.Read(Self);
            Check(week.Status == CodexStatus.Ready, "week-only RPC succeeds despite extra lines and notifications");
            Check(week.Buckets.Count == 1 && week.Buckets[0].Windows.Count == 1, "null secondary is omitted");
            Check(week.Buckets[0].Windows[0].WindowDurationMins == 10080 && week.Buckets[0].Windows[0].UsedPercent == 43,
                "primary duration is a week, never assumed to be 5h");
            Check(week.Buckets[0].Windows[0].ResetsAtUtc.HasValue && week.Buckets[0].Windows[0].ResetsAtUtc.Value.Kind == DateTimeKind.Utc,
                "Unix reset timestamp becomes UTC");
            Check(week.LastSuccessUtc.HasValue && !week.IsStale, "successful snapshot records freshness");
            DateTime? success = week.LastSuccessUtc;
            week.Buckets[0].Windows[0].UsedPercent = 99;
            Scenario("login");
            CodexSnapshot login = client.Read(Self);
            Check(login.Status == CodexStatus.LoginRequired, "unauthenticated RPC is categorized");
            Check(login.IsStale && login.LastSuccessUtc == success && login.Buckets[0].Windows[0].UsedPercent == 43,
                "login failure preserves independent last successful data");
            CodexSnapshot missing = client.Read(Self + ".missing");
            Check(missing.Status == CodexStatus.Missing && missing.IsStale && missing.LastSuccessUtc == success,
                "missing executable preserves last success");
            Scenario("multi");
            CodexSnapshot multi = client.Read(Self);
            Check(multi.Status == CodexStatus.Ready && multi.Buckets.Count == 2, "multiple quota buckets are retained");
            Check(multi.Buckets[0].Id == "codex" && multi.Buckets[0].Windows.Count == 2 && multi.Buckets[1].Windows[0].WindowDurationMins == 60,
                "5h week and additional durations are retained");
            Scenario("missing-fields");
            CodexSnapshot partial = client.Read(Self);
            Check(partial.Status == CodexStatus.Ready && partial.Buckets[0].Windows.Count == 1,
                "missing usage is omitted rather than treated as zero");
            Check(partial.Buckets[0].Windows[0].WindowDurationMins == 0 && !partial.Buckets[0].Windows[0].ResetsAtUtc.HasValue,
                "missing duration and reset remain unknown");
            Scenario("empty");
            CodexSnapshot empty = client.Read(Self);
            Check(empty.Status == CodexStatus.Error && empty.IsStale, "empty limits are not fabricated as fresh data");
            Scenario("error");
            CodexSnapshot error = client.Read(Self);
            Check(error.Status == CodexStatus.Error && error.IsStale && !error.StatusMessage.Contains("FAKE_PRIVATE"),
                "raw server errors are not exposed");
            Scenario("legacy");
            CodexSnapshot legacy = client.Read(Self);
            Check(legacy.Status == CodexStatus.Ready && legacy.Buckets[0].Windows[0].WindowDurationMins == 300,
                "legacy single-bucket response works");
        }

        string marker = Path.Combine(Path.GetTempPath(), "codex-quota-probe-" + Guid.NewGuid().ToString("N") + ".txt");
        Environment.SetEnvironmentVariable(MarkerVariable, marker);
        try
        {
            using (CodexQuotaClient client = new CodexQuotaClient())
            {
                Scenario("early-helper");
                CodexSnapshot earlyHelper = client.Read(Self);
                int helperId = Int32.Parse(File.ReadAllText(marker));
                Check(earlyHelper.Status == CodexStatus.Ready && WaitUntilExited(helperId, 2000),
                    "helper spawned before initialization belongs to the owned job");
            }
            File.Delete(marker);
            using (CodexQuotaClient client = new CodexQuotaClient())
            {
                Scenario("helper");
                CodexSnapshot helper = client.Read(Self);
                int helperId = Int32.Parse(File.ReadAllText(marker));
                Check(helper.Status == CodexStatus.Ready && WaitUntilExited(helperId, 2000),
                    "successful query cleans up owned helper process");
            }
            File.Delete(marker);
            using (CodexQuotaClient client = new CodexQuotaClient())
            {
                Scenario("timeout");
                Stopwatch timer = Stopwatch.StartNew();
                CodexSnapshot timeout = client.Read(Self);
                timer.Stop();
                int helperId = Int32.Parse(File.ReadAllText(marker));
                Check(timeout.Status == CodexStatus.Error && timeout.StatusMessage.Contains("超时")
                    && timer.ElapsedMilliseconds >= 17000 && timer.ElapsedMilliseconds < 22000,
                    "query timeout is bounded at about 18 seconds");
                Check(WaitUntilExited(helperId, 2000), "timeout cleans up owned helper process");
            }
            File.Delete(marker);
            CodexQuotaClient cancelClient = new CodexQuotaClient();
            try
            {
                Scenario("timeout");
                Task<CodexSnapshot> read = Task.Factory.StartNew(delegate { return cancelClient.Read(Self); });
                Stopwatch launch = Stopwatch.StartNew();
                while (!File.Exists(marker) && launch.ElapsedMilliseconds < 5000) Thread.Sleep(20);
                Check(File.Exists(marker), "cancellation fixture reached child startup");
                int helperId = Int32.Parse(File.ReadAllText(marker));
                Stopwatch close = Stopwatch.StartNew();
                cancelClient.Dispose();
                close.Stop();
                Check(close.ElapsedMilliseconds < 1500, "Dispose returns quickly without waiting for read timeout");
                Check(read.Wait(2000) && read.Result.Status == CodexStatus.Error, "Dispose cancels pending query");
                Check(WaitUntilExited(helperId, 2000), "Dispose cleans up owned helper process");
            }
            finally { cancelClient.Dispose(); }
        }
        finally
        {
            Environment.SetEnvironmentVariable(MarkerVariable, null);
            if (File.Exists(marker)) File.Delete(marker);
        }
    }

    private static bool WaitUntilExited(int id, int timeout)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < timeout)
        {
            try { using (Process process = Process.GetProcessById(id)) { if (process.HasExited) return true; } }
            catch (ArgumentException) { return true; }
            Thread.Sleep(20);
        }
        return false;
    }

    private static int RealProbe()
    {
        Environment.SetEnvironmentVariable(ScenarioVariable, null);
        using (CodexQuotaClient client = new CodexQuotaClient())
        {
            CodexSnapshot snapshot = client.Read(null);
            Console.WriteLine("Status=" + snapshot.Status + "; stale=" + snapshot.IsStale + "; buckets=" + snapshot.Buckets.Count);
            foreach (QuotaBucket bucket in snapshot.Buckets)
                foreach (QuotaWindow window in bucket.Windows)
                    Console.WriteLine("durationMins=" + window.WindowDurationMins + "; usedPercent=" + window.UsedPercent
                        + "; resetUtc=" + (window.ResetsAtUtc.HasValue ? window.ResetsAtUtc.Value.ToString("o") : "unknown"));
            if (snapshot.Status != CodexStatus.Ready) Console.WriteLine(snapshot.StatusMessage);
            return snapshot.Status == CodexStatus.Ready ? 0 : 2;
        }
    }

    private static int FakeServer()
    {
        string scenario = Environment.GetEnvironmentVariable(ScenarioVariable);
        if (String.IsNullOrEmpty(scenario)) return 3;
        if (scenario == "early-helper") SpawnHelper();
        string line;
        while ((line = Console.ReadLine()) != null)
        {
            Dictionary<string, object> request = Json.DeserializeObject(line) as Dictionary<string, object>;
            string method = (string)request["method"];
            if (method == "initialize") Console.WriteLine("{\"id\":1,\"result\":{}}");
            else if (method == "initialized") { }
            else if (method == "account/rateLimits/read")
            {
                // A pipe-filling stderr burst verifies that the client drains it.
                for (int i = 0; i < 100; i++) Console.Error.WriteLine(new string('x', 1000));
                if (scenario == "helper" || scenario == "timeout")
                    SpawnHelper();
                if (scenario == "timeout") { Thread.Sleep(60000); return 0; }
                Console.WriteLine("non-JSON startup line");
                Console.WriteLine("{\"method\":\"account/rateLimits/updated\",\"params\":{}}");
                Console.WriteLine("{\"id\":999,\"result\":{}}");
                if (scenario == "login") Console.WriteLine("{\"id\":2,\"error\":{\"code\":-32000,\"message\":\"Not logged in. Please run codex login.\"}}");
                else if (scenario == "error") Console.WriteLine("{\"id\":2,\"error\":{\"code\":-32603,\"message\":\"FAKE_PRIVATE_ACCOUNT_IDENTIFIER\"}}");
                else if (scenario == "empty") Console.WriteLine("{\"id\":2,\"result\":{\"rateLimits\":null,\"rateLimitsByLimitId\":null}}");
                else if (scenario == "missing-fields") Console.WriteLine("{\"id\":2,\"result\":{\"rateLimits\":{\"primary\":{\"windowDurationMins\":10080},\"secondary\":{\"usedPercent\":45}}}}");
                else if (scenario == "legacy") Console.WriteLine("{\"id\":2,\"result\":{\"rateLimits\":{\"primary\":{\"usedPercent\":20,\"windowDurationMins\":300}}}}");
                else if (scenario == "multi") Console.WriteLine("{\"id\":2,\"result\":{\"rateLimitsByLimitId\":{\"codex_other\":{\"limitName\":\"Other\",\"primary\":{\"usedPercent\":12,\"windowDurationMins\":60}},\"codex\":{\"primary\":{\"usedPercent\":20,\"windowDurationMins\":300},\"secondary\":{\"usedPercent\":43,\"windowDurationMins\":10080}}}}}");
                else Console.WriteLine("{\"id\":2,\"result\":{\"rateLimitsByLimitId\":{\"codex\":{\"primary\":{\"usedPercent\":43,\"windowDurationMins\":10080,\"resetsAt\":1730947200},\"secondary\":null}}}}");
                Console.Out.Flush();
            }
            else return 9; // Any thread/turn/model request makes the fixture fail.
            Console.Out.Flush();
        }
        return 0;
    }

    private static void SpawnHelper()
    {
        using (Process helper = Process.Start(new ProcessStartInfo(Self, "--helper")
        { UseShellExecute = false, CreateNoWindow = true }))
        { File.WriteAllText(Environment.GetEnvironmentVariable(MarkerVariable), helper.Id.ToString()); }
    }
}

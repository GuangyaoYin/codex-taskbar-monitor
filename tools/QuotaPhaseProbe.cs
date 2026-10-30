using System;
using System.Diagnostics;
using System.Reflection;
using CodexTaskbarMonitor;

// Diagnostics prints stage timing and category flags only. Never print RPC
// payloads, stderr text, account identifiers, or authentication material.
internal static class QuotaPhaseProbe
{
    private static int Main(string[] args)
    {
        int count = args.Length > 0 ? Int32.Parse(args[0]) : 3;
        if (args.Length > 1 && args[1] == "client") return ClientReads(count);
        bool persistent = args.Length > 1 && args[1] == "persistent";
        bool excludeDetails = args.Length > 2 && args[2] == "exclude";
        Type rpcType = typeof(CodexQuotaClient).GetNestedType("RpcProcess", BindingFlags.NonPublic);
        MethodInfo send = rpcType.GetMethod("Send");
        MethodInfo receive = rpcType.GetMethod("ReceiveResult");
        PropertyInfo diagnostic = rpcType.GetProperty("SafeDiagnostics");
        object rpc = null;
        int successes = 0;
        try
        {
            string path = CodexQuotaClient.FindCodexPath(null);
            for (int attempt = 1; attempt <= count; attempt++)
            {
                Stopwatch watch = Stopwatch.StartNew();
                string phase = "launch";
                try
                {
                    if (rpc == null)
                    {
                        rpc = Activator.CreateInstance(rpcType, new object[] { path });
                        Console.WriteLine("attempt=" + attempt + ";phase=launch;elapsedMs=" + watch.ElapsedMilliseconds);
                        phase = "initialize";
                        send.Invoke(rpc, new object[] { "{\"method\":\"initialize\",\"id\":1,\"params\":{\"clientInfo\":{\"name\":\"codex_taskbar_quota_diagnostic\",\"title\":\"Read-only quota diagnostic\",\"version\":\"0.1\"}}}" });
                        receive.Invoke(rpc, new object[] { 1, DateTime.UtcNow.AddSeconds(18) });
                        Console.WriteLine("attempt=" + attempt + ";phase=initialize;elapsedMs=" + watch.ElapsedMilliseconds);
                        send.Invoke(rpc, new object[] { "{\"method\":\"initialized\",\"params\":{}}" });
                    }
                    phase = "quota";
                    long quotaStart = watch.ElapsedMilliseconds;
                    int id = 10 + attempt;
                    send.Invoke(rpc, new object[] { "{\"method\":\"account/rateLimits/read\",\"id\":" + id
                        + (excludeDetails ? ",\"params\":{\"excludeResetCreditDetails\":true}" : "") + "}" });
                    receive.Invoke(rpc, new object[] { id, DateTime.UtcNow.AddSeconds(18) });
                    Console.WriteLine("attempt=" + attempt + ";phase=quota;quotaMs=" + (watch.ElapsedMilliseconds - quotaStart)
                        + ";totalMs=" + watch.ElapsedMilliseconds + ";result=success;" + diagnostic.GetValue(rpc, null));
                    successes++;
                }
                catch (Exception exception)
                {
                    Exception actual = exception is TargetInvocationException ? exception.InnerException : exception;
                    Console.WriteLine("attempt=" + attempt + ";phase=" + phase + ";totalMs=" + watch.ElapsedMilliseconds
                        + ";result=" + actual.GetType().Name + ";" + (rpc == null ? "noProcess" : diagnostic.GetValue(rpc, null)));
                    if (rpc != null) { ((IDisposable)rpc).Dispose(); rpc = null; }
                }
                finally
                {
                    if (!persistent && rpc != null) { ((IDisposable)rpc).Dispose(); rpc = null; }
                }
            }
            return successes == count ? 0 : 2;
        }
        finally { if (rpc != null) ((IDisposable)rpc).Dispose(); }
    }

    private static int ClientReads(int count)
    {
        int successes = 0;
        using (CodexQuotaClient client = new CodexQuotaClient())
        {
            for (int attempt = 1; attempt <= count; attempt++)
            {
                Stopwatch watch = Stopwatch.StartNew();
                CodexSnapshot snapshot = client.Read(null);
                Console.WriteLine("read=" + attempt + ";status=" + snapshot.Status + ";stale=" + snapshot.IsStale
                    + ";totalMs=" + watch.ElapsedMilliseconds + ";" + client.LastDiagnostics);
                if (snapshot.Status == CodexStatus.Ready) successes++;
            }
        }
        return successes == count ? 0 : 2;
    }
}

using System.Diagnostics;
using System.IO;
using System.Windows.Threading;

namespace FileHub.Platform;

/// <summary>
/// Logs every time the UI thread is stuck for over 300 ms (clicks and double-clicks wait while it is).
/// A stall over 1 s also saves every thread's stack with dotnet-stack, when installed, so the log says what blocked it.
/// </summary>
public static class UiWatchdog
{
    private static readonly string StackTool = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools", "dotnet-stack.exe");

    public static void Start(Dispatcher ui)
    {
        var t = new Thread(() =>
        {
            DateTime lastDump = DateTime.MinValue;
            while (!ui.HasShutdownStarted)
            {
                Thread.Sleep(250);
                var sw = Stopwatch.StartNew();
                var op = ui.BeginInvoke(DispatcherPriority.Send, () => { });   // Send: measures only the work running now, not the queue
                string? dump = null;
                if (op.Wait(TimeSpan.FromSeconds(1)) != DispatcherOperationStatus.Completed
                    && File.Exists(StackTool) && DateTime.UtcNow - lastDump > TimeSpan.FromMinutes(1))
                {
                    lastDump = DateTime.UtcNow;
                    dump = DumpStacks();
                }
                op.Wait();
                if (sw.ElapsedMilliseconds > 300)
                    Log.Warn($"UI thread busy {sw.ElapsedMilliseconds} ms" + (dump != null ? $" (stacks: {dump})" : ""));
            }
        }) { IsBackground = true, Name = "UI watchdog", Priority = ThreadPriority.AboveNormal };
        t.Start();
    }

    private static string? DumpStacks()
    {
        try
        {
            string file = Path.Combine(Log.Directory, $"stall-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            var psi = new ProcessStartInfo(StackTool, $"report -p {Environment.ProcessId}")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(15_000)) { try { p.Kill(); } catch { } return null; }
            File.WriteAllText(file, output.Result + p.StandardError.ReadToEnd());
            return Path.GetFileName(file);
        }
        catch (Exception ex) { Log.Warn("Stack dump failed: " + ex.Message); return null; }
    }
}

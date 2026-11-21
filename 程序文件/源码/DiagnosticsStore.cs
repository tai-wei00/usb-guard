using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

internal sealed class DiagnosticsStore : IDisposable {
    private readonly object sync = new object();
    private readonly Queue<DiagnosticEvent> queue = new Queue<DiagnosticEvent>();
    private readonly List<DiagnosticEvent> recent = new List<DiagnosticEvent>();
    private readonly AutoResetEvent signal = new AutoResetEvent(false);
    private readonly Thread worker;
    private readonly string logPath;
    private string lastFingerprint = "";
    private int pending;
    private bool closing;
    private bool healthy = true;
    private string healthMessage = "日志正常";

    internal DiagnosticsStore(string directory) {
        logPath = Path.Combine(directory, "usb-guard.log");
        worker = new Thread(Work);
        worker.IsBackground = true;
        worker.Name = "UsbGuardDiagnostics";
        worker.Start();
    }

    internal bool Healthy { get { lock (sync) return healthy; } }
    internal string HealthMessage { get { lock (sync) return healthMessage; } }
    internal string LogPath { get { return logPath; } }

    internal void Write(DiagnosticEvent item) {
        if (item == null) return;
        DiagnosticEvent copy = item.Clone();
        copy.Message = Clean(copy.Message);
        copy.TargetSummary = Clean(copy.TargetSummary);
        copy.Source = Clean(copy.Source);
        lock (sync) {
            if (closing) return;
            string fingerprint = copy.Fingerprint();
            if (fingerprint == lastFingerprint) return;
            lastFingerprint = fingerprint;
            if (queue.Count >= 512) queue.Dequeue();
            queue.Enqueue(copy);
            pending++;
            recent.Insert(0, copy.Clone());
            if (recent.Count > 20) recent.RemoveRange(20, recent.Count - 20);
        }
        signal.Set();
    }

    internal void WriteState(StateChangeSource source, GuardStateSnapshot previous, GuardStateSnapshot current, OperationResultKind result, string failedItems) {
        DiagnosticEvent item = new DiagnosticEvent();
        item.Source = source.ToString();
        item.EventType = "GUARD_STATE";
        item.FromState = previous == null ? "" : previous.DisplayName();
        item.TargetState = current == null ? "" : current.DisplayName();
        item.Result = result;
        item.Message = (current == null ? "" : current.Detail) + (string.IsNullOrEmpty(failedItems) ? "" : "；失败项：" + failedItems);
        item.Level = result == OperationResultKind.Failed || result == OperationResultKind.Partial ? "WARN" : "INFO";
        Write(item);
    }

    internal List<DiagnosticEvent> GetRecentKeyEvents(int maximumCount) {
        List<DiagnosticEvent> result = new List<DiagnosticEvent>();
        lock (sync) {
            int count = Math.Min(maximumCount, recent.Count);
            for (int i = 0; i < count; i++) result.Add(recent[i].Clone());
        }
        return result;
    }

    private void Work() {
        while (true) {
            signal.WaitOne(1000);
            while (true) {
                DiagnosticEvent next = null;
                lock (sync) {
                    if (queue.Count > 0) next = queue.Dequeue();
                }
                if (next == null) break;
                WriteFile(next);
                lock (sync) pending--;
            }
            lock (sync) { if (closing && queue.Count == 0) return; }
        }
    }

    private void WriteFile(DiagnosticEvent item) {
        Mutex mutex = null;
        bool owned = false;
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath));
            mutex = new Mutex(false, "Local\\UsbGuardDiagnosticsLog");
            try { owned = mutex.WaitOne(2000); } catch (AbandonedMutexException) { owned = true; }
            if (!owned) throw new IOException("日志跨进程互斥超时");
            string old = File.Exists(logPath) ? File.ReadAllText(logPath, Encoding.UTF8) : "";
            if (old.Length > 300000) {
                old = old.Substring(0, 300000);
                int cut = old.LastIndexOf('\n');
                if (cut > 0) old = old.Substring(0, cut + 1);
            }
            string line = item.FormatLine().Replace("\r", " ").Replace("\n", " ") + Environment.NewLine;
            File.WriteAllText(logPath, line + old, new UTF8Encoding(false));
            lock (sync) {
                bool recovered = !healthy;
                healthy = true;
                healthMessage = "日志正常";
                if (recovered) healthMessage = "日志已恢复可写";
            }
        } catch (Exception ex) {
            lock (sync) { healthy = false; healthMessage = "日志暂不可写：" + ex.Message; }
        } finally {
            if (owned && mutex != null) { try { mutex.ReleaseMutex(); } catch { } }
            if (mutex != null) mutex.Dispose();
        }
    }

    internal bool Flush(int milliseconds) {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < deadline) {
            lock (sync) { if (pending == 0) return true; }
            Thread.Sleep(10);
        }
        lock (sync) return pending == 0;
    }

    private static string Clean(string value) {
        return (value ?? "").Replace("\r", " ").Replace("\n", " ").Replace("|", "/");
    }

    public void Dispose() {
        lock (sync) closing = true;
        signal.Set();
        Flush(2000);
        if (worker.Join(2000)) signal.Dispose();
    }
}
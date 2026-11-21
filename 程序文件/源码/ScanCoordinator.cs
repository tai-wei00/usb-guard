using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text;
using System.Threading;

internal sealed class MountGenerationTracker {
    private readonly object sync = new object();
    private readonly Dictionary<string, long> active = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    private long nextGeneration;

    internal long Observe(IEnumerable<string> drives) {
        HashSet<string> current = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string drive in drives) current.Add(ScanCoordinator.NormalizeDrive(drive));
        lock (sync) {
            List<string> remove = new List<string>();
            foreach (string key in active.Keys) if (!current.Contains(key)) remove.Add(key);
            foreach (string key in remove) active.Remove(key);
            long maximum = nextGeneration;
            foreach (string drive in current) {
                if (!active.ContainsKey(drive)) active[drive] = ++nextGeneration;
                if (active[drive] > maximum) maximum = active[drive];
            }
            return maximum;
        }
    }

    internal long GenerationOf(string drive) {
        string key = ScanCoordinator.NormalizeDrive(drive);
        lock (sync) {
            if (!active.ContainsKey(key)) active[key] = ++nextGeneration;
            return active[key];
        }
    }
}

internal sealed class ScanCoordinator : IDisposable {
    private readonly object sync = new object();
    private readonly List<ScanRequest> queue = new List<ScanRequest>();
    private readonly Dictionary<string, ScanSummary> summaries = new Dictionary<string, ScanSummary>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> cancelReasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string[]> engineOrder;
    private readonly AutoResetEvent signal = new AutoResetEvent(false);
    private readonly Thread worker;
    private ScanRequest active;
    private Process activeProcess;
    private bool closing;

    internal event EventHandler<ScanStateChangedEventArgs> StateChanged;

    internal ScanCoordinator(Func<string[]> orderProvider) {
        engineOrder = orderProvider;
        worker = new Thread(Work);
        worker.IsBackground = true;
        worker.Name = "UsbGuardScanQueue";
        worker.Start();
    }

    internal static string NormalizeDrive(string drive) {
        string value = (drive ?? "").Trim().TrimEnd('\\');
        if (value.Length != 2 || value[1] != ':' || !char.IsLetter(value[0])) throw new ArgumentException("盘符格式无效");
        return char.ToUpperInvariant(value[0]) + ":";
    }

    internal OperationResult RequestScan(ScanRequest request) {
        if (request == null || request.MountGeneration <= 0) return OperationResult.Create(OperationResultKind.Failed, "扫描请求缺少有效挂载代次");
        ScanSummary changed;
        lock (sync) {
            if (closing) return OperationResult.Create(OperationResultKind.Cancelled, "扫描已停止");
            try { request.DriveRoot = NormalizeDrive(request.DriveRoot); } catch (Exception ex) { return OperationResult.Create(OperationResultKind.Failed, ex.Message); }
            if (IsDuplicateLocked(request)) return OperationResult.Create(OperationResultKind.Unchanged, request.DriveRoot + " 同一代次扫描已在队列中");
            queue.Add(request);
            ScanSummary summary = NewSummary(request);
            summary.State = ScanState.Scanning;
            summary.LastResult = "排队等待";
            summaries[request.DriveRoot] = summary;
            changed = summary.Clone();
        }
        Publish(changed);
        signal.Set();
        return OperationResult.Create(OperationResultKind.Success, "扫描请求已提交");
    }

    private bool IsDuplicateLocked(ScanRequest request) {
        if (active != null && active.DriveRoot == request.DriveRoot && active.MountGeneration == request.MountGeneration) return true;
        foreach (ScanRequest queued in queue) if (queued.DriveRoot == request.DriveRoot && queued.MountGeneration == request.MountGeneration) return true;
        return false;
    }

    internal ScanSummary GetLastResult(string drive, long generation) {
        string key = NormalizeDrive(drive);
        lock (sync) {
            ScanSummary summary;
            if (summaries.TryGetValue(key, out summary) && summary.MountGeneration == generation) return summary.Clone();
        }
        ScanSummary missing = new ScanSummary();
        missing.DriveRoot = key;
        missing.MountGeneration = generation;
        return missing;
    }

    internal List<ScanSummary> GetSummaries() {
        List<ScanSummary> result = new List<ScanSummary>();
        lock (sync) foreach (ScanSummary summary in summaries.Values) result.Add(summary.Clone());
        result.Sort(delegate(ScanSummary a, ScanSummary b) { return string.Compare(a.DriveRoot, b.DriveRoot, StringComparison.OrdinalIgnoreCase); });
        return result;
    }

    internal void CancelDrive(string drive, string reason) {
        string key = NormalizeDrive(drive);
        lock (sync) {
            for (int i = queue.Count - 1; i >= 0; i--) {
                if (queue[i].DriveRoot == key) {
                    ScanSummary cancelled = NewSummary(queue[i]);
                    cancelled.State = ScanState.Cancelled;
                    cancelled.LastResult = reason ?? "已取消";
                    cancelled.CompletedAt = DateTimeOffset.Now;
                    cancelled.IsTerminal = true;
                    summaries[key] = cancelled;
                    queue.RemoveAt(i);
                    Publish(cancelled.Clone());
                }
            }
            if (active != null && active.DriveRoot == key) {
                cancelReasons[key] = reason ?? "已取消";
                try { if (activeProcess != null && !activeProcess.HasExited) activeProcess.Kill(); } catch { }
            }
        }
    }

    internal void CancelAll(string reason) {
        List<ScanSummary> cancelled = new List<ScanSummary>();
        lock (sync) {
            foreach (ScanRequest request in queue) {
                ScanSummary summary = NewSummary(request);
                summary.State = ScanState.Cancelled;
                summary.LastResult = reason ?? "已取消";
                summary.CompletedAt = DateTimeOffset.Now;
                summary.IsTerminal = true;
                summaries[request.DriveRoot] = summary;
                cancelled.Add(summary.Clone());
            }
            queue.Clear();
            if (active != null) {
                cancelReasons[active.DriveRoot] = reason ?? "已取消";
                try { if (activeProcess != null && !activeProcess.HasExited) activeProcess.Kill(); } catch { }
            }
        }
        foreach (ScanSummary summary in cancelled) Publish(summary);
        signal.Set();
    }

    private void Work() {
        while (true) {
            signal.WaitOne(1000);
            ScanRequest request = null;
            lock (sync) {
                if (closing && queue.Count == 0) return;
                if (queue.Count > 0) {
                    request = queue[0];
                    queue.RemoveAt(0);
                    active = request;
                }
            }
            if (request == null) continue;
            RunScan(request);
            lock (sync) {
                active = null;
                activeProcess = null;
                cancelReasons.Remove(request.DriveRoot);
            }
        }
    }

    private void RunScan(ScanRequest request) {
        ScanSummary summary = NewSummary(request);
        Process process = null;
        StringBuilder tail = new StringBuilder();
        try {
            string engineName;
            string enginePath = FindAvEngine(out engineName);
            if (enginePath == null) {
                Finish(summary, ScanState.Failed, "未检测到可用杀毒软件，已跳过" + (request.Manual ? "手动" : "自动") + "扫描");
                return;
            }
            summary.Engine = engineName == "卡巴斯基" ? ScanEngineKind.Kaspersky : ScanEngineKind.Defender;
            summary.LastResult = "正在使用 " + engineName + " 扫描";
            Publish(summary.Clone());
            ProcessStartInfo start = new ProcessStartInfo();
            start.FileName = enginePath;
            start.Arguments = summary.Engine == ScanEngineKind.Kaspersky
                ? "SCAN \"" + request.DriveRoot + "\\\" /i0"
                : "-Scan -ScanType 3 -File \"" + request.DriveRoot + "\\\" -DisableRemediation";
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            process = new Process();
            process.StartInfo = start;
            process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs args) { AppendTail(tail, args.Data); };
            process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs args) { AppendTail(tail, args.Data); };
            lock (sync) activeProcess = process;
            if (!process.Start()) { Finish(summary, ScanState.Failed, "扫描引擎启动失败"); return; }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            DateTime deadline = DateTime.UtcNow.AddMinutes(10);
            while (!process.HasExited) {
                string cancel = CancelReason(request.DriveRoot);
                if (cancel != null) { TryKill(process); Finish(summary, ScanState.Cancelled, cancel); return; }
                if (DateTime.UtcNow >= deadline) { TryKill(process); Finish(summary, ScanState.Cancelled, "扫描超过 10 分钟，已取消"); return; }
                if (!DrivePresent(request.DriveRoot)) { TryKill(process); Finish(summary, ScanState.Cancelled, "设备已移除，扫描已取消"); return; }
                Thread.Sleep(500);
            }
            process.WaitForExit(2000);
            int code = process.ExitCode;
            bool threat = (summary.Engine == ScanEngineKind.Kaspersky && code == 1) || (summary.Engine == ScanEngineKind.Defender && code == 2);
            if (code == 0) Finish(summary, ScanState.Clean, engineName + " 扫描完成，未发现威胁");
            else if (threat) Finish(summary, ScanState.ThreatFound, engineName + " 发现威胁，请打开杀软查看详情；" + TailOf(tail));
            else Finish(summary, ScanState.Failed, engineName + " 返回异常 exit=" + code + "；" + TailOf(tail));
        } catch (Exception ex) {
            Finish(summary, ScanState.Failed, "扫描失败：" + ex.Message);
        } finally {
            lock (sync) { if (activeProcess == process) activeProcess = null; }
            if (process != null) process.Dispose();
        }
    }

    private string CancelReason(string drive) {
        lock (sync) {
            string reason;
            return cancelReasons.TryGetValue(drive, out reason) ? reason : null;
        }
    }

    private void Finish(ScanSummary summary, ScanState state, string message) {
        summary.State = state;
        summary.LastResult = string.IsNullOrEmpty(message) ? GuardText.ScanName(state) : message;
        summary.CompletedAt = DateTimeOffset.Now;
        summary.IsTerminal = true;
        lock (sync) summaries[summary.DriveRoot] = summary;
        Publish(summary.Clone());
    }

    private static ScanSummary NewSummary(ScanRequest request) {
        ScanSummary summary = new ScanSummary();
        summary.DriveRoot = request.DriveRoot;
        summary.MountGeneration = request.MountGeneration;
        return summary;
    }

    private void Publish(ScanSummary summary) {
        EventHandler<ScanStateChangedEventArgs> handler = StateChanged;
        if (handler != null) handler(this, new ScanStateChangedEventArgs(summary));
    }

    private string FindAvEngine(out string engineName) {
        string[] order = engineOrder == null ? new string[0] : engineOrder();
        if (order == null || order.Length == 0) order = new string[] { "kaspersky", "defender" };
        foreach (string engine in order) {
            if (engine == "kaspersky") {
                string path = FindKaspersky();
                if (path != null) { engineName = "卡巴斯基"; return path; }
            } else if (engine == "defender") {
                string path = FindDefender();
                if (path != null && WinDefendUsable()) { engineName = "Windows Defender"; return path; }
            }
        }
        engineName = null;
        return null;
    }

    private static string FindKaspersky() {
        try {
            string[] roots = new string[] { Environment.GetEnvironmentVariable("ProgramFiles(x86)"), Environment.GetEnvironmentVariable("ProgramFiles") };
            foreach (string root in roots) {
                if (string.IsNullOrEmpty(root)) continue;
                string basePath = Path.Combine(root, "Kaspersky Lab");
                if (!Directory.Exists(basePath)) continue;
                foreach (string directory in Directory.GetDirectories(basePath)) {
                    string candidate = Path.Combine(directory, "avp.com");
                    if (File.Exists(candidate)) return candidate;
                }
            }
        } catch { }
        return null;
    }

    private static string FindDefender() {
        try {
            string data = Environment.GetEnvironmentVariable("ProgramData");
            if (!string.IsNullOrEmpty(data)) {
                string platform = Path.Combine(data, @"Microsoft\Windows Defender\Platform");
                if (Directory.Exists(platform)) {
                    string best = null;
                    Version bestVersion = new Version(0, 0);
                    foreach (string directory in Directory.GetDirectories(platform)) {
                        string candidate = Path.Combine(directory, "MpCmdRun.exe");
                        if (!File.Exists(candidate)) continue;
                        Version version;
                        if (Version.TryParse(Path.GetFileName(directory), out version)) {
                            if (version > bestVersion) { bestVersion = version; best = candidate; }
                        } else if (best == null) best = candidate;
                    }
                    if (best != null) return best;
                }
            }
            string[] candidates = new string[] {
                Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles") ?? "", @"Windows Defender\MpCmdRun.exe"),
                Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? "", @"Windows Defender\MpCmdRun.exe")
            };
            foreach (string candidate in candidates) if (File.Exists(candidate)) return candidate;
        } catch { }
        return null;
    }

    private static bool WinDefendUsable() {
        try {
            using (ManagementObjectSearcher query = new ManagementObjectSearcher("SELECT State FROM Win32_Service WHERE Name='WinDefend'"))
            using (ManagementObjectCollection collection = query.Get()) {
                foreach (ManagementObject item in collection) {
                    string state = Convert.ToString(item["State"]);
                    item.Dispose();
                    return string.Equals(state, "Running", StringComparison.OrdinalIgnoreCase);
                }
            }
        } catch { return true; }
        return false;
    }

    private static bool DrivePresent(string drive) {
        try {
            foreach (DriveInfo item in DriveInfo.GetDrives()) if (string.Equals(item.Name.Substring(0, 2), drive, StringComparison.OrdinalIgnoreCase)) return true;
        } catch { }
        return false;
    }

    private static void TryKill(Process process) { try { if (!process.HasExited) process.Kill(); } catch { } }
    private static void AppendTail(StringBuilder tail, string line) {
        if (line == null) return;
        lock (tail) { tail.AppendLine(line); if (tail.Length > 8192) tail.Remove(0, tail.Length - 8192); }
    }
    private static string TailOf(StringBuilder tail) {
        lock (tail) {
            string text = tail.ToString().Replace("\r", " ").Replace("\n", " ").Trim();
            return text.Length > 400 ? text.Substring(text.Length - 400) : text;
        }
    }

    public void Dispose() {
        CancelAll("程序正在退出，扫描已取消");
        lock (sync) closing = true;
        signal.Set();
        if (worker.Join(3000)) signal.Dispose();
    }
}
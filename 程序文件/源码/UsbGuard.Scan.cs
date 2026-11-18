using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text;
using System.Threading;

static partial class UsbGuard {
    static Thread scanThread;
    static string activeScanKey;
    static HashSet<string> noAvPrompted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    static string FindKaspersky() {
        try {
            string[] roots = new string[] {
                Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                Environment.GetEnvironmentVariable("ProgramFiles")
            };
            foreach (string r in roots) {
                if (string.IsNullOrEmpty(r)) continue;
                string kl = Path.Combine(r, "Kaspersky Lab");
                if (!Directory.Exists(kl)) continue;
                foreach (string d in Directory.GetDirectories(kl)) {
                    string p = Path.Combine(d, "avp.com");
                    if (File.Exists(p)) return p;
                }
            }
        } catch { }
        return null;
    }

    static bool WinDefendUsable() {
        try {
            ManagementObjectSearcher q = new ManagementObjectSearcher("SELECT State FROM Win32_Service WHERE Name='WinDefend'");
            foreach (ManagementObject o in q.Get()) {
                string st = Convert.ToString(o["State"]);
                return string.Equals(st, "Running", StringComparison.OrdinalIgnoreCase);
            }
        } catch { return true; }
        return false;
    }

    static string FindDefender() {
        try {
            string pd = Environment.GetEnvironmentVariable("ProgramData");
            if (!string.IsNullOrEmpty(pd)) {
                string plat = Path.Combine(pd, @"Microsoft\Windows Defender\Platform");
                if (Directory.Exists(plat)) {
                    string best = null;
                    Version bestVer = new Version(0, 0);
                    foreach (string d in Directory.GetDirectories(plat)) {
                        string exe = Path.Combine(d, "MpCmdRun.exe");
                        if (!File.Exists(exe)) continue;
                        Version v;
                        string name = Path.GetFileName(d);
                        if (Version.TryParse(name, out v)) {
                            if (v > bestVer) { bestVer = v; best = exe; }
                        } else if (best == null) best = exe;
                    }
                    if (best != null) return best;
                }
            }
            string[] cands = new string[] {
                Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles") ?? "", @"Windows Defender\MpCmdRun.exe"),
                Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? "", @"Windows Defender\MpCmdRun.exe")
            };
            foreach (string c in cands) { if (!string.IsNullOrEmpty(c) && File.Exists(c)) return c; }
        } catch { }
        return null;
    }

    static string FindAvEngine(out string engineName) {
        foreach (string eng in scanEngineOrder) {
            if (eng == "kaspersky") {
                string p = FindKaspersky();
                if (p != null) { engineName = "卡巴斯基"; return p; }
            } else if (eng == "defender") {
                string p = FindDefender();
                if (p != null && WinDefendUsable()) { engineName = "Windows Defender"; return p; }
            }
        }
        engineName = null;
        return null;
    }

    static void StartScanAsync(string drive, bool manual) {
        if (string.IsNullOrEmpty(drive)) return;
        string key = drive.ToUpperInvariant();
        lock (noAvPrompted) {
            if (activeScanKey != null && string.Equals(activeScanKey, key, StringComparison.OrdinalIgnoreCase)) return;
        }
        Thread t = new Thread(delegate () { ScanWorker(key, manual); });
        t.IsBackground = true;
        t.Name = "AvScan";
        scanThread = t;
        t.Start();
    }

    static void UiBalloon(string title, string text, System.Windows.Forms.ToolTipIcon icon) {
        try {
            if (notify != null) {
                notify.BalloonTipTitle = title;
                notify.BalloonTipText = text;
                notify.BalloonTipIcon = icon;
                notify.ShowBalloonTip(6000);
            }
        } catch { }
    }

    static void ScanWorker(string drive, bool manual) {
        lock (noAvPrompted) activeScanKey = drive;
        try {
            string engineName;
            string exe = FindAvEngine(out engineName);
            if (exe == null) {
                bool skipPrompt = false;
                if (!manual) {
                    lock (noAvPrompted) {
                        if (noAvPrompted.Contains(drive)) skipPrompt = true;
                        else noAvPrompted.Add(drive);
                    }
                }
                if (!skipPrompt) {
                    string mode = manual ? "手动" : "自动";
                    UiBalloon("USB 哨兵", "未检测到可用杀毒软件，已跳过" + mode + "扫描，建议启用 Windows Defender 或安装杀软", System.Windows.Forms.ToolTipIcon.Warning);
                    Log("WARN", "SCAN_RESULT", drive, "no av engine, " + mode + " scan skipped");
                }
                return;
            }
            string args;
            if (engineName == "卡巴斯基") args = "SCAN \"" + drive + "\\\" /i0";
            else args = "-Scan -ScanType 3 -File \"" + drive + "\\\" -DisableRemediation";
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = exe;
            psi.Arguments = args;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            Process p = new Process();
            p.StartInfo = psi;
            StringBuilder tail = new StringBuilder();
            p.OutputDataReceived += delegate (object s, DataReceivedEventArgs e2) { AppendTail(tail, e2.Data); };
            p.ErrorDataReceived += delegate (object s, DataReceivedEventArgs e2) { AppendTail(tail, e2.Data); };
            try {
                if (!p.Start()) {
                    UiBalloon("USB 哨兵", "扫描引擎启动失败，建议手动查杀", System.Windows.Forms.ToolTipIcon.Warning);
                    Log("WARN", "SCAN_START", drive, "engine start failed: " + engineName);
                    return;
                }
            } catch (Exception ex) {
                UiBalloon("USB 哨兵", "扫描引擎启动失败，建议手动查杀", System.Windows.Forms.ToolTipIcon.Warning);
                Log("WARN", "SCAN_START", drive, "engine start failed: " + ex.Message);
                return;
            }
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            UiBalloon("USB 哨兵", "已开始扫描（" + engineName + "）" + drive, System.Windows.Forms.ToolTipIcon.Info);
            Log("INFO", "SCAN_START", drive, engineName + " pid=" + p.Id);
            DateTime deadline = DateTime.Now.AddMinutes(10);
            bool killed = false;
            while (true) {
                if (p.HasExited) break;
                if (DateTime.Now > deadline) {
                    killed = true;
                    try { p.Kill(); } catch { }
                    UiBalloon("USB 哨兵", drive + " 扫描超时已中止", System.Windows.Forms.ToolTipIcon.Warning);
                    Log("WARN", "SCAN_RESULT", drive, "timeout, killed");
                    break;
                }
                HashSet<string> now = RemovableDrives();
                if (!now.Contains(drive.ToUpperInvariant())) {
                    killed = true;
                    try { p.Kill(); } catch { }
                    UiBalloon("USB 哨兵", drive + " 设备已移除，扫描中止", System.Windows.Forms.ToolTipIcon.Warning);
                    Log("WARN", "SCAN_RESULT", drive, "drive removed, killed");
                    break;
                }
                Thread.Sleep(2000);
            }
            if (killed) return;
            int code;
            try { code = p.ExitCode; } catch { code = -999; }
            bool threat = (engineName == "卡巴斯基" && code == 1) || (engineName == "Windows Defender" && code == 2);
            if (code == 0) {
                UiBalloon("USB 哨兵", drive + " 扫描完成（" + engineName + "），未发现威胁", System.Windows.Forms.ToolTipIcon.Info);
                Log("INFO", "SCAN_RESULT", drive, engineName + " clean, exit=0");
            } else if (threat) {
                UiBalloon("USB 哨兵", "⚠ " + drive + " 发现威胁（" + engineName + "），请打开杀软查看详情", System.Windows.Forms.ToolTipIcon.Warning);
                Log("ERROR", "SCAN_RESULT", drive, engineName + " THREAT FOUND, exit=" + code + " | " + TailOf(tail));
            } else {
                UiBalloon("USB 哨兵", drive + " 扫描引擎返回异常（" + engineName + " exit=" + code + "），建议手动查杀", System.Windows.Forms.ToolTipIcon.Warning);
                Log("WARN", "SCAN_RESULT", drive, engineName + " abnormal exit=" + code + " | " + TailOf(tail));
            }
        } catch (Exception ex) {
            Log("WARN", "SCAN_RESULT", drive, "scan worker error: " + ex.Message);
        } finally {
            lock (noAvPrompted) activeScanKey = null;
        }
    }

    static void AppendTail(StringBuilder sb, string line) {
        if (line == null) return;
        lock (sb) {
            sb.AppendLine(line);
            if (sb.Length > 8192) sb.Remove(0, sb.Length - 8192);
        }
    }

    static string TailOf(StringBuilder sb) {
        lock (sb) {
            string s = sb.ToString();
            if (s.Length > 400) s = s.Substring(s.Length - 400);
            return s.Replace("\r", " ").Replace("\n", " ").Trim();
        }
    }
}
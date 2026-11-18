using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

static partial class UsbGuard {
    static string dir, whitelistPath, logPath, cfgPath;
    static HashSet<string> white = new HashSet<string>();
    static HashSet<string> last = new HashSet<string>();
    static bool lockOnUnknown = true;
    static DateTime pausedUntil = DateTime.MinValue;
    static DateTime repairUntil = DateTime.MinValue;
    static volatile bool deviceDirty = false;
    static NotifyIcon notify;
    static ContextMenuStrip menu;
    static Icon cur;
    static ManagementEventWatcher watcher;
    static bool persistentLock = true;
    static bool autoScanRemovable = true;
    static Form alertForm;
    static Form repairForm;
    static Dictionary<string, DateTime> alertedAt = new Dictionary<string, DateTime>();
    static Dictionary<string, DateTime> alertedGroupAt = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
    static HashSet<string> lastRemovable = new HashSet<string>();

    [DllImport("user32.dll")] static extern bool LockWorkStation();
    [DllImport("user32.dll")] static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);
    [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern bool GetUserObjectInformation(IntPtr hObj, int nIndex, StringBuilder pvInfo, uint nLength, out uint lpnLengthNeeded);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern bool DestroyIcon(IntPtr h);

    static void LogErr(object ex) {
        try {
            string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "USBGuard");
            Directory.CreateDirectory(d);
            File.AppendAllText(Path.Combine(d, "guard-error.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + Convert.ToString(ex) + Environment.NewLine, Encoding.UTF8);
        } catch { }
    }

    [STAThread]
    static void Main() {
        AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e) { LogErr(e.ExceptionObject); };
        Application.ThreadException += delegate (object s, ThreadExceptionEventArgs e) { LogErr(e.Exception); };
        string[] cliArgs = Environment.GetCommandLineArgs();
        if (Array.IndexOf(cliArgs, "--test-alert") >= 0) { RunAlertTest(); return; }
        if (Array.IndexOf(cliArgs, "--test-prompt") >= 0) { RunPromptTest(); return; }
        if (Array.IndexOf(cliArgs, "--sync-allow") >= 0) { RunSyncAllow(); return; }
        int ti = Array.IndexOf(cliArgs, "--trust-current");
        if (ti >= 0) { InitPaths(); Environment.Exit(RunCliTrustCurrent()); return; }
        ti = Array.IndexOf(cliArgs, "--trust-ids");
        if (ti >= 0 && ti + 1 < cliArgs.Length) { InitPaths(); Environment.Exit(RunCliTrustIds(cliArgs[ti + 1])); return; }
        ti = Array.IndexOf(cliArgs, "--remove-entry");
        if (ti >= 0 && ti + 1 < cliArgs.Length) { InitPaths(); Environment.Exit(RunCliRemoveEntry(cliArgs[ti + 1])); return; }
        ti = Array.IndexOf(cliArgs, "--save-config");
        if (ti >= 0 && ti + 1 < cliArgs.Length) { InitPaths(); Environment.Exit(RunCliSaveConfig(cliArgs[ti + 1])); return; }
        try { RunGuard(); } catch (Exception ex) { LogErr(ex); }
    }

    static void InitPaths() {
        dir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
        whitelistPath = Path.Combine(dir, "usb-whitelist.json");
        logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "USBGuard", "usb-guard.log");
        cfgPath = Path.Combine(dir, "usb-guard-config.json");
    }

    static void RunSyncAllow() {
        InitPaths();
        if (!StrictModeOn()) {
            Log("SYNC-USB-LOCK skipped: system lock not enabled (no AllowInstanceIDs)");
            return;
        }
        // v2：同步交由安装脚本的 -EnforcePolicy 统一处理（写 AllowInstanceIDs + 恢复策略），
        // 不再由本进程直接写 DenyDeviceClasses 类拒绝——那与 v2 设计冲突且会误伤蓝牙 HID。
        string installer = Path.Combine(Directory.GetParent(dir).FullName, "setup.ps1");
        if (!File.Exists(installer)) { Log("SYNC-USB-LOCK failed: installer not found"); return; }
        try {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = "powershell.exe";
            psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" +
                            installer + "\" -EnforcePolicy";
            psi.WorkingDirectory = Directory.GetParent(dir).FullName;
            psi.UseShellExecute = true;
            psi.Verb = "runas";
            Process.Start(psi);
            Log("SYNC-USB-LOCK: elevated -EnforcePolicy requested (UAC)");
        } catch (Exception ex) { Log("SYNC-USB-LOCK failed: " + ex.Message); }
    }

    static string[] HardwareIdsOfAllDevices() {
        List<string> list = new List<string>();
        try {
            using (RegistryKey root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum")) {
                if (root != null) CollectHwIds(root, 0, list);
            }
        } catch { }
        return list.ToArray();
    }

    static void CollectHwIds(RegistryKey key, int depth, List<string> list) {
        if (depth > 6) return;
        try {
            string[] hw = key.GetValue("HardwareID") as string[];
            if (hw != null) foreach (string s in hw) if (!string.IsNullOrEmpty(s)) list.Add(s);
            foreach (string name in key.GetSubKeyNames()) {
                try { using (RegistryKey sub = key.OpenSubKey(name)) { if (sub != null) CollectHwIds(sub, depth + 1, list); } } catch { }
            }
        } catch { }
    }

    static int ApplyAllowList() {
        // v2：不再由主程序写 DenyDeviceClasses。历史版本在这里写 USB/HID 类拒绝，
        // 会误伤蓝牙 HID，且默认评估顺序下 Allow 压不住它。
        // 系统级放行/拦截统一由 安装.ps1 的 Sync-AllowList（AllowInstanceIDs）负责。
        // 保留此函数仅为了兼容旧调用点，不做任何注册表写入。
        Log("SYNC-USB-LOCK: no-op (system policy managed by 安装.ps1 -EnforcePolicy)");
        return 0;
    }

    // v2 起系统策略以 AllowInstanceIDs 为准，不再用 DenyDeviceClasses 判断"是否启用系统锁定"。
    // 旧版用 DenyDeviceClasses 判断，但 v2 明确不写类拒绝，导致信任后同步永远是死代码。
    static bool StrictModeOn() {
        try {
            using (RegistryKey k = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Policies\Microsoft\Windows\DeviceInstall\Restrictions\AllowInstanceIDs")) {
                return k != null && k.GetValueNames().Length > 0;
            }
        } catch { return false; }
    }

    static void ElevateSyncAllow() {
        try {
            if (!StrictModeOn()) {
                // 没启用系统级锁定就静默返回，不打扰用户（信任名单本身已保存，功能完整）。
                return;
            }
            // 让安装脚本的 -EnforcePolicy 统一负责写 AllowInstanceIDs / 恢复策略，
            // 不再走本文件里的 ApplyAllowList（它会写 DenyDeviceClasses 类拒绝，与 v2 设计冲突）。
            string installer = Path.Combine(Directory.GetParent(dir).FullName, "setup.ps1");
            if (!File.Exists(installer)) {
                Log("SYNC-ALLOW skipped: installer not found: " + installer);
                return;
            }
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = "powershell.exe";
            psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" +
                            installer + "\" -EnforcePolicy";
            psi.WorkingDirectory = Directory.GetParent(dir).FullName;
            psi.UseShellExecute = true;
            psi.Verb = "runas";
            Process.Start(psi);
            Log("SYNC-ALLOW: elevated -EnforcePolicy requested (UAC)");
        } catch (Exception ex) { Log("SYNC-ALLOW skipped: " + ex.Message); }
    }

    static void RunAlertTest() {
        InitPaths();
        LoadCfgV2(); LoadWhiteV2();
        Log("TEST ALERT: simulate unknown USB device, lock=" + lockOnUnknown);
        if (lockOnUnknown) {
            try { LockWorkStation(); Log("TEST ALERT: LockWorkStation called"); }
            catch (Exception ex) { Log("TEST ALERT lock failed: " + ex.Message); }
        }
    }

    static void RunPromptTest() {
        InitPaths();
        LoadCfgV2(); LoadWhiteV2();
        string catArg = null;
        string[] args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, "--test-prompt");
        if (i >= 0 && i + 1 < args.Length) {
            string c = (args[i + 1] ?? "").ToLowerInvariant();
            if (c == "storage" || c == "hid" || c == "other") catArg = c;
        }
        Log("INFO", "TEST", null, "test prompt shown, category=" + (catArg ?? "default"));
        Application.EnableVisualStyles();
        ShowAlertForm(new List<string> { "USB\\VID_TEST&PID_0001\\TESTINSTANCE" }, null, true, catArg);
        Application.Run();
    }

    static void RunGuard() {
        InitPaths();
        bool created;
        string mutexName = "Global\\UsbGuardSentinel_" +
            BitConverter.ToString(System.Security.Cryptography.SHA256.Create().ComputeHash(System.Text.Encoding.UTF8.GetBytes(dir)))
                      .Replace("-", "").Substring(0, 16);
        using (Mutex m = new Mutex(true, mutexName, out created)) {
            if (!created) { Log("DUPLICATE launch rejected (already running)"); return; }
            LoadCfgV2();
            whitelistHealthy = LoadWhiteV2();
            if (!whitelistHealthy) Log("ERROR", "WHITELIST_MIGRATE", null, "whitelist file corrupted, safe degraded (auto-lock disabled)");
            Application.EnableVisualStyles();
            notify = new NotifyIcon();
            menu = new ContextMenuStrip();
            notify.ContextMenuStrip = menu;
            menu.Opening += delegate { BuildMenu(); };
            notify.DoubleClick += delegate { OpenLog(); };
            SetIcon(false);
            notify.Visible = true;
            Log("START guard on, whitelist=" + white.Count + " lock=" + lockOnUnknown + " healthy=" + whitelistHealthy);
            last = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string[]> snap0 = SnapshotV2();
            if (snap0 != null) foreach (string k in snap0.Keys) { if (IsMonitorableId(k)) last.Add(k); }
            lastRemovable = RemovableDrives();
            watcher = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM Win32_DeviceChangeEvent WITHIN 1"));
            watcher.EventArrived += OnDeviceEvent;
            try { watcher.Start(); Log("WMI watcher started"); } catch (Exception ex) { Log("WARN watcher failed: " + ex.Message); }
            System.Windows.Forms.Timer persistent = new System.Windows.Forms.Timer();
            persistent.Interval = 1500;
            persistent.Tick += delegate { CheckPersistent(); };
            persistent.Start();
            System.Windows.Forms.Timer heal = new System.Windows.Forms.Timer();
            heal.Interval = 30000;
            heal.Tick += delegate {
                try { notify.Visible = false; notify.Visible = true; } catch { }
            };
            heal.Start();
            Application.Run();
        }
    }

    static void Log(string level, string eventType, string deviceSummary, string action) {
        StringBuilder sb = new StringBuilder();
        sb.Append(level).Append(" | ").Append(eventType);
        if (!string.IsNullOrEmpty(deviceSummary)) sb.Append(" | ").Append(EscapeLogField(deviceSummary));
        sb.Append(" | ").Append(EscapeLogField(action));
        Log(sb.ToString());
    }

    static void Log(string s) {
        Mutex mtx = null;
        bool owned = false;
        try {
            try {
                mtx = new Mutex(false, "Global\\UsbGuardLog");
                try { owned = mtx.WaitOne(2000); }
                catch (AbandonedMutexException) { owned = true; }
                catch { owned = false; }
            } catch { mtx = null; owned = false; }
            Directory.CreateDirectory(Path.GetDirectoryName(logPath));
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + s + Environment.NewLine;
            string old = "";
            if (File.Exists(logPath)) {
                try { old = File.ReadAllText(logPath, Encoding.UTF8); } catch { old = ""; }
            }
            if (old.Length > 300000) {
                old = old.Substring(0, 300000);
                int cut = old.LastIndexOf('\n');
                if (cut > 0) old = old.Substring(0, cut + 1);
            }
            File.WriteAllText(logPath, line + old, Encoding.UTF8);
        } catch { }
        finally {
            if (owned && mtx != null) { try { mtx.ReleaseMutex(); } catch { } }
            if (mtx != null) { try { mtx.Dispose(); } catch { } }
        }
    }

    static void OnDeviceEvent(object sender, EventArrivedEventArgs e) {
        deviceDirty = true;
    }

    static void ProcessDeviceChanges() {
        Dictionary<string, string[]> snap = SnapshotV2();
        if (snap == null) return;
        HashSet<string> now = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string k in snap.Keys) { if (IsMonitorableId(k)) now.Add(k); }
        List<string> fresh = new List<string>();
        foreach (string k in now) {
            if (last.Contains(k)) continue;
            if (IsTrustedId(k)) { Log("INFO", "DEVICE_INSERT", k, "known device"); continue; }
            string name = NameOfId(snap, k) ?? k;
            string gk = GroupKeyOf(k);
            bool dup = (alertedAt.ContainsKey(k) && (DateTime.Now - alertedAt[k]).TotalSeconds < 10)
                    || (alertedGroupAt.ContainsKey(gk) && (DateTime.Now - alertedGroupAt[gk]).TotalSeconds < 10);
            alertedAt[k] = DateTime.Now;
            alertedGroupAt[gk] = DateTime.Now;
            string cls = NameOfId(snap, k) != null && snap[k].Length > 1 ? snap[k][1] : null;
            string cat = ClassifyOf(k, cls, false);
            Log("INFO", "DEVICE_INSERT", k, "unknown " + CategoryName(cat) + " (" + name + ")" + (dup ? " [throttled]" : ""));
            if (DateTime.Now < repairUntil) { Log("  repair mode: record only"); continue; }
            if (DateTime.Now < pausedUntil) { Log("  paused: record only"); continue; }
            if (!whitelistHealthy) { Log("  whitelist unhealthy: record only"); continue; }
            if (dup) continue;
            fresh.Add(k);
            SetIcon(true);
            notify.BalloonTipTitle = "USB 哨兵: 检测到陌生" + CategoryName(cat);
            notify.BalloonTipText = name + "  (" + k + ")";
            notify.BalloonTipIcon = ToolTipIcon.Warning;
            notify.ShowBalloonTip(8000);
        }
        last = now;
        if (fresh.Count > 0) {
            ShowAlertForm(fresh, snap, false, null);
            if (lockOnUnknown) {
                try { LockWorkStation(); Log("INFO", "LOCK_TRIGGER", null, "unknown device inserted, screen locked"); } catch { }
            }
        }
        CheckRemovable();
    }

    static void TrustCurrent() {
        Dictionary<string, string[]> snap = SnapshotV2();
        if (snap == null) return;
        List<string> ids = new List<string>();
        foreach (KeyValuePair<string, string[]> kv in snap) {
            if (IsMonitorableId(kv.Key) && !IsTrustedId(kv.Key)) ids.Add(kv.Key);
        }
        foreach (string id in ids) AddTrustedIds(new string[] { id }, snap[id][0]);
        Log("INFO", "DEVICE_TRUST", null, "trust current +" + ids.Count + ", whitelist=" + whiteEntries.Count);
        bool saved = SaveWhiteV2();
        if (!saved) {
            int rc = RunElevatedSelf("--trust-current");
            LoadWhiteV2();
            saved = (rc == 0);
        }
        notify.BalloonTipTitle = "USB 哨兵";
        notify.BalloonTipText = saved
            ? "已信任当前所有设备（新增 " + ids.Count + " 个）。系统驱动策略将在下次策略守护（10 分钟内）自动更新"
            : "保存失败，名单未变更";
        notify.BalloonTipIcon = saved ? ToolTipIcon.Info : ToolTipIcon.Warning;
        notify.ShowBalloonTip(5000);
    }

    static string StartupLnk() {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "USB哨兵.lnk");
    }

    static bool Autostart() { return File.Exists(StartupLnk()); }

    static void ToggleAutostart() {
        try {
            string lnk = StartupLnk();
            if (File.Exists(lnk)) { File.Delete(lnk); return; }
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(t);
            object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
            Type sct = sc.GetType();
            sct.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { Path.Combine(dir, "UsbGuard.exe") });
            sct.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { dir });
            sct.InvokeMember("WindowStyle", BindingFlags.SetProperty, null, sc, new object[] { 7 });
            sct.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
        } catch (Exception ex) { Log("ERR autostart: " + ex.Message); }
    }

    static void OpenLog() {
        try { Process.Start("notepad.exe", logPath); } catch { }
    }

    static void Pause30() {
        pausedUntil = DateTime.Now.AddMinutes(30);
        Log("INFO", "GUARD_STATE", null, "pause 30min");
        UpdateTrayText();
        notify.BalloonTipTitle = "USB 哨兵";
        notify.BalloonTipText = "已暂停守卫 30 分钟（这期间新设备只记录、不锁屏）";
        notify.BalloonTipIcon = ToolTipIcon.Info;
        notify.ShowBalloonTip(4000);
    }

    static void UpdateTrayText() {
        string s;
        if (!whitelistHealthy) s = "USB 哨兵：白名单异常（自动锁屏已暂停）";
        else if (DateTime.Now < repairUntil) s = "USB 哨兵：维修模式（剩 " + ((int)(repairUntil - DateTime.Now).TotalMinutes + 1) + " 分钟）";
        else if (DateTime.Now < pausedUntil) s = "USB 哨兵：暂停守卫（剩 " + ((int)(pausedUntil - DateTime.Now).TotalMinutes + 1) + " 分钟）";
        else s = "USB 哨兵：防护中";
        if (s.Length > 63) s = s.Substring(0, 63);
        try { if (notify != null && notify.Text != s) notify.Text = s; } catch { }
    }

    static void ShowRepairFormOnce() {
        try {
            if (repairForm != null) return;
            Form f = new Form();
            f.Text = "USB 哨兵：白名单文件异常";
            f.FormBorderStyle = FormBorderStyle.FixedDialog;
            f.MaximizeBox = false; f.MinimizeBox = false;
            f.StartPosition = FormStartPosition.CenterScreen;
            f.TopMost = true;
            f.Width = 520; f.Height = 240;
            f.BackColor = Color.White;
            Label lbl = new Label();
            lbl.Text = "白名单文件异常（可能已损坏），已暂停自动锁屏保护。\n\n请点「重试加载」；若仍失败，请运行「1-装好就能用.bat」重新安装。\n当前状态下陌生设备只记录、不锁屏。";
            lbl.SetBounds(16, 14, 470, 110);
            lbl.Font = new Font("Microsoft YaHei UI", 10f);
            f.Controls.Add(lbl);
            Button bRetry = MakeBtn("重试加载", 16, 135, 110);
            bRetry.Click += delegate {
                bool ok = LoadWhiteV2();
                if (ok) {
                    whitelistHealthy = true;
                    Log("INFO", "GUARD_STATE", null, "whitelist recovered, guard resumed");
                    UpdateTrayText();
                    try { f.Close(); } catch { }
                } else {
                    MessageBox.Show(f, "仍然加载失败，请检查 usb-whitelist.json 内容或重新安装。", "USB 哨兵", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            Button bLog = MakeBtn("查看日志", 138, 135, 110);
            bLog.Click += delegate { OpenLog(); };
            Button bClose = MakeBtn("关闭", 260, 135, 110);
            bClose.Click += delegate { f.Close(); };
            f.Controls.Add(bRetry); f.Controls.Add(bLog); f.Controls.Add(bClose);
            f.FormClosed += delegate { repairForm = null; };
            repairForm = f;
            f.Show();
        } catch { }
    }

    static void BuildMenu() {
        menu.Items.Clear();
        bool paused = DateTime.Now < pausedUntil;
        bool repairing = DateTime.Now < repairUntil;
        string statusText = !whitelistHealthy ? "状态: 白名单异常（自动锁屏已暂停）" : (repairing ? "状态: 维修模式" : (paused ? "状态: 已暂停" : "状态: 守卫中"));
        ToolStripMenuItem st = new ToolStripMenuItem(statusText);
        st.Enabled = false;
        menu.Items.Add(st);
        ToolStripMenuItem trust = new ToolStripMenuItem("信任当前所有已插设备");
        trust.Click += delegate { TrustCurrent(); };
        menu.Items.Add(trust);
        ToolStripMenuItem pause = new ToolStripMenuItem(paused ? "恢复守卫" : "暂停守卫 30 分钟");
        pause.Click += delegate {
            if (DateTime.Now < pausedUntil) { pausedUntil = DateTime.MinValue; Log("INFO", "GUARD_STATE", null, "resumed from pause"); }
            else Pause30();
            UpdateTrayText();
        };
        menu.Items.Add(pause);
        ToolStripMenuItem repair = new ToolStripMenuItem(repairing ? "退出维修模式" : "维修模式（最长 2 小时）");
        repair.Click += delegate {
            if (repairing) { repairUntil = DateTime.MinValue; Log("INFO", "GUARD_STATE", null, "repair mode off"); }
            else { repairUntil = DateTime.Now.AddHours(2); Log("INFO", "GUARD_STATE", null, "repair mode on (max 2h)"); }
            UpdateTrayText();
        };
        menu.Items.Add(repair);
        ToolStripMenuItem lockm = new ToolStripMenuItem("陌生设备自动锁屏");
        lockm.Checked = lockOnUnknown;
        lockm.Click += delegate { lockOnUnknown = !lockOnUnknown; SaveCfgOrElevate(); Log("INFO", "CONFIG_CHANGE", null, "lockOnUnknown=" + lockOnUnknown); };
        menu.Items.Add(lockm);
        ToolStripMenuItem persist = new ToolStripMenuItem("陌生设备持续锁屏（不拔掉就一直锁）");
        persist.Checked = persistentLock;
        persist.Click += delegate {
            persistentLock = !persistentLock;
            SaveCfgOrElevate();
            Log("INFO", "CONFIG_CHANGE", null, "persistentLock=" + persistentLock);
            if (!persistentLock) CloseAlertForm();
        };
        menu.Items.Add(persist);
        ToolStripMenuItem scanAuto = new ToolStripMenuItem("插入U盘自动扫描（多杀软）");
        scanAuto.Checked = autoScanRemovable;
        scanAuto.Click += delegate { autoScanRemovable = !autoScanRemovable; SaveCfgOrElevate(); Log("INFO", "CONFIG_CHANGE", null, "autoScanRemovable=" + autoScanRemovable); };
        menu.Items.Add(scanAuto);
        ToolStripMenuItem scanNow = new ToolStripMenuItem("立即扫描当前U盘");
        scanNow.Click += delegate {
            HashSet<string> drives = RemovableDrives();
            if (drives.Count == 0) { notify.BalloonTipTitle = "USB 哨兵"; notify.BalloonTipText = "当前没有可移动磁盘"; notify.ShowBalloonTip(3000); }
            foreach (string d in drives) StartScanAsync(d, true);
        };
        menu.Items.Add(scanNow);
        ToolStripMenuItem devMgr = new ToolStripMenuItem("设备管理…");
        devMgr.Click += delegate { DeviceManagerForm.ShowManager(null); };
        menu.Items.Add(devMgr);
        ToolStripMenuItem syncAllow = new ToolStripMenuItem("同步系统 USB 锁定（需管理员）");
        syncAllow.Click += delegate { ElevateSyncAllow(); };
        menu.Items.Add(syncAllow);
        ToolStripMenuItem log = new ToolStripMenuItem("查看日志");
        log.Click += delegate { OpenLog(); };
        menu.Items.Add(log);
        ToolStripMenuItem auto = new ToolStripMenuItem("开机自启");
        auto.Checked = Autostart();
        auto.Click += delegate { ToggleAutostart(); };
        menu.Items.Add(auto);
        menu.Items.Add(new ToolStripSeparator());
        ToolStripMenuItem quit = new ToolStripMenuItem("退出（看护仍会自动重启，需结束计划任务才彻底停止）");
        quit.Click += delegate {
            Log("QUIT by user, watchdog disabled until manual start");
            try { notify.Visible = false; } catch { }
            Application.Exit();
        };
        menu.Items.Add(quit);
        UpdateTrayText();
    }

    static bool IsWorkstationLocked() {
        try {
            IntPtr h = OpenInputDesktop(0, false, 0x0100);
            if (h == IntPtr.Zero) return true;
            StringBuilder sb = new StringBuilder(256);
            uint needed;
            bool ok = GetUserObjectInformation(h, 2, sb, (uint)(sb.Capacity * 2), out needed);
            CloseDesktop(h);
            if (!ok) return true;
            return !sb.ToString().Equals("Default", StringComparison.OrdinalIgnoreCase);
        } catch { return false; }
    }

    static List<string> UnknownKeys(HashSet<string> now) {
        List<string> list = new List<string>();
        foreach (string k in now) if (!IsTrustedId(k)) list.Add(k);
        return list;
    }

    static void CheckPersistent() {
        try {
            if (deviceDirty) { deviceDirty = false; ProcessDeviceChanges(); }
            if (repairUntil != DateTime.MinValue && DateTime.Now >= repairUntil) {
                repairUntil = DateTime.MinValue;
                Log("INFO", "GUARD_STATE", null, "repair mode expired, guard resumed");
            }
            UpdateTrayText();
            if (!whitelistHealthy) { ShowRepairFormOnce(); CloseAlertForm(); SetIcon(false); return; }
            if (!persistentLock || DateTime.Now < repairUntil || DateTime.Now < pausedUntil) { CloseAlertForm(); return; }
            Dictionary<string, string[]> snap = SnapshotV2();
            if (snap == null) return;
            HashSet<string> now = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string k in snap.Keys) { if (IsMonitorableId(k)) now.Add(k); }
            List<string> unk = UnknownKeys(now);
            if (unk.Count == 0) { CloseAlertForm(); SetIcon(false); return; }
            SetIcon(true);
            if (alertForm != null) return;
            if (IsWorkstationLocked()) return;
            Log("ALERT present: " + unk.Count + " untrusted device(s), show warning dialog");
            ShowAlertForm(unk, snap, false, null);
        } catch { }
    }

    static string NamesOf(List<string> keys, Dictionary<string, string[]> snap) {
        StringBuilder sb = new StringBuilder();
        foreach (string k in keys) {
            if (sb.Length > 0) sb.Append("、");
            string n = NameOfId(snap, k);
            sb.Append(string.IsNullOrEmpty(n) ? k : n).Append(" (").Append(VidPidOf(k)).Append(")");
        }
        return sb.ToString();
    }

    static Button MakeBtn(string text, int x, int y, int w) {
        Button b = new Button();
        b.Text = text;
        b.SetBounds(x, y, w, 32);
        return b;
    }

    static void CloseAlertForm() {
        try {
            if (alertForm != null) {
                Form f = alertForm;
                alertForm = null;
                System.Windows.Forms.Timer t = f.Tag as System.Windows.Forms.Timer;
                if (t != null) t.Stop();
                f.Hide();
                f.Dispose();
            }
        } catch { }
    }

    static void TrustGroupAndSave(List<string> unknown, Dictionary<string, string[]> snap) {
        List<string> ids = new List<string>();
        foreach (string k in unknown) {
            string gk = GroupKeyOf(k);
            if (!ids.Contains(k)) ids.Add(k);
            if (snap != null) {
                foreach (string kk in snap.Keys) {
                    if (IsMonitorableId(kk) && !IsTrustedId(kk) && string.Equals(GroupKeyOf(kk), gk, StringComparison.OrdinalIgnoreCase) && !ids.Contains(kk)) ids.Add(kk);
                }
            }
        }
        string name = snap != null ? NameOfId(snap, unknown[0]) : null;
        AddTrustedIds(ids, name);
        bool saved = SaveWhiteV2();
        if (!saved) {
            int rc = RunElevatedSelf("--trust-ids " + string.Join(",", ids.ToArray()));
            LoadWhiteV2();
            saved = (rc == 0);
        }
        if (saved) {
            Log("INFO", "DEVICE_TRUST", string.Join(";", ids.ToArray()), "alert dialog trust, +" + ids.Count);
            // 白名单已更新，系统级 AllowInstanceIDs 也要同步，否则策略守护任务失败时
            // 该设备仍会被 DenyUnspecified 拦住装不上驱动。提权同步是异步的、失败不阻断 UI。
            try { ElevateSyncAllow(); } catch { }
            CloseAlertForm();
        } else {
            Log("WARN", "DEVICE_TRUST", null, "alert dialog trust save failed");
            try {
                notify.BalloonTipTitle = "USB 哨兵";
                notify.BalloonTipText = "保存失败，名单未变更";
                notify.BalloonTipIcon = ToolTipIcon.Warning;
                notify.ShowBalloonTip(4000);
            } catch { }
        }
    }

    static void ShowAlertForm(List<string> unknown, Dictionary<string, string[]> snap, bool testMode, string testCategory) {
        try {
            CloseAlertForm();
            Form f = new Form();
            f.Text = "USB 哨兵：检测到陌生设备";
            f.FormBorderStyle = FormBorderStyle.FixedDialog;
            f.MaximizeBox = false;
            f.MinimizeBox = false;
            f.StartPosition = FormStartPosition.CenterScreen;
            f.TopMost = true;
            f.Width = 620;
            f.Height = 360;
            f.BackColor = Color.White;

            List<string> cats = new List<string>();
            foreach (string k in unknown) {
                string c;
                if (testCategory != null) c = testCategory;
                else {
                    string cls = (snap != null && snap.ContainsKey(k) && snap[k].Length > 1) ? snap[k][1] : null;
                    c = ClassifyOf(k, cls, false);
                }
                if (!cats.Contains(c)) cats.Add(c);
            }
            string mainCat = cats.Count > 0 ? cats[0] : "other";

            StringBuilder sb = new StringBuilder();
            if (testMode) sb.Append("【测试模式】\n\n");
            sb.Append("⚠ 检测到陌生").Append(CategoryName(mainCat)).Append("！\n\n");
            sb.Append("设备：").Append(NamesOf(unknown, snap)).Append("\n\n");
            foreach (string c in cats) sb.Append("· ").Append(CategoryAdvice(c)).Append("\n");
            sb.Append("\n确认安全可点「信任此设备」；也可「暂停守卫 30 分钟」后自行处理。");

            Label lbl = new Label();
            lbl.Text = sb.ToString();
            lbl.SetBounds(16, 14, 576, 170);
            lbl.Font = new Font("Microsoft YaHei UI", 10f);
            f.Controls.Add(lbl);

            List<string> unkRef = unknown;
            Dictionary<string, string[]> snapRef = snap;
            Button bTrust = MakeBtn("信任此设备", 16, 195, 130);
            bTrust.Click += delegate {
                if (testMode) { Log("INFO", "TEST", null, "test prompt: trust clicked (no-op)"); CloseAlertForm(); Application.Exit(); return; }
                TrustGroupAndSave(unkRef, snapRef);
            };
            Button bPause = MakeBtn("暂停 30 分钟", 158, 195, 130);
            bPause.Click += delegate {
                if (testMode) { Log("INFO", "TEST", null, "test prompt: pause clicked (no-op)"); CloseAlertForm(); Application.Exit(); return; }
                Pause30(); Log("ALERT dialog: pause clicked"); CloseAlertForm();
            };
            Button bMgr = MakeBtn("管理白名单", 300, 195, 120);
            bMgr.Click += delegate {
                if (testMode) return;
                DeviceManagerForm.ShowManager(f);
            };
            Button bLock = MakeBtn("立即锁定", 432, 195, 110);
            bLock.Click += delegate {
                if (testMode) { Log("INFO", "TEST", null, "test prompt: lock clicked (no-op)"); CloseAlertForm(); Application.Exit(); return; }
                Log("INFO", "LOCK_TRIGGER", null, "alert dialog lock clicked");
                CloseAlertForm(); try { LockWorkStation(); } catch { }
            };
            f.Controls.Add(bTrust);
            f.Controls.Add(bPause);
            f.Controls.Add(bMgr);
            f.Controls.Add(bLock);

            Label cd = new Label();
            cd.SetBounds(16, 245, 576, 40);
            cd.ForeColor = Color.Firebrick;
            f.Controls.Add(cd);

            int left = testMode ? 6 : 25;
            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = 1000;
            t.Tick += delegate {
                left--;
                if (testMode) {
                    cd.Text = "（测试模式）" + left + " 秒后自动关闭";
                    if (left <= 0) {
                        t.Stop();
                        Log("TEST PROMPT: dialog auto-closed OK");
                        CloseAlertForm();
                        Application.Exit();
                    }
                } else {
                    cd.Text = left + " 秒后自动锁定屏幕。要正常使用请先拔掉陌生设备。";
                    if (left <= 0) {
                        t.Stop();
                        Log("ALERT dialog timeout -> locking screen again");
                        CloseAlertForm();
                        try { LockWorkStation(); } catch { }
                    }
                }
            };
            f.FormClosing += delegate (object s, FormClosingEventArgs e2) {
                if (!testMode && e2.CloseReason == CloseReason.UserClosing) {
                    Log("ALERT dialog closed by user -> locking screen");
                    e2.Cancel = true;
                    t.Stop();
                    CloseAlertForm();
                    try { LockWorkStation(); } catch { }
                }
            };
            f.Tag = t;
            alertForm = f;
            f.Show();
            t.Start();
        } catch (Exception ex) { Log("ERR alert form: " + ex.Message); }
    }

    static HashSet<string> RemovableDrives() {
        HashSet<string> s = new HashSet<string>();
        try {
            ManagementObjectSearcher q = new ManagementObjectSearcher("SELECT DeviceID FROM Win32_LogicalDisk WHERE DriveType=2");
            foreach (ManagementObject o in q.Get()) {
                string d = Convert.ToString(o["DeviceID"]);
                if (!string.IsNullOrEmpty(d)) s.Add(d.ToUpperInvariant());
            }
        } catch { }
        return s;
    }

    static void CheckRemovable() {
        try {
            HashSet<string> now = RemovableDrives();
            foreach (string d in now) {
                if (lastRemovable.Contains(d)) continue;
                Log("INFO", "DEVICE_INSERT", d, "removable drive mounted");
                notify.BalloonTipTitle = "USB 哨兵: 检测到可移动磁盘";
                notify.BalloonTipText = d + " 已插入，建议先杀毒扫描再打开。";
                notify.BalloonTipIcon = ToolTipIcon.Info;
                notify.ShowBalloonTip(6000);
                if (autoScanRemovable) StartScanAsync(d, false);
            }
            lastRemovable = now;
        } catch { }
    }

    static GraphicsPath RoundedRect(Rectangle r, int radius) {
        GraphicsPath p = new GraphicsPath();
        int d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static void SetIcon(bool alert) {
        try {
            using (Bitmap bmp = new Bitmap(32, 32))
            using (Graphics g = Graphics.FromImage(bmp)) {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);
                Color top = alert ? Color.FromArgb(255, 235, 92, 100) : Color.FromArgb(255, 59, 130, 246);
                Color bottom = alert ? Color.FromArgb(255, 190, 44, 52) : Color.FromArgb(255, 37, 99, 235);
                using (GraphicsPath path = RoundedRect(new Rectangle(0, 0, 31, 31), 8))
                using (LinearGradientBrush br = new LinearGradientBrush(new Rectangle(0, 0, 31, 32), top, bottom, 90f)) {
                    g.FillPath(br, path);
                    using (Pen pen = new Pen(Color.FromArgb(90, 255, 255, 255), 1f)) g.DrawPath(pen, path);
                }
                using (Font f = new Font("Segoe UI", 15, FontStyle.Bold, GraphicsUnit.Pixel))
                using (StringFormat sf = new StringFormat()) {
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    g.DrawString("U", f, Brushes.White, new RectangleF(0, 0, 31, 31), sf);
                }
                IntPtr h = bmp.GetHicon();
                Icon ico = (Icon)Icon.FromHandle(h).Clone();
                DestroyIcon(h);
                if (cur != null) cur.Dispose();
                cur = ico;
                notify.Icon = ico;
            }
        } catch { }
    }
}

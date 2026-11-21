using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

internal sealed class GuardRuntime : IDisposable {
    private readonly string baseDirectory;
    private readonly SynchronizationContext ui;
    private readonly object documentSync = new object();
    private readonly object operationSync = new object();
    private readonly GuardStateCoordinator state;
    private readonly GuardStateStore stateStore;
    private readonly DeviceInventory inventory;
    private readonly MaintenanceService maintenance;
    private readonly ProtectionControlService protection;
    private readonly ScanCoordinator scan;
    private readonly MountGenerationTracker mountGenerations = new MountGenerationTracker();
    private readonly DiagnosticsStore diagnostics;
    private readonly ShellTrayController tray;
    private readonly AlertPresenter alerts;
    private readonly HashSet<string> pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> pendingMutations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> lockedGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly System.Windows.Forms.Timer persistentTimer;
    private readonly System.Windows.Forms.Timer refreshTimer;
    private MainForm mainForm;
    private ManagementEventWatcher watcher;
    private ConfigDocument configDocument = new ConfigDocument();
    private WhitelistDocument whitelistDocument = new WhitelistDocument();
    private DeviceSnapshot lastSnapshot = new DeviceSnapshot();
    private List<PhysicalDevice> unknownDevices = new List<PhysicalDevice>();
    private HashSet<string> knownDrives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private BuildInfo buildInfo = new BuildInfo { ProductVersion = BuildInfoProvider.ProductVersion, BuildDate = BuildInfoProvider.BuildDate, Consistency = "待核验" };
    private string operationMessage = "";
    private int refreshInFlight;
    private int refreshPending;
    private bool drivesInitialized;
    private bool disposed;
    private bool initialized;

    internal GuardRuntime(string programDirectory, SynchronizationContext context) {
        baseDirectory = programDirectory;
        ui = context;
        string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "USBGuard");
        diagnostics = new DiagnosticsStore(local);
        stateStore = new GuardStateStore(baseDirectory);
        state = new GuardStateCoordinator(stateStore);
        inventory = new DeviceInventory();
        maintenance = new MaintenanceService(baseDirectory, inventory, ui);
        protection = new ProtectionControlService(baseDirectory, ui);
        scan = new ScanCoordinator(GetEngineOrder);
        tray = new ShellTrayController();
        alerts = new AlertPresenter(TrustDevice, ExecuteCommand, delegate { RequestLock(true); });
        state.StateChanged += OnStateChanged;
        state.RefreshRequested += delegate { RequestDeviceRefresh(); };
        scan.StateChanged += OnScanChanged;
        tray.CommandRequested += ExecuteCommand;
        tray.RecoveryWarning += delegate(string message) {
            operationMessage = message;
            diagnostics.Write(OperationFeedback.FromResult("Tray", "TRAY_RECOVERY", OperationResult.Create(OperationResultKind.Failed, message)));
            ShowMain();
            tray.ShowNotification("USB 哨兵：托盘暂不可用", message, ToolTipIcon.Warning);
        };
        persistentTimer = new System.Windows.Forms.Timer();
        persistentTimer.Interval = 500;
        persistentTimer.Tick += TickUi;
        refreshTimer = new System.Windows.Forms.Timer();
        refreshTimer.Interval = 5000;
        refreshTimer.Tick += delegate { RequestDeviceRefresh(); };
    }

    internal void Start(bool showMain) {
        TrayOperationResult trayResult = tray.EnsureVisible();
        if (!trayResult.Success) {
            MessageBox.Show("启动未完成：托盘创建失败。\n" + trayResult.Message + "\n\n防护初始化仍会继续；请使用主面板重试或运行安装修复。", "USB 哨兵", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        persistentTimer.Start();
        refreshTimer.Start();
        if (showMain) ShowMain();
        ThreadPool.QueueUserWorkItem(delegate { InitializeBackground(); });
    }

    private void InitializeBackground() {
        ConfigStore configStore = new ConfigStore(Path.Combine(baseDirectory, "usb-guard-config.json"));
        WhitelistStore whitelistStore = new WhitelistStore(Path.Combine(baseDirectory, "usb-whitelist.json"));
        ConfigLoadResult config = configStore.Load();
        WhitelistLoadResult whitelist = whitelistStore.Load();
        BuildInfo info = BuildInfoProvider.GetBuildInfo();
        ui.Post(delegate {
            if (disposed) return;
            lock (documentSync) {
                configDocument = config.Document;
                whitelistDocument = whitelist.Document;
            }
            buildInfo = info;

            tray.ApplyConfig(configDocument);
            if (!config.Healthy) operationMessage = config.Message;
        }, null);
        state.Initialize(protection.StopFlagPresent, whitelist.Healthy, whitelist.Message);
        if (!whitelist.Healthy) WriteLog("WHITELIST", OperationResultKind.Failed, whitelist.Message, "");
        if (!config.Healthy) WriteLog("CONFIG", OperationResultKind.Failed, config.Message, "");
        ui.Post(delegate {
            initialized = true;
            ApplyViews();
            if (!protection.StopFlagPresent) {
                ThreadPool.QueueUserWorkItem(delegate {
                    StartWatcher();
                    RequestDeviceRefresh();
                });
            }
        }, null);
    }

    private void StartWatcher() {
        if (disposed || watcher != null || state.Current.Mode == GuardMode.Stopped) return;
        try {
            watcher = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM Win32_DeviceChangeEvent WITHIN 1"));
            watcher.EventArrived += delegate { RequestDeviceRefresh(); };
            watcher.Start();
            WriteLog("WMI_WATCH", OperationResultKind.Success, "设备事件看护已启动", "");
        } catch (Exception ex) {
            if (watcher != null) { watcher.Dispose(); watcher = null; }
            WriteLog("WMI_WATCH", OperationResultKind.Failed, "事件看护失败，5 秒后台快照兜底仍有效：" + ex.Message, "");
        }
    }

    private void RequestDeviceRefresh() {
        if (disposed || !initialized || state.Current.Mode == GuardMode.Stopped) return;
        if (Interlocked.CompareExchange(ref refreshInFlight, 1, 0) != 0) {
            Interlocked.Exchange(ref refreshPending, 1);
            return;
        }
        ui.Post(delegate { if (!disposed) { operationMessage = "正在后台刷新设备"; ApplyViews(); } }, null);
        ThreadPool.QueueUserWorkItem(delegate {
            DeviceSnapshot snapshot = null;
            try {
                snapshot = inventory.CaptureSnapshot();
                ProcessSnapshot(snapshot);
            } catch (Exception ex) {
                WriteLog("DEVICE_REFRESH", OperationResultKind.Failed, ex.Message, "");
                ui.Post(delegate {
                    if (lastSnapshot == null || !lastSnapshot.Success) state.SetUnknown("设备状态无法确认：" + ex.Message);
                    operationMessage = "刷新失败，保留最近有效状态";
                    ApplyViews();
                }, null);
            } finally {
                Interlocked.Exchange(ref refreshInFlight, 0);
                if (Interlocked.Exchange(ref refreshPending, 0) != 0 && !disposed) RequestDeviceRefresh();
            }
        });
    }

    private void ProcessSnapshot(DeviceSnapshot snapshot) {
        if (snapshot == null || !snapshot.Success) {
            string error = snapshot == null ? "空设备快照" : snapshot.Error;
            ui.Post(delegate {
                if (lastSnapshot == null || !lastSnapshot.Success) state.SetUnknown("设备快照读取失败：" + error);
                operationMessage = "设备刷新失败，保留最近有效状态：" + error;
                ApplyViews();
            }, null);
            return;
        }
        WhitelistDocument document = CurrentWhitelist();
        List<PhysicalDevice> unknown = new List<PhysicalDevice>();
        foreach (PhysicalDevice group in snapshot.Devices) if (!IsTrustedGroup(group, document)) unknown.Add(group);
        HashSet<string> previousGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (lastSnapshot != null && lastSnapshot.Success) foreach (PhysicalDevice group in lastSnapshot.Devices) previousGroups.Add(group.GroupKey);
        List<PhysicalDevice> inserted = new List<PhysicalDevice>();
        foreach (PhysicalDevice group in snapshot.Devices) if (!previousGroups.Contains(group.GroupKey)) inserted.Add(group);
        HashSet<string> previousDrives = knownDrives;
        mountGenerations.Observe(snapshot.RemovableDrives);
        List<string> newDrives = new List<string>();
        if (drivesInitialized) foreach (string drive in snapshot.RemovableDrives) if (!previousDrives.Contains(drive)) newDrives.Add(drive);
        knownDrives = new HashSet<string>(snapshot.RemovableDrives, StringComparer.OrdinalIgnoreCase);
        drivesInitialized = true;
        lastSnapshot = snapshot;
        unknownDevices = unknown;
        foreach (PhysicalDevice group in inserted) {
            bool trusted = IsTrustedGroup(group, document);
            WriteLog("DEVICE_INSERT", trusted ? OperationResultKind.Unchanged : OperationResultKind.Success,
                trusted ? "已信任设备接入" : "陌生设备接入", group.GroupKey + " / " + string.Join(";", group.InstanceIds().ToArray()));
        }
        if (state.Current.Mode != GuardMode.Stopped && configDocument.autoScanRemovable) {
            foreach (string drive in newDrives) QueueScan(drive, false);
        }
        ui.Post(delegate {
            if (disposed) return;
            state.SetUnknownDevices(unknown.Count > 0);
            operationMessage = "设备快照已更新（代次 " + snapshot.Generation + "）";
            HashSet<string> currentKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (PhysicalDevice group in unknown) currentKeys.Add(group.GroupKey);
            lockedGroups.RemoveWhere(key => !currentKeys.Contains(key));
            if (state.Current.Mode == GuardMode.Protecting && unknown.Count > 0) alerts.Enqueue(unknown);
            else if (unknown.Count == 0 || state.Current.Mode != GuardMode.Protecting) alerts.DismissAll();
            ApplyViews();
            CheckAutoLock();
        }, null);
    }

    private static bool IsTrustedGroup(PhysicalDevice group, WhitelistDocument document) {
        if (group == null || group.Nodes.Count == 0) return false;
        foreach (DeviceNode node in group.Nodes) if (!document.IsTrusted(node.InstanceId)) return false;
        return true;
    }

    private void TickUi(object sender, EventArgs args) {
        if (disposed) return;
        GuardStateSnapshot snapshot = state.Current;
        if (snapshot.Deadline.HasValue && snapshot.Deadline.Value <= DateTimeOffset.Now) {
            if (BeginOperation("expiration")) {
                ThreadPool.QueueUserWorkItem(delegate {
                    state.Tick(DateTimeOffset.Now);
                    ui.Post(delegate { EndOperation("expiration"); }, null);
                });
            }
        }
        if (snapshot.Mode == GuardMode.Protecting && configDocument.persistentLock) CheckAutoLock();
        ApplyViews();
    }

    private void CheckAutoLock() {
        if (!configDocument.persistentLock) {
            foreach (PhysicalDevice group in unknownDevices) if (!lockedGroups.Contains(group.GroupKey)) { RequestLock(false); break; }
        } else RequestLock(false);
    }

    private void RequestLock(bool userInitiated) {
        if (disposed) return;
        if (!userInitiated) {
            if (!state.Current.IsBlockingEnabled() || !configDocument.lockOnUnknown || lastSnapshot == null || !lastSnapshot.Success || unknownDevices.Count == 0) return;
            if (IsWorkstationLocked()) return;
        }
        try {
            bool ok = LockWorkStation();
            foreach (PhysicalDevice group in unknownDevices) lockedGroups.Add(group.GroupKey);
            WriteLog("LOCK_TRIGGER", ok ? OperationResultKind.Success : OperationResultKind.Failed,
                userInitiated ? "用户请求立即锁屏" : (ok ? "陌生设备自动锁屏" : "系统锁屏调用返回失败"), "");
        } catch (Exception ex) {
            WriteLog("LOCK_TRIGGER", OperationResultKind.Failed, "锁屏调用失败：" + ex.Message, "");
        }
    }

    private void ExecuteCommand(ShellCommand command) {
        if (command == null || disposed) return;
        switch (command.Kind) {
            case ShellCommandKind.ShowMain: ShowMain(); break;
            case ShellCommandKind.DeviceManager: ShowDeviceManager(); break;
            case ShellCommandKind.ScanNow: ScanCurrentDrives(); break;
            case ShellCommandKind.Pause30: RunStateCommand("pause", delegate { return state.RequestPause(TimeSpan.FromMinutes(30), StateChangeSource.MainPanel); }); break;
            case ShellCommandKind.Pause120: RunStateCommand("pause", delegate { return state.RequestPause(TimeSpan.FromHours(2), StateChangeSource.MainPanel); }); break;
            case ShellCommandKind.Repair120: RunStateCommand("repair", delegate { return state.RequestRepair(TimeSpan.FromHours(2), StateChangeSource.MainPanel); }); break;
            case ShellCommandKind.Resume: RunStateCommand("resume", delegate { return state.ResumeProtection(StateChangeSource.MainPanel); }); break;
            case ShellCommandKind.StopProtection: ChangeProtection(ProtectionAction.Stop); break;
            case ShellCommandKind.EnableProtection: ChangeProtection(ProtectionAction.Enable); break;
            case ShellCommandKind.ShowLog: OpenLog(); break;
            case ShellCommandKind.Retry: ReloadDataBackground(true); RequestDeviceRefresh(); break;
            case ShellCommandKind.RunInstaller: RunInstaller(); break;
            case ShellCommandKind.ToggleLock: SaveConfig(command.Value, configDocument.persistentLock, configDocument.autoScanRemovable); break;
            case ShellCommandKind.TogglePersistent: SaveConfig(configDocument.lockOnUnknown, command.Value, configDocument.autoScanRemovable); break;
            case ShellCommandKind.ToggleAutoScan: SaveConfig(configDocument.lockOnUnknown, configDocument.persistentLock, command.Value); break;
            case ShellCommandKind.SyncAllow: StartElevatedLegacy("--sync-allow"); break;
            case ShellCommandKind.LockNow: RequestLock(true); break;
            case ShellCommandKind.ExitNormal: NormalExit(); break;
        }
    }

    private void RunStateCommand(string key, Func<OperationResult> action) {
        if (!BeginOperation(key)) return;
        operationMessage = "正在保存运行状态…";
        ApplyViews();
        ThreadPool.QueueUserWorkItem(delegate {
            OperationResult result = action();
            WriteLog("GUARD_STATE_COMMAND", result.Kind, result.Message, "");
            ui.Post(delegate {
                EndOperation(key);
                operationMessage = result.Summary();
                ShowOperationResult(result);
                ApplyViews();
            }, null);
        });
    }

    private void ChangeProtection(ProtectionAction action) {
        if (!BeginOperation("protection")) return;
        if (action == ProtectionAction.Stop) {
            DialogResult answer = MessageBox.Show("彻底停止将停止实时监控、扫描、报警锁屏及四个看护任务，但保留现有注册表加固。\n停止后托盘仍保留，用于重新启用。确定继续吗？",
                "USB 哨兵：彻底停止二次确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) { EndOperation("protection"); return; }
        }
        operationMessage = action == ProtectionAction.Stop ? "正在停止并逐项校验看护…" : "正在启用并逐项校验看护…";
        ShowOperationResult(OperationResult.Create(OperationResultKind.Unchanged, operationMessage));
        protection.ChangeAsync(action, delegate(ProtectionTransitionResult result) {
            EndOperation("protection");
            if (action == ProtectionAction.Stop && result.StopFlagPresent) {
                scan.CancelAll("防护已彻底停止");
                StopWatcher();
                alerts.DismissAll();
            }
            state.ApplyProtectionResult(result);
            if (action == ProtectionAction.Enable && result.Result == OperationResultKind.Success) {
                ReloadDataBackground(true);
                StartWatcher();
                RequestDeviceRefresh();
            }
            operationMessage = OperationFeedback.ProtectionMessage(result);
            OperationResult feedback = OperationResult.Create(result.Result, operationMessage);
            foreach (ControlItemResult item in result.FailedItems) feedback.FailedItems.Add(item);
            ShowOperationResult(feedback);
            WriteLog("PROTECTION_CONTROL", result.Result, operationMessage, "");
            ApplyViews();
        });
    }

    private void TrustDevice(TrustRequest trust) {
        MutationRequest request = new MutationRequest();
        request.Operation = MutationKind.Trust;
        request.Trust = trust;
        request.ExpectedRevision = CurrentWhitelist().Revision;
        StartMutation(request, "trust");
    }

    private void RemoveDevice(WhitelistEntry entry) {
        if (entry == null || entry.deviceIds.Count == 0) return;
        MutationRequest request = new MutationRequest();
        request.Operation = MutationKind.Remove;
        request.EntryInstanceId = entry.deviceIds[0];
        request.ExpectedRevision = CurrentWhitelist().Revision;
        StartMutation(request, "remove");
    }

    private void SaveConfig(bool lockOnUnknown, bool persistentLock, bool autoScan) {
        MutationRequest request = new MutationRequest();
        request.Operation = MutationKind.Config;
        request.ExpectedRevision = CurrentConfig().Revision;
        request.Config = CurrentConfig();
        request.Config.lockOnUnknown = lockOnUnknown;
        request.Config.persistentLock = persistentLock;
        request.Config.autoScanRemovable = autoScan;
        StartMutation(request, "config");
    }

    private void StartMutation(MutationRequest request, string kind) {
        if (!BeginOperation(kind)) {
            if (kind == "remove" && DeviceManagerForm.Current != null) DeviceManagerForm.Current.ShowOperationResult(OperationResult.Create(OperationResultKind.Unchanged, "已有删除操作正在执行"), false);
            return;
        }
        pendingMutations[request.RequestId] = kind;
        maintenance.ApplyAsync(request, OnMutationCompleted);
    }

    private void OnMutationCompleted(MutationResult result) {
        if (disposed) return;
        string kind;
        lock (operationSync) {
            pendingMutations.TryGetValue(result.RequestId, out kind);
            pendingMutations.Remove(result.RequestId);
            if (!string.IsNullOrEmpty(kind)) pending.Remove(kind);
        }
        bool success = result.Kind == OperationResultKind.Success;
        bool unchanged = result.Kind == OperationResultKind.Unchanged;
        if (success) {
            ThreadPool.QueueUserWorkItem(delegate {
                WhitelistLoadResult white = new WhitelistStore(Path.Combine(baseDirectory, "usb-whitelist.json")).Load();
                ConfigLoadResult config = new ConfigStore(Path.Combine(baseDirectory, "usb-guard-config.json")).Load();
                ui.Post(delegate {
                    if (!white.Healthy && kind != "config") {
                        result.Kind = OperationResultKind.Failed;
                        result.Message = "保存后名单重读失败，目标报警保持：" + white.Message;
                        success = false;
                    } else {
                        lock (documentSync) {
                            if (kind != "config") whitelistDocument = white.Document;
                            if (kind == "config") configDocument = config.Document;
                        }
                        state.SetWhitelistHealth(white.Healthy, white.Message);
                    }
                    FinishMutation(result, kind, success);
                }, null);
            });
        } else FinishMutation(result, kind, unchanged);
    }

    private void FinishMutation(MutationResult result, string kind, bool refresh) {
        string subject = kind == "config" ? "配置" : "名单";
        operationMessage = OperationFeedback.MutationMessage(result, subject);
        OperationResult feedback = result.ToOperationResult();
        ShowOperationResult(feedback);
        WriteLog(kind == "config" ? "CONFIG_CHANGE" : kind == "remove" ? "DEVICE_REMOVE" : "DEVICE_TRUST", result.Kind, operationMessage, "");
        if (kind == "trust") alerts.TrustCompleted(result);
        if (kind == "remove" && DeviceManagerForm.Current != null) DeviceManagerForm.Current.ShowOperationResult(feedback, refresh);
        tray.ApplyConfig(CurrentConfig());
        ApplyViews();
        if (refresh) RequestDeviceRefresh();
    }

    private void ReloadDataBackground(bool refreshDevices) {
        ThreadPool.QueueUserWorkItem(delegate {
            ConfigLoadResult config = new ConfigStore(Path.Combine(baseDirectory, "usb-guard-config.json")).Load();
            WhitelistLoadResult white = new WhitelistStore(Path.Combine(baseDirectory, "usb-whitelist.json")).Load();
            ui.Post(delegate {
                if (disposed) return;
                lock (documentSync) { configDocument = config.Document; whitelistDocument = white.Document; }
                state.SetWhitelistHealth(white.Healthy, white.Message);
                tray.ApplyConfig(configDocument);
                if (DeviceManagerForm.Current != null && white.Healthy) DeviceManagerForm.Current.Reload();
                operationMessage = white.Healthy ? "名单与配置已刷新" : white.Message;
                ApplyViews();
            }, null);
        });
        if (refreshDevices) RequestDeviceRefresh();
    }

    private void ScanCurrentDrives() {
        DeviceSnapshot snapshot = lastSnapshot;
        if (snapshot == null || snapshot.RemovableDrives.Count == 0) {
            ShowOperationResult(OperationResult.Create(OperationResultKind.Unchanged, "当前没有可移动磁盘；设备列表仍在后台刷新"));
            return;
        }
        foreach (string drive in snapshot.RemovableDrives) QueueScan(drive, true);
    }

    private void QueueScan(string drive, bool manual) {
        try {
            ScanRequest request = new ScanRequest();
            request.DriveRoot = drive;
            request.MountGeneration = mountGenerations.GenerationOf(drive);
            request.Manual = manual;
            request.Source = manual ? StateChangeSource.MainPanel : StateChangeSource.Startup;
            OperationResult accepted = scan.RequestScan(request);
            if (manual) ShowOperationResult(accepted);
        } catch (Exception ex) {
            if (manual) ShowOperationResult(OperationResult.Create(OperationResultKind.Failed, ex.Message));
        }
    }

    private void OnScanChanged(object sender, ScanStateChangedEventArgs args) {
        DiagnosticEvent item = new DiagnosticEvent();
        item.Source = "Scan";
        item.EventType = "SCAN_" + args.Summary.State.ToString();
        item.Result = args.Summary.State == ScanState.Clean ? OperationResultKind.Success
            : args.Summary.State == ScanState.Cancelled ? OperationResultKind.Cancelled
            : args.Summary.State == ScanState.Failed ? OperationResultKind.Failed : OperationResultKind.Unchanged;
        item.TargetSummary = args.Summary.DriveRoot + " / 代次 " + args.Summary.MountGeneration;
        item.Message = args.Summary.LastResult;
        Write(item);
        ui.Post(delegate { if (!disposed) ApplyViews(); }, null);
    }

    internal void ShowMain() {
        if (disposed) return;
        if (mainForm == null || mainForm.IsDisposed) mainForm = new MainForm(ExecuteCommand);
        mainForm.ShowOrActivate(BuildPanelSnapshot());
    }

    private void ShowDeviceManager() {
        DeviceManagerForm.ShowManager(mainForm, CurrentWhitelist, RemoveDevice, delegate { ReloadDataBackground(true); });
    }

    private MainPanelSnapshot BuildPanelSnapshot() {
        MainPanelSnapshot snapshot = new MainPanelSnapshot();
        snapshot.Guard = state.Current;
        snapshot.UnknownCount = unknownDevices.Count;
        snapshot.TrustedDeviceCount = CurrentWhitelist().TrustedCount();
        snapshot.Config = CurrentConfig();
        snapshot.Scans = scan.GetSummaries();
        snapshot.RecentEvents = diagnostics.GetRecentKeyEvents(20);
        snapshot.Build = buildInfo;
        snapshot.LogHealth = diagnostics.HealthMessage;
        snapshot.Refreshing = refreshInFlight != 0 || !initialized;
        snapshot.OperationMessage = operationMessage;
        return snapshot;
    }

    private void ApplyViews() {
        if (disposed) return;
        tray.ApplyState(state.Current);
        tray.ApplyConfig(CurrentConfig());
        if (mainForm != null && !mainForm.IsDisposed && mainForm.Visible) mainForm.ApplyState(BuildPanelSnapshot());
    }

    private void OnStateChanged(object sender, GuardStateChangedEventArgs args) {
        WriteState(args);
        GuardStateSnapshot snapshot = args.Current.Clone();
        ThreadPool.QueueUserWorkItem(delegate { stateStore.WriteCurrentState(snapshot); });
        ui.Post(delegate {
            if (disposed) return;
            if (snapshot.Mode != GuardMode.Protecting) alerts.DismissAll();
            ApplyViews();
        }, null);
    }

    private void WriteState(GuardStateChangedEventArgs args) {
        diagnostics.WriteState(args.Current.Source, args.Previous, args.Current, args.Current.Result, "");
    }

    private void WriteLog(string eventType, OperationResultKind result, string message, string target) {
        DiagnosticEvent item = new DiagnosticEvent();
        item.EventType = eventType;
        item.Result = result;
        item.Message = message;
        item.TargetSummary = target;
        item.Source = "Runtime";
        item.Level = result == OperationResultKind.Failed || result == OperationResultKind.Partial ? "WARN" : "INFO";
        Write(item);
    }

    private void Write(DiagnosticEvent item) { diagnostics.Write(item); }

    private void ShowOperationResult(OperationResult result) {
        if (mainForm != null && !mainForm.IsDisposed) mainForm.ShowOperationResult(result);
        if (result.Kind == OperationResultKind.Failed || result.Kind == OperationResultKind.Partial)
            tray.ShowNotification("USB 哨兵：" + GuardText.ResultName(result.Kind), result.Summary(), ToolTipIcon.Warning);
    }

    private bool BeginOperation(string key) {
        lock (operationSync) { if (pending.Contains(key)) return false; pending.Add(key); return true; }
    }
    private void EndOperation(string key) { lock (operationSync) pending.Remove(key); }

    private ConfigDocument CurrentConfig() { lock (documentSync) return configDocument.Clone(); }
    private WhitelistDocument CurrentWhitelist() { lock (documentSync) return whitelistDocument.Clone(); }
    private string[] GetEngineOrder() { lock (documentSync) return (string[])configDocument.scanEngineOrder.Clone(); }

    private void OpenLog() {
        try { Process.Start("notepad.exe", diagnostics.LogPath); }
        catch (Exception ex) { ShowOperationResult(OperationResult.Create(OperationResultKind.Failed, "日志打开失败：" + ex.Message)); }
    }

    private void RunInstaller() {
        try {
            string root = Directory.GetParent(baseDirectory).FullName;
            ProcessStartInfo start = new ProcessStartInfo(Path.Combine(root, "1-装好就能用.bat"));
            start.WorkingDirectory = root;
            start.UseShellExecute = true;
            Process.Start(start);
            operationMessage = "已请求运行安装修复，请以安装窗口实际结果为准";
        } catch (Exception ex) { ShowOperationResult(OperationResult.Create(OperationResultKind.Failed, "安装修复启动失败：" + ex.Message)); }
    }

    private void StartElevatedLegacy(string arguments) {
        try {
            ProcessStartInfo start = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName);
            start.Arguments = arguments;
            start.WorkingDirectory = baseDirectory;
            start.UseShellExecute = true;
            start.Verb = "runas";
            start.WindowStyle = ProcessWindowStyle.Hidden;
            Process process = Process.Start(start);
            if (process == null) throw new InvalidOperationException("提权进程未创建");
            process.EnableRaisingEvents = true;
            process.Exited += delegate {
                WriteLog("SYNC_ALLOW", process.ExitCode == 0 ? OperationResultKind.Success : OperationResultKind.Failed, "系统策略同步 exit=" + process.ExitCode, "");
                process.Dispose();
            };
            operationMessage = "已请求管理员策略同步，完成结果见日志";
        } catch (Exception ex) { ShowOperationResult(OperationResult.Create(OperationResultKind.Failed, "提权请求失败：" + ex.Message)); }
    }

    private void NormalExit() {
        if (MessageBox.Show("普通退出只结束当前主程序，不会写停止标记或修改计划任务；看护可能重新启动程序。\n若要长期停止，请使用“彻底停止防护”。确定普通退出吗？",
            "USB 哨兵：普通退出确认", MessageBoxButtons.YesNo, MessageBoxIcon.Information, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        WriteLog("QUIT", OperationResultKind.Success, "仅退出主程序，看护仍启用", "");
        Dispose();
        Application.Exit();
    }

    private void StopWatcher() {
        if (watcher == null) return;
        try { watcher.Stop(); } catch { }
        try { watcher.Dispose(); } catch { }
        watcher = null;
    }

    [DllImport("user32.dll")] private static extern bool LockWorkStation();
    [DllImport("user32.dll")] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint desiredAccess);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr handle);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder info, uint length, out uint needed);

    private static bool IsWorkstationLocked() {
        try {
            IntPtr handle = OpenInputDesktop(0, false, 0x0100);
            if (handle == IntPtr.Zero) return true;
            StringBuilder info = new StringBuilder(256);
            uint needed;
            bool ok = GetUserObjectInformation(handle, 2, info, (uint)(info.Capacity * 2), out needed);
            CloseDesktop(handle);
            return !ok || !info.ToString().Equals("Default", StringComparison.OrdinalIgnoreCase);
        } catch { return false; }
    }

    public void Dispose() {
        if (disposed) return;
        disposed = true;
        persistentTimer.Stop(); persistentTimer.Dispose();
        refreshTimer.Stop(); refreshTimer.Dispose();
        StopWatcher();
        alerts.DismissAll();
        scan.Dispose();
        if (mainForm != null && !mainForm.IsDisposed) { mainForm.ExitFromRuntime(); mainForm.Close(); mainForm.Dispose(); }
        tray.Dispose();
        diagnostics.Dispose();
    }
}
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

internal static class SelfTests {
    private static readonly List<string> lines = new List<string>();
    private static int failed;

    internal static int Run() {
        failed = 0;
        lines.Clear();
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "USBGuard", "self-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        RunCase("命令行强类型解析与废弃扩信拒绝", delegate {
            CommandLineOptions options; string error;
            Check(UsbGuard.TryParseCommandLine(new string[] { "--show-main" }, out options, out error) && options.ShowMain, "--show-main 应只请求主面板");
            Check(!UsbGuard.TryParseCommandLine(new string[] { "--trust-current" }, out options, out error), "自动扩信入口必须拒绝");
            Check(UsbGuard.TryParseCommandLine(new string[] { "--apply-mutation", "C:\\outside.json" }, out options, out error), "参数结构应先接受，固定目录校验由受限入口执行");
        });
        RunCase("完整实例精确信任", delegate {
            WhitelistDocument document = new WhitelistDocument();
            WhitelistEntry entry = new WhitelistEntry();
            entry.deviceIds.Add("USB\\VID_1234&PID_5678\\SERIAL_A");
            document.Entries.Add(entry);
            Check(document.IsTrusted("  USB\\VID_1234&PID_5678\\SERIAL_A  "), "Trim + OrdinalIgnoreCase 应命中完整 ID");
            Check(!document.IsTrusted("USB\\VID_1234&PID_5678\\SERIAL_B"), "同型号不同实例不得命中");
            Check(!document.IsTrusted("USB\\VID_1234&PID_5678&MI_00\\SERIAL_A"), "MI 变体不得使用父 ID 捷径");
        });
        RunCase("物理归组证据与冲突降级", delegate {
            DeviceNode a = Node("USB\\VID_1234&PID_5678\\A", "", "");
            DeviceNode b = Node("USB\\VID_1234&PID_5678\\B", "", "");
            Check(DeviceGrouper.Group(new DeviceNode[] { a, b }, 1).Count == 2, "同型号无证据实例必须分组");
            DeviceNode c = Node("USB\\VID_1234&PID_5678&MI_00\\C", "11111111-1111-1111-1111-111111111111", "USB\\PARENT\\C");
            DeviceNode d = Node("HID\\VID_1234&PID_5678&MI_00\\D", "11111111-1111-1111-1111-111111111111", "USB\\PARENT\\C");
            Check(DeviceGrouper.Group(new DeviceNode[] { c, d }, 1).Count == 1, "一致 ContainerId 应归组");
            DeviceNode e = Node("USB\\VID_1234&PID_5678\\E", "", "USB\\PARENT\\X");
            DeviceNode f = Node("HID\\VID_1234&PID_5678\\F", "22222222-2222-2222-2222-222222222222", "USB\\PARENT\\X");
            Check(DeviceGrouper.Group(new DeviceNode[] { e, f }, 1).Count == 2, "父链与 ContainerId 证据不足/冲突不得猜测合并");
            PhysicalDevice same = DeviceGrouper.Group(new DeviceNode[] { c, d }, 1)[0];
            DeviceSnapshot newer = new DeviceSnapshot { Success = true, Generation = 2 };
            newer.Devices.Add(same);
            TrustRequest target = new TrustRequest { GroupKey = same.GroupKey, SnapshotGeneration = 1, ExpectedInstanceIds = same.InstanceIds() };
            Check(new DeviceInventory().ResolveTarget(target, newer) != null, "更新代次但证据与接口完全相同的目标应允许复核保存");
        });
        RunCase("旧名单混合异常无损且读取不改哈希", delegate {
            string path = Path.Combine(root, "white-lossless.json");
            string json = "[\"USB\\\\VID_1234&PID_5678\\\\A\",{\"description\":\"历史异常\"}]";
            File.WriteAllText(path, json, new UTF8Encoding(false));
            string before = BuildInfoProvider.Sha256File(path);
            WhitelistStore store = new WhitelistStore(path);
            WhitelistLoadResult loaded = store.Load();
            Check(loaded.Healthy && loaded.Document.Entries.Count == 2, "混合格式应可读且条目不丢失");
            Check(loaded.Document.Entries[1].Kind == WhitelistEntryKind.Unrecognized, "异常对象必须标记未识别");
            Check(before == BuildInfoProvider.Sha256File(path), "单纯加载不得写文件或扩信");
            AtomicWriteResult saved = store.Save(loaded.Document.Clone(), loaded.Document.Revision);
            Check(saved.Kind == OperationResultKind.Success, "显式保存应成功");
            WhitelistLoadResult reloaded = store.Load();
            Check(reloaded.Document.Entries.Count == 2 && reloaded.Document.Entries[1].Kind == WhitelistEntryKind.Unrecognized, "保存后历史异常仍须保留");
            AtomicWriteResult repeated = store.Save(reloaded.Document.Clone(), reloaded.Document.Revision);
            Check(repeated.Kind == OperationResultKind.Unchanged, "重复相同变更不得重写文件");
        });
        RunCase("原子保存冲突、写失败与克隆边界", delegate {
            string path = Path.Combine(root, "atomic.json");
            File.WriteAllText(path, "{\"v\":1}", new UTF8Encoding(false));
            string oldRevision = AtomicJsonStore.RevisionOf(File.ReadAllText(path));
            File.WriteAllText(path, "{\"v\":2}", new UTF8Encoding(false));
            AtomicWriteResult conflict = AtomicJsonStore.WriteText(path, "{\"v\":3}", oldRevision, true);
            Check(conflict.Kind == OperationResultKind.Conflict && File.ReadAllText(path) == "{\"v\":2}", "修订冲突不得覆盖较新内容");
            string directoryTarget = Path.Combine(root, "directory-target");
            Directory.CreateDirectory(directoryTarget);
            AtomicWriteResult failure = AtomicJsonStore.WriteText(directoryTarget, "data", AtomicJsonStore.RevisionOf(""), false);
            Check(failure.Kind == OperationResultKind.Failed, "目标不可替换时必须返回失败");
            WhitelistDocument original = new WhitelistDocument();
            WhitelistDocument clone = original.Clone();
            clone.Entries.Add(new WhitelistEntry());
            Check(original.Entries.Count == 0, "克隆变化不得污染原内存快照");
        });
        RunCase("暂停持久化、状态优先级与损坏安全恢复", delegate {
            string runtime = Path.Combine(root, "guard-runtime.json");
            string legacy = Path.Combine(root, "legacy.json");
            GuardStateCoordinator first = new GuardStateCoordinator(new GuardStateStore(runtime, legacy));
            first.Initialize(false, true, "");
            OperationResult paused = first.RequestPause(TimeSpan.FromMinutes(30), StateChangeSource.MainPanel);
            Check(paused.Kind == OperationResultKind.Success && first.Current.Mode == GuardMode.Paused, "30 分钟暂停应保存并生效");
            string originalUntil = first.Current.Deadline.Value.ToString("o");
            GuardStateCoordinator restarted = new GuardStateCoordinator(new GuardStateStore(runtime, legacy));
            restarted.Initialize(false, true, "");
            Check(restarted.Current.Mode == GuardMode.Paused && restarted.Current.Deadline.Value.ToString("o") == originalUntil, "重启必须恢复原截止时间");
            restarted.SetWhitelistHealth(false, "fixture corrupt");
            Check(restarted.Current.Mode == GuardMode.WhitelistError, "名单异常优先于暂停");
            ProtectionTransitionResult stopped = new ProtectionTransitionResult { StopFlagPresent = true, ActualMode = GuardMode.Stopped, Result = OperationResultKind.Success };
            restarted.ApplyProtectionResult(stopped);
            Check(restarted.Current.Mode == GuardMode.Stopped, "彻底停止优先于名单和暂停");
            File.WriteAllText(runtime, "not-json", new UTF8Encoding(false));
            GuardStateCoordinator corrupt = new GuardStateCoordinator(new GuardStateStore(runtime, legacy));
            corrupt.Initialize(false, true, "");
            Check(corrupt.Current.Mode == GuardMode.Protecting && corrupt.Current.Detail.Contains("暂停状态异常"), "损坏状态必须安全回防护并提示");
        });
        RunCase("暂停保存失败不假成功", delegate {
            string directoryTarget = Path.Combine(root, "runtime-as-directory");
            Directory.CreateDirectory(directoryTarget);
            GuardStateCoordinator coordinator = new GuardStateCoordinator(new GuardStateStore(directoryTarget, Path.Combine(root, "none.json")));
            coordinator.Initialize(false, true, "");
            OperationResult result = coordinator.RequestPause(TimeSpan.FromMinutes(30), StateChangeSource.MainPanel);
            Check(result.Kind == OperationResultKind.Failed && coordinator.Current.Mode == GuardMode.Protecting, "写失败不得宣称暂停已持久化");
        });
        RunCase("操作结果取消、冲突与超时映射", delegate {
            Check(MaintenanceService.MapProcessResult("x", 3).Kind == OperationResultKind.Cancelled, "取消必须独立映射");
            Check(MaintenanceService.MapProcessResult("x", 2).Kind == OperationResultKind.Conflict, "冲突必须独立映射");
            Check(MaintenanceService.MapProcessResult("x", -2).Message.Contains("超时"), "超时不得映射成功");
        });
        RunCase("扫描挂载代次不复用旧结果", delegate {
            MountGenerationTracker tracker = new MountGenerationTracker();
            tracker.Observe(new string[] { "E:\\" });
            long first = tracker.GenerationOf("E:");
            tracker.Observe(new string[] { "E:\\" });
            Check(tracker.GenerationOf("E:") == first, "同一挂载代次必须稳定");
            tracker.Observe(new string[0]);
            tracker.Observe(new string[] { "E:\\" });
            Check(tracker.GenerationOf("E:") > first, "重新挂载必须递增代次");
            ScanCoordinator coordinator = new ScanCoordinator(delegate { return new string[] { "defender", "kaspersky" }; });
            coordinator.Dispose();
        });
        RunCase("日志单写线程与连续指纹去噪", delegate {
            string directory = Path.Combine(root, "logs");
            DiagnosticsStore diagnostics = new DiagnosticsStore(directory);
            DiagnosticEvent item = new DiagnosticEvent { Source = "Test", EventType = "STATE", Message = "same", Result = OperationResultKind.Unchanged };
            diagnostics.Write(item); diagnostics.Write(item.Clone());
            DiagnosticEvent recovered = item.Clone(); recovered.EventType = "RECOVERED"; recovered.Message = "different";
            diagnostics.Write(recovered);
            Check(diagnostics.Flush(3000), "日志队列应及时清空");
            string[] logLines = File.ReadAllLines(Path.Combine(directory, "usb-guard.log"));
            Check(logLines.Length == 2 && diagnostics.GetRecentKeyEvents(20).Count == 2, "连续相同事件只能记录一次");
            diagnostics.Dispose();
        });
        RunCase("版本与说明唯一来源", delegate {
            BuildInfo info = BuildInfoProvider.GetBuildInfo();
            Check(info.ProductVersion == "2.1.0" && info.BuildDate == "2026-11-20", "版本和构建日期必须来自 BuildInfoProvider");
            Check(info.Consistency == "交付一致", "候选 EXE、源文件、说明与清单必须完整一致，实际=" + info.Consistency);
        });
        RunCase("从未报警与连续二十次清理安全", delegate {
            AlertPresenter presenter = new AlertPresenter(null, null, null);
            Check(!presenter.HasActiveAlert, "新实例不得虚构活动报警");
            for (int i = 0; i < 20; i++) {
                presenter.Enqueue(new PhysicalDevice[0]);
                presenter.DismissAll();
            }
            presenter.DismissAll();
            Check(!presenter.HasActiveAlert, "重复清理后必须保持无活动报警");
        });

        int total = 12;
        string summary = "SELF-TEST " + (failed == 0 ? "PASS" : "FAIL") + " cases=" + total + " failed=" + failed;
        lines.Add(summary);
        string report = string.Join(Environment.NewLine, lines.ToArray()) + Environment.NewLine;
        Console.WriteLine(report);
        try {
            string reportPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "USBGuard", "self-test-last.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath));
            File.WriteAllText(reportPath, report, new UTF8Encoding(false));
        } catch { }
        try { Directory.Delete(root, true); } catch { }
        return failed == 0 ? 0 : 1;
    }

    private static DeviceNode Node(string id, string container, string parent) {
        return new DeviceNode { InstanceId = id, ContainerId = container, ParentInstanceId = parent, Description = "fixture", Category = DeviceCategory.Other };
    }

    private static void RunCase(string name, Action test) {
        try { test(); lines.Add("[PASS] " + name); }
        catch (Exception ex) { failed++; lines.Add("[FAIL] " + name + " :: " + ex.Message); }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
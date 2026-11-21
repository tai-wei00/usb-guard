using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

internal static class UsbGuard {
    [STAThread]
    private static int Main(string[] args) {
        AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e) { LogError(e.ExceptionObject); };
        Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e) { LogError(e.Exception); };
        string error;
        CommandLineOptions options;
        if (!TryParseCommandLine(args, out options, out error)) return Fail(error, 64);
        string directory = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
        if (options.Operation == "self-test") return SelfTests.Run();
        if (options.Operation == "apply-mutation") {
            MaintenanceService service = new MaintenanceService(directory, new DeviceInventory(), null);
            return service.ApplyInElevatedProcess(options.RequestPath);
        }
        if (options.Operation == "setup-trust") return SetupTrustForm.Run();
        if (options.Operation == "control-protection") return RunProtectionCommand(directory, options.RequestPath);
        if (options.Operation == "sync-allow") return RunSyncAllow();
        if (options.Operation == "test-alert") return RunAlertTest(directory);
        if (options.Operation == "test-prompt") return RunPromptTest(options.TestCategory);
        return RunGuard(directory, options.ShowMain);
    }

    internal static bool TryParseCommandLine(string[] args, out CommandLineOptions options, out string error) {
        options = new CommandLineOptions();
        error = "";
        args = args ?? new string[0];
        if (args.Length == 0) return true;
        string operation = args[0];
        if (operation == "--show-main" && args.Length == 1) { options.ShowMain = true; return true; }
        if (operation == "--apply-mutation" && args.Length == 2 && !string.IsNullOrWhiteSpace(args[1])) {
            options.Operation = "apply-mutation"; options.RequestPath = args[1]; return true;
        }
        if (operation == "--setup-trust" && args.Length == 1) { options.Operation = "setup-trust"; return true; }
        if (operation == "--control-protection" && args.Length == 2 && (args[1] == "stop" || args[1] == "enable")) {
            options.Operation = "control-protection"; options.RequestPath = args[1]; return true;
        }
        if (operation == "--self-test" && args.Length == 1) { options.Operation = "self-test"; return true; }
        if (operation == "--sync-allow" && args.Length == 1) { options.Operation = "sync-allow"; return true; }
        if (operation == "--test-alert" && args.Length == 1) { options.Operation = "test-alert"; return true; }
        if (operation == "--test-prompt" && args.Length <= 2) {
            options.Operation = "test-prompt";
            if (args.Length == 2) {
                string category = (args[1] ?? "").ToLowerInvariant();
                if (category != "storage" && category != "hid" && category != "other") { error = "测试类别无效"; return false; }
                options.TestCategory = category;
            }
            return true;
        }
        if (operation == "--trust-current" || operation == "--trust-ids" || operation == "--remove-entry" || operation == "--save-config") {
            error = operation + " 批量/CSV 常驻维护入口已废弃，请使用显式设备选择或结构化维护请求";
            return false;
        }
        error = "未知操作或参数不完整：" + string.Join(" ", args);
        return false;
    }

    private static int RunGuard(string directory, bool showMain) {
        Application.EnableVisualStyles();
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        GuardRuntime runtime = null;
        SingleInstanceActivator activator = new SingleInstanceActivator(directory);
        bool primary = activator.ActivateOrTakeOwnership(showMain, SynchronizationContext.Current, delegate { if (runtime != null) runtime.ShowMain(); });
        if (!primary) {
            int result = string.IsNullOrEmpty(activator.ActivationError) ? 0 : Fail(activator.ActivationError, 3);
            activator.Dispose();
            return result;
        }
        try {
            runtime = new GuardRuntime(directory, SynchronizationContext.Current);
            runtime.Start(showMain);
            Application.Run();
            return 0;
        } catch (Exception ex) {
            return Fail("启动未完成：" + ex, 1);
        } finally {
            if (runtime != null) runtime.Dispose();
            activator.Dispose();
        }
    }

    private static int RunProtectionCommand(string directory, string action) {
        Application.EnableVisualStyles();
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        ProtectionControlService service = new ProtectionControlService(directory, SynchronizationContext.Current);
        service.ChangeAsync(action == "stop" ? ProtectionAction.Stop : ProtectionAction.Enable, delegate(ProtectionTransitionResult result) {
            int code = result.Result == OperationResultKind.Success ? 0 : result.Result == OperationResultKind.Cancelled ? 3 : result.Result == OperationResultKind.Conflict ? 2 : 1;
            Environment.Exit(code);
        });
        Application.Run();
        return 1;
    }

    private static int RunPromptTest(string category) {
        Application.EnableVisualStyles();
        DeviceCategory parsed = category == "storage" ? DeviceCategory.Storage : category == "hid" ? DeviceCategory.Hid : DeviceCategory.Other;
        PhysicalDevice group = new PhysicalDevice();
        group.GroupKey = "INSTANCE:USB\\VID_TEST&PID_0001\\TESTINSTANCE";
        group.Category = parsed;
        group.Description = "测试设备（无实际操作）";
        group.SnapshotGeneration = 1;
        group.Nodes.Add(new DeviceNode { InstanceId = "USB\\VID_TEST&PID_0001\\TESTINSTANCE", Description = group.Description, Category = parsed });
        AlertPresenter presenter = new AlertPresenter(null, null, null, true, Application.Exit);
        presenter.Enqueue(new PhysicalDevice[] { group });
        Application.Run();
        return 0;
    }

    private static int RunAlertTest(string directory) {
        DiagnosticsStore diagnostics = new DiagnosticsStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "USBGuard"));
        try {
            ConfigLoadResult config = new ConfigStore(Path.Combine(directory, "usb-guard-config.json")).Load();
            bool locked = false;
            if (config.Document.lockOnUnknown) locked = LockWorkStation();
            DiagnosticEvent item = new DiagnosticEvent();
            item.EventType = "TEST_ALERT";
            item.Result = locked || !config.Document.lockOnUnknown ? OperationResultKind.Success : OperationResultKind.Failed;
            item.Message = "模拟陌生设备；lockOnUnknown=" + config.Document.lockOnUnknown + "，LockWorkStation=" + locked;
            diagnostics.Write(item);
            diagnostics.Flush(2000);
            return item.Result == OperationResultKind.Success ? 0 : 1;
        } finally { diagnostics.Dispose(); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool LockWorkStation();

    private static int RunSyncAllow() {
        try {
            string script = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "安装.ps1");
            if (!File.Exists(script)) return Fail("系统策略同步失败：安装脚本不存在", 1);
            ProcessStartInfo start = new ProcessStartInfo();
            start.FileName = Path.Combine(Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows", @"System32\WindowsPowerShell\v1.0\powershell.exe");
            start.Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\" -EnforcePolicy";
            start.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.WindowStyle = ProcessWindowStyle.Hidden;
            using (Process process = Process.Start(start)) {
                if (process == null) return Fail("系统策略同步失败：进程未创建", 1);
                process.WaitForExit();
                if (process.ExitCode != 0) return Fail("系统策略同步失败，exit=" + process.ExitCode, 1);
            }
            return 0;
        } catch (Exception ex) { return Fail("系统策略同步失败：" + ex.Message, 1); }
    }

    private static int Fail(string message, int code) { LogError(message); return code; }

    private static void LogError(object value) {
        try {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "USBGuard");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "guard-error.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + Convert.ToString(value) + Environment.NewLine, Encoding.UTF8);
        } catch { }
    }
}
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

internal sealed class ProtectionControlService {
    private readonly string baseDirectory;
    private readonly SynchronizationContext uiContext;
    private readonly JavaScriptSerializer serializer = new JavaScriptSerializer();
    private readonly string maintenanceDirectory;

    internal ProtectionControlService(string programDirectory, SynchronizationContext context) {
        baseDirectory = programDirectory;
        uiContext = context;
        maintenanceDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "USBGuard", "maintenance");
    }

    internal bool StopFlagPresent { get { return File.Exists(Path.Combine(baseDirectory, "usb-guard-disabled.flag")); } }

    internal ProtectionTransitionResult ReadActualState() {
        string resultPath = Path.Combine(maintenanceDirectory, "control-get-" + Guid.NewGuid().ToString("N") + ".json");
        ProtectionTransitionResult result = RunControl("Get", resultPath, false, 8000);
        result.Action = ProtectionAction.Enable;
        result.StopFlagPresent = StopFlagPresent;
        if (result.StopFlagPresent) result.ActualMode = GuardMode.Stopped;
        return result;
    }

    internal void ChangeAsync(ProtectionAction action, Action<ProtectionTransitionResult> completed) {
        ThreadPool.QueueUserWorkItem(delegate {
            string resultPath = Path.Combine(maintenanceDirectory, "control-" + Guid.NewGuid().ToString("N") + ".json");
            ProtectionTransitionResult result = RunControl(action == ProtectionAction.Stop ? "Stopped" : "Enabled", resultPath, true, 60000);
            result.Action = action;
            result.StopFlagPresent = StopFlagPresent;
            if (result.StopFlagPresent) result.ActualMode = GuardMode.Stopped;
            else if (action == ProtectionAction.Enable && result.Result == OperationResultKind.Success) result.ActualMode = GuardMode.Protecting;
            if (uiContext != null) uiContext.Post(delegate { completed(result); }, null);
            else completed(result);
        });
    }

    private ProtectionTransitionResult RunControl(string target, string resultPath, bool elevated, int timeoutMs) {
        ProtectionTransitionResult fallback = new ProtectionTransitionResult();
        fallback.Result = OperationResultKind.Failed;
        Process process = null;
        try {
            Directory.CreateDirectory(maintenanceDirectory);
            string script = Path.Combine(baseDirectory, "GuardControl.ps1");
            if (!File.Exists(script)) { fallback.Message = "GuardControl.ps1 缺失，请运行安装修复"; return fallback; }
            ProcessStartInfo start = new ProcessStartInfo();
            start.FileName = Path.Combine(Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows", @"System32\WindowsPowerShell\v1.0\powershell.exe");
            start.Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\" -Target " + target + " -ResultPath \"" + resultPath + "\"";
            start.WorkingDirectory = baseDirectory;
            start.CreateNoWindow = true;
            start.WindowStyle = ProcessWindowStyle.Hidden;
            start.UseShellExecute = elevated;
            if (elevated) start.Verb = "runas";
            process = Process.Start(start);
            if (process == null) { fallback.Message = "保护控制进程未创建"; return fallback; }
            if (!process.WaitForExit(timeoutMs)) {
                try { process.Kill(); } catch { }
                fallback.Message = "保护控制超时";
                return fallback;
            }
            ProtectionTransitionResult parsed = ReadResult(resultPath);
            if (parsed != null) return parsed;
            fallback.Message = "保护控制未返回结构化结果，exit=" + process.ExitCode;
            return fallback;
        } catch (Win32Exception ex) {
            fallback.Result = ex.NativeErrorCode == 1223 ? OperationResultKind.Cancelled : OperationResultKind.Failed;
            fallback.Message = ex.NativeErrorCode == 1223 ? "已取消授权，状态未变更" : ex.Message;
            return fallback;
        } catch (Exception ex) {
            fallback.Message = ex.Message;
            return fallback;
        } finally {
            if (process != null) process.Dispose();
            try { if (File.Exists(resultPath)) File.Delete(resultPath); } catch { }
        }
    }

    private ProtectionTransitionResult ReadResult(string resultPath) {
        try {
            if (!File.Exists(resultPath)) return null;
            return serializer.Deserialize<ProtectionTransitionResult>(File.ReadAllText(resultPath, Encoding.UTF8));
        } catch { return null; }
    }
}
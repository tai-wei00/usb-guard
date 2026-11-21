using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

internal sealed class MaintenanceService {
    private readonly string baseDirectory;
    private readonly DeviceInventory inventory;
    private readonly SynchronizationContext uiContext;
    private readonly JavaScriptSerializer serializer = new JavaScriptSerializer();
    private readonly string maintenanceDirectory;

    internal MaintenanceService(string programDirectory, DeviceInventory deviceInventory, SynchronizationContext context) {
        baseDirectory = programDirectory;
        inventory = deviceInventory;
        uiContext = context;
        maintenanceDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "USBGuard", "maintenance");
    }

    internal void ApplyAsync(MutationRequest request, Action<MutationResult> completed) {
        if (request == null || completed == null) return;
        ThreadPool.QueueUserWorkItem(delegate {
            MutationResult direct = ApplyCurrentProcess(request);
            if (direct.Kind == OperationResultKind.Failed && direct.RequiresElevation) StartElevated(request, completed);
            else CompleteOnUi(direct, completed);
        });
    }

    internal MutationResult ApplyCurrentProcess(MutationRequest request) {
        MutationResult result = new MutationResult();
        result.RequestId = request == null ? "" : request.RequestId;
        try {
            if (request == null) { result.Kind = OperationResultKind.Failed; result.Message = "维护请求为空"; return result; }
            if (request.Operation == MutationKind.Config) return ApplyConfig(request);
            WhitelistStore store = new WhitelistStore(Path.Combine(baseDirectory, "usb-whitelist.json"));
            WhitelistLoadResult loaded = store.Load();
            if (!loaded.Healthy) { result.Kind = OperationResultKind.Failed; result.Message = loaded.Message; return result; }
            if (loaded.Document.Revision != request.ExpectedRevision) {
                result.Kind = OperationResultKind.Conflict;
                result.Message = "名单已变化，请刷新后重试";
                return result;
            }
            WhitelistDocument candidate;
            if (request.Operation == MutationKind.Trust) {
                if (request.Trust == null || request.Trust.ExpectedInstanceIds == null || request.Trust.ExpectedInstanceIds.Count == 0) {
                    result.Kind = OperationResultKind.Failed; result.Message = "信任目标为空"; return result;
                }
                bool allTrusted = true;
                foreach (string id in request.Trust.ExpectedInstanceIds) if (!loaded.Document.IsTrusted(id)) { allTrusted = false; break; }
                if (allTrusted) { result.Kind = OperationResultKind.Unchanged; result.Message = "目标已在名单中"; return result; }
                DeviceSnapshot snapshot = inventory.CaptureSnapshot();
                PhysicalDevice target = inventory.ResolveTarget(request.Trust, snapshot);
                if (target == null) {
                    result.Kind = OperationResultKind.Conflict;
                    result.Message = "目标设备已拔出、快照代次或接口范围已变化，名单未变更";
                    return result;
                }
                candidate = store.CreateTrustedClone(loaded.Document, request.Trust, DateTimeOffset.Now);
            } else if (request.Operation == MutationKind.Remove) {
                if (WhitelistStore.FindAny(loaded.Document, request.EntryInstanceId) == null) {
                    result.Kind = OperationResultKind.Unchanged; result.Message = "条目不存在，名单未变化"; return result;
                }
                candidate = store.CreateRemovedClone(loaded.Document, request.EntryInstanceId);
            } else {
                result.Kind = OperationResultKind.Failed; result.Message = "未知名单操作"; return result;
            }
            AtomicWriteResult saved = store.Save(candidate, request.ExpectedRevision);
            result.Kind = saved.Kind;
            result.Message = saved.Message;
            result.NewRevision = saved.Revision;
            result.RequiresElevation = saved.RequiresElevation;
            return result;
        } catch (Exception ex) {
            result.Kind = OperationResultKind.Failed;
            result.Message = ex.Message;
            return result;
        }
    }

    private MutationResult ApplyConfig(MutationRequest request) {
        MutationResult result = new MutationResult();
        result.RequestId = request.RequestId;
        if (request.Config == null) { result.Kind = OperationResultKind.Failed; result.Message = "配置载荷为空"; return result; }
        ConfigStore store = new ConfigStore(Path.Combine(baseDirectory, "usb-guard-config.json"));
        ConfigLoadResult loaded = store.Load();
        if (loaded.Document.Revision != request.ExpectedRevision) {
            result.Kind = OperationResultKind.Conflict;
            result.Message = "配置已变化，请刷新后重试";
            return result;
        }
        AtomicWriteResult saved = store.Save(request.Config.Clone(), request.ExpectedRevision);
        result.Kind = saved.Kind;
        result.Message = saved.Message;
        result.NewRevision = saved.Revision;
        result.RequiresElevation = saved.RequiresElevation;
        return result;
    }

    private void StartElevated(MutationRequest request, Action<MutationResult> completed) {
        string requestPath = Path.Combine(maintenanceDirectory, request.RequestId + ".request.json");
        string resultPath = Path.Combine(maintenanceDirectory, request.RequestId + ".result.json");
        try {
            Directory.CreateDirectory(maintenanceDirectory);
            string json = serializer.Serialize(request);
            AtomicWriteResult written = AtomicJsonStore.WriteText(requestPath, json, AtomicJsonStore.RevisionOf(""), false);
            if (written.Kind != OperationResultKind.Success && written.Kind != OperationResultKind.Unchanged) {
                MutationResult failure = new MutationResult { RequestId = request.RequestId, Kind = OperationResultKind.Failed, Message = "维护请求写入失败：" + written.Message };
                CompleteOnUi(failure, completed);
                return;
            }
            ProcessStartInfo start = new ProcessStartInfo();
            start.FileName = Assembly.GetExecutingAssembly().Location;
            start.Arguments = "--apply-mutation \"" + requestPath + "\"";
            start.WorkingDirectory = baseDirectory;
            start.UseShellExecute = true;
            start.Verb = "runas";
            start.WindowStyle = ProcessWindowStyle.Hidden;
            Process process = Process.Start(start);
            if (process == null) throw new InvalidOperationException("提权进程未创建");
            int finished = 0;
            System.Threading.Timer timeout = null;
            Action<int> finish = delegate(int exitCode) {
                if (Interlocked.CompareExchange(ref finished, 1, 0) != 0) return;
                if (timeout != null) timeout.Dispose();
                MutationResult result = ReadResult(resultPath, request.RequestId);
                if (result == null) result = MapProcessResult(request.RequestId, exitCode);
                CompleteOnUi(result, completed);
                try { process.Dispose(); } catch { }
            };
            timeout = new System.Threading.Timer(delegate {
                try { if (!process.HasExited) process.Kill(); } catch { }
                finish(-2);
            }, null, 60000, System.Threading.Timeout.Infinite);
            process.EnableRaisingEvents = true;
            process.Exited += delegate { finish(process.ExitCode); };
            if (process.HasExited) finish(process.ExitCode);
        } catch (Win32Exception ex) {
            MutationResult result = new MutationResult();
            result.RequestId = request.RequestId;
            result.Kind = ex.NativeErrorCode == 1223 ? OperationResultKind.Cancelled : OperationResultKind.Failed;
            result.Message = ex.NativeErrorCode == 1223 ? "已取消授权" : ex.Message;
            CompleteOnUi(result, completed);
        } catch (Exception ex) {
            CompleteOnUi(new MutationResult { RequestId = request.RequestId, Kind = OperationResultKind.Failed, Message = ex.Message }, completed);
        }
    }

    private MutationResult ReadResult(string resultPath, string requestId) {
        try {
            if (!File.Exists(resultPath)) return null;
            MutationResult result = serializer.Deserialize<MutationResult>(File.ReadAllText(resultPath, Encoding.UTF8));
            if (result == null || result.RequestId != requestId) return null;
            return result;
        } catch { return null; }
    }

    internal static MutationResult MapProcessResult(string requestId, int exitCode) {
        MutationResult result = new MutationResult();
        result.RequestId = requestId;
        if (exitCode == 0) { result.Kind = OperationResultKind.Success; result.Message = "维护成功"; }
        else if (exitCode == 2) { result.Kind = OperationResultKind.Conflict; result.Message = "名单或配置已变化，请刷新后重试"; }
        else if (exitCode == 3) { result.Kind = OperationResultKind.Cancelled; result.Message = "已取消授权"; }
        else if (exitCode == -2) { result.Kind = OperationResultKind.Failed; result.Message = "提权维护超时"; }
        else { result.Kind = OperationResultKind.Failed; result.Message = "提权维护失败，exit=" + exitCode; }
        return result;
    }

    internal int ApplyInElevatedProcess(string requestPath) {
        MutationResult result = new MutationResult();
        string resultPath = null;
        try {
            string fullPath = ValidateRequestPath(requestPath);
            resultPath = Path.Combine(maintenanceDirectory, Path.GetFileNameWithoutExtension(fullPath).Replace(".request", "") + ".result.json");
            MutationRequest request = serializer.Deserialize<MutationRequest>(File.ReadAllText(fullPath, Encoding.UTF8));
            if (request == null || string.IsNullOrEmpty(request.RequestId)) throw new InvalidDataException("维护请求无效");
            string expectedName = request.RequestId + ".request.json";
            if (!string.Equals(Path.GetFileName(fullPath), expectedName, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("请求 ID 与文件名不一致");
            result = ApplyCurrentProcess(request);
        } catch (Exception ex) {
            result.Kind = OperationResultKind.Failed;
            result.Message = ex.Message;
        }
        if (resultPath != null) {
            try { AtomicJsonStore.WriteText(resultPath, serializer.Serialize(result), AtomicJsonStore.RevisionOf(""), false); } catch { }
        }
        if (result.Kind == OperationResultKind.Success || result.Kind == OperationResultKind.Unchanged) return 0;
        if (result.Kind == OperationResultKind.Conflict) return 2;
        if (result.Kind == OperationResultKind.Cancelled) return 3;
        return 1;
    }

    private string ValidateRequestPath(string requestPath) {
        string full = Path.GetFullPath(requestPath ?? "");
        string root = Path.GetFullPath(maintenanceDirectory).TrimEnd('\\') + "\\";
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !full.EndsWith(".request.json", StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("维护请求越出固定目录或扩展名无效");
        if (!File.Exists(full)) throw new FileNotFoundException("维护请求不存在", full);
        return full;
    }

    private void CompleteOnUi(MutationResult result, Action<MutationResult> completed) {
        if (uiContext != null) uiContext.Post(delegate { completed(result); }, null);
        else completed(result);
    }
}
using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

internal sealed class GuardStateStore {
    private readonly string path;
    private readonly string legacyPath;
    private readonly JavaScriptSerializer serializer = new JavaScriptSerializer();

    internal GuardStateStore(string baseDirectory) {
        string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "USBGuard");
        path = Path.Combine(local, "guard-runtime.json");
        legacyPath = Path.Combine(baseDirectory, "usb-guard-suspend.json");
    }

    internal GuardStateStore(string runtimePath, string oldPath) {
        path = runtimePath;
        legacyPath = oldPath;
    }

    internal string PathForDiagnostics { get { return path; } }

    internal RuntimeIntentLoadResult Load() {
        RuntimeIntentLoadResult result = new RuntimeIntentLoadResult();
        if (!File.Exists(path)) {
            if (!File.Exists(legacyPath)) { result.Status = RuntimeIntentLoadStatus.Missing; return result; }
            RuntimeIntentLoadResult legacy = ReadIntent(legacyPath);
            if (legacy.Status != RuntimeIntentLoadStatus.Loaded) return legacy;
            AtomicWriteResult migrated = Save(legacy.Intent);
            if (migrated.Kind == OperationResultKind.Success || migrated.Kind == OperationResultKind.Unchanged) {
                try { File.Delete(legacyPath); } catch (Exception ex) { legacy.Message = "已导入旧暂停状态，旧文件清理失败：" + ex.Message; }
            } else {
                legacy.Status = RuntimeIntentLoadStatus.IoError;
                legacy.Message = "旧暂停状态迁移失败：" + migrated.Message;
            }
            return legacy;
        }
        return ReadIntent(path);
    }

    private RuntimeIntentLoadResult ReadIntent(string sourcePath) {
        RuntimeIntentLoadResult result = new RuntimeIntentLoadResult();
        try {
            string json = File.ReadAllText(sourcePath, Encoding.UTF8);
            RuntimeIntent intent = serializer.Deserialize<RuntimeIntent>(json);
            DateTimeOffset until;
            if (intent == null || (intent.kind != "pause" && intent.kind != "repair")
                || !DateTimeOffset.TryParse(intent.until, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out until)) {
                result.Status = RuntimeIntentLoadStatus.Corrupt;
                result.Message = "暂停状态异常，已恢复防护";
                PreserveCorrupt(sourcePath);
                return result;
            }
            if (until <= DateTimeOffset.Now) {
                result.Status = RuntimeIntentLoadStatus.Expired;
                result.Message = "暂停或维修已到期，恢复防护";
                if (string.Equals(sourcePath, path, StringComparison.OrdinalIgnoreCase)) Clear("");
                return result;
            }
            intent.until = until.ToString("o");
            result.Status = RuntimeIntentLoadStatus.Loaded;
            result.Intent = intent;
            return result;
        } catch (UnauthorizedAccessException ex) {
            result.Status = RuntimeIntentLoadStatus.IoError;
            result.Message = "运行状态不可读：" + ex.Message;
            return result;
        } catch (Exception ex) {
            result.Status = RuntimeIntentLoadStatus.Corrupt;
            result.Message = "暂停状态异常，已恢复防护：" + ex.Message;
            PreserveCorrupt(sourcePath);
            return result;
        }
    }

    private void PreserveCorrupt(string sourcePath) {
        try {
            if (File.Exists(sourcePath)) File.Copy(sourcePath, sourcePath + ".corrupt-" + Guid.NewGuid().ToString("N") + ".bak", false);
        } catch { }
    }

    internal AtomicWriteResult Save(RuntimeIntent intent) {
        if (intent == null) return new AtomicWriteResult { Kind = OperationResultKind.Failed, Message = "运行状态为空" };
        DateTimeOffset until;
        if (!DateTimeOffset.TryParse(intent.until, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out until) || until <= DateTimeOffset.Now)
            return new AtomicWriteResult { Kind = OperationResultKind.Failed, Message = "截止时间无效" };
        intent.kind = intent.Kind() == RuntimeIntentKind.Repairing ? "repair" : "pause";
        intent.until = until.ToString("o");
        if (string.IsNullOrEmpty(intent.revision)) intent.revision = Guid.NewGuid().ToString("N");
        string current = File.Exists(path) ? SafeRead(path) : "";
        string json = serializer.Serialize(intent);
        return AtomicJsonStore.WriteText(path, json, AtomicJsonStore.RevisionOf(current), true);
    }

    internal AtomicWriteResult Clear(string expectedRevision) {
        if (!File.Exists(path)) return new AtomicWriteResult { Kind = OperationResultKind.Unchanged, Message = "无运行状态需要清理" };
        string current = SafeRead(path);
        string actual = AtomicJsonStore.RevisionOf(current);
        if (!string.IsNullOrEmpty(expectedRevision) && actual != expectedRevision)
            return new AtomicWriteResult { Kind = OperationResultKind.Conflict, Message = "运行状态已变化，请重试" };
        try {
            string backup = path + ".last-valid.bak";
            File.Copy(path, backup, true);
            File.Delete(path);
            return new AtomicWriteResult { Kind = OperationResultKind.Success, Revision = "" };
        } catch (Exception ex) {
            return new AtomicWriteResult { Kind = OperationResultKind.Failed, Message = ex.Message };
        }
    }

    internal void WriteCurrentState(GuardStateSnapshot state) {
        try {
            GuardStateStatusDocument document = new GuardStateStatusDocument();
            document.mode = state.Mode.ToString();
            document.displayName = state.DisplayName();
            document.until = state.Deadline.HasValue ? state.Deadline.Value.ToString("o") : "";
            document.source = state.Source.ToString();
            document.result = state.Result.ToString();
            document.detail = state.Detail ?? "";
            document.revision = state.Revision;
            string json = serializer.Serialize(document);
            string statusPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path), "guard-state.json");
            string current = File.Exists(statusPath) ? SafeRead(statusPath) : "";
            AtomicJsonStore.WriteText(statusPath, json, AtomicJsonStore.RevisionOf(current), false);
        } catch { }
    }

    private static string SafeRead(string value) {
        try { return File.ReadAllText(value, Encoding.UTF8); } catch { return ""; }
    }
}
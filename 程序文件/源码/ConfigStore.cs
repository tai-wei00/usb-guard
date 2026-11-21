using System;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

internal sealed class ConfigLoadResult {
    public ConfigDocument Document = new ConfigDocument();
    public bool Healthy = true;
    public string Message = "";
}

internal sealed class ConfigStore {
    private readonly string path;
    private readonly JavaScriptSerializer serializer = new JavaScriptSerializer();
    internal ConfigStore(string configPath) { path = configPath; }

    internal ConfigLoadResult Load() {
        ConfigLoadResult result = new ConfigLoadResult();
        string text = "";
        try {
            if (File.Exists(path)) text = File.ReadAllText(path, Encoding.UTF8);
            result.Document.Revision = AtomicJsonStore.RevisionOf(text);
            if (string.IsNullOrWhiteSpace(text)) return result;
            ConfigDocument loaded = serializer.Deserialize<ConfigDocument>(text);
            if (loaded == null) throw new InvalidDataException("配置根节点无效");
            loaded.Revision = result.Document.Revision;
            loaded.scanEngineOrder = NormalizeOrder(loaded.scanEngineOrder);
            result.Document = loaded;
            return result;
        } catch (Exception ex) {
            try { if (File.Exists(path)) File.Copy(path, path + ".corrupt-" + Guid.NewGuid().ToString("N") + ".bak", false); } catch { }
            result.Document = new ConfigDocument();
            result.Document.Revision = AtomicJsonStore.RevisionOf(text);
            result.Healthy = false;
            result.Message = "配置异常，已使用安全默认值但未改写原文件：" + ex.Message;
            return result;
        }
    }

    internal AtomicWriteResult Save(ConfigDocument candidate, string expectedRevision) {
        if (candidate == null) return new AtomicWriteResult { Kind = OperationResultKind.Failed, Message = "配置为空" };
        candidate.scanEngineOrder = NormalizeOrder(candidate.scanEngineOrder);
        string json = serializer.Serialize(candidate);
        AtomicWriteResult saved = AtomicJsonStore.WriteText(path, json, expectedRevision, true);
        if (saved.Kind == OperationResultKind.Success) {
            ConfigLoadResult verify = Load();
            if (!verify.Healthy || verify.Document.Revision != saved.Revision) {
                saved.Kind = OperationResultKind.Failed;
                saved.Message = "配置保存后重读失败：" + verify.Message;
            } else {
                candidate.lockOnUnknown = verify.Document.lockOnUnknown;
                candidate.persistentLock = verify.Document.persistentLock;
                candidate.autoScanRemovable = verify.Document.autoScanRemovable;
                candidate.scanEngineOrder = verify.Document.scanEngineOrder;
                candidate.Revision = verify.Document.Revision;
            }
        }
        return saved;
    }

    private static string[] NormalizeOrder(string[] order) {
        if (order != null && order.Length == 2) {
            string a = (order[0] ?? "").Trim().ToLowerInvariant();
            string b = (order[1] ?? "").Trim().ToLowerInvariant();
            if ((a == "kaspersky" && b == "defender") || (a == "defender" && b == "kaspersky")) return new string[] { a, b };
        }
        return new string[] { "kaspersky", "defender" };
    }
}
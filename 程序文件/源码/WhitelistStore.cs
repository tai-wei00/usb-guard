using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

internal sealed class WhitelistStore {
    private readonly string path;
    private readonly JavaScriptSerializer serializer = new JavaScriptSerializer();
    internal string PathForDiagnostics { get { return path; } }

    internal WhitelistStore(string whitelistPath) {
        path = whitelistPath;
        serializer.MaxJsonLength = 10 * 1024 * 1024;
    }

    internal WhitelistLoadResult Load() {
        WhitelistLoadResult result = new WhitelistLoadResult();
        string text = "";
        try {
            if (File.Exists(path)) text = File.ReadAllText(path, Encoding.UTF8);
        } catch (Exception ex) {
            result.Status = WhitelistLoadStatus.IoError;
            result.Message = "名单读取失败：" + ex.Message;
            return result;
        }
        result.Document.RawText = text;
        result.Document.Revision = AtomicJsonStore.RevisionOf(text);
        if (string.IsNullOrWhiteSpace(text)) { result.Status = WhitelistLoadStatus.Empty; return result; }
        object[] items;
        try {
            items = serializer.Deserialize<object[]>(text);
            if (items == null) throw new InvalidDataException("名单根节点不是数组");
            foreach (object item in items) result.Document.Entries.Add(ReadEntry(item));
        } catch (Exception ex) {
            result.Status = WhitelistLoadStatus.Corrupt;
            result.Message = "名单格式异常，原文件已保留：" + ex.Message;
            result.Document = new WhitelistDocument { RawText = text, Revision = AtomicJsonStore.RevisionOf(text) };
            return result;
        }
        result.Status = result.Document.Entries.Count == 0 ? WhitelistLoadStatus.Empty : WhitelistLoadStatus.Loaded;
        result.Message = result.Status == WhitelistLoadStatus.Empty ? "尚无信任设备" : "名单已加载";
        return result;
    }

    private WhitelistEntry ReadEntry(object item) {
        WhitelistEntry entry = new WhitelistEntry();
        string raw = item as string;
        if (raw != null) {
            entry.deviceIds.Add(raw.Trim());
            entry.OriginalPayload = serializer.Serialize(raw);
            entry.Kind = DeviceIdentity.IsValidInstanceId(raw) ? WhitelistEntryKind.TrustedDevice : WhitelistEntryKind.Unrecognized;
            entry.category = entry.Kind == WhitelistEntryKind.TrustedDevice ? WhitelistEntry.CategoryToString(DeviceInventory.Classify(raw, "", false)) : "unrecognized";
            return entry;
        }
        Dictionary<string, object> dictionary = item as Dictionary<string, object>;
        if (dictionary == null) {
            entry.Kind = WhitelistEntryKind.Unrecognized;
            entry.category = "unrecognized";
            entry.description = "未识别条目";
            entry.OriginalPayload = serializer.Serialize(item);
            return entry;
        }
        entry.OriginalPayload = serializer.Serialize(dictionary);
        object value;
        if (dictionary.TryGetValue("deviceIds", out value)) {
            object[] ids = value as object[];
            if (ids != null) {
                foreach (object id in ids) {
                    string text = Convert.ToString(id);
                    if (!string.IsNullOrWhiteSpace(text)) entry.deviceIds.Add(text.Trim());
                }
            }
        }
        entry.description = dictionary.TryGetValue("description", out value) && !string.IsNullOrEmpty(Convert.ToString(value)) ? Convert.ToString(value) : "未知设备";
        entry.category = dictionary.TryGetValue("category", out value) ? Convert.ToString(value) : "other";
        entry.trustedAt = dictionary.TryGetValue("trustedAt", out value) && !string.IsNullOrEmpty(Convert.ToString(value)) ? Convert.ToString(value) : null;
        bool hasExact = false;
        foreach (string id in entry.deviceIds) if (DeviceIdentity.IsValidInstanceId(id)) { hasExact = true; break; }
        if (!hasExact) {
            entry.Kind = WhitelistEntryKind.Unrecognized;
            entry.category = "unrecognized";
            if (entry.deviceIds.Count == 0 && dictionary.TryGetValue("deviceId", out value)) {
                string legacy = Convert.ToString(value);
                if (!string.IsNullOrWhiteSpace(legacy)) entry.deviceIds.Add(legacy.Trim());
            }
        }
        return entry;
    }

    internal WhitelistDocument CreateTrustedClone(WhitelistDocument current, TrustRequest request, DateTimeOffset trustedAt) {
        if (current == null || request == null || request.ExpectedInstanceIds == null || request.ExpectedInstanceIds.Count == 0)
            throw new ArgumentException("信任目标或当前名单为空");
        WhitelistDocument clone = current.Clone();
        WhitelistEntry target = null;
        List<string> accepted = new List<string>();
        foreach (string raw in request.ExpectedInstanceIds) {
            string id = DeviceIdentity.Normalize(raw);
            if (!DeviceIdentity.IsValidInstanceId(id)) throw new InvalidDataException("信任请求包含非完整实例 ID");
            bool duplicate = false;
            foreach (string existing in accepted) if (DeviceIdentity.Equals(existing, id)) { duplicate = true; break; }
            if (!duplicate) accepted.Add(id);
            if (target == null) target = FindAny(clone, id);
        }
        if (target != null && target.Kind != WhitelistEntryKind.TrustedDevice) throw new InvalidDataException("历史未识别条目不能自动转为信任设备");
        if (target == null) {
            target = new WhitelistEntry();
            target.description = string.IsNullOrEmpty(request.Description) ? "未知设备" : request.Description;
            target.category = request.Category ?? "other";
            target.trustedAt = trustedAt.ToString("yyyy-MM-ddTHH:mm:sszzz");
            clone.Entries.Add(target);
        }
        foreach (string id in accepted) if (!ContainsId(target.deviceIds, id)) target.deviceIds.Add(id);
        ValidateUnique(clone);
        return clone;
    }

    internal WhitelistDocument CreateRemovedClone(WhitelistDocument current, string instanceId) {
        WhitelistDocument clone = current.Clone();
        WhitelistEntry target = FindAny(clone, instanceId);
        if (target == null) return clone;
        clone.Entries.Remove(target);
        return clone;
    }

    internal static WhitelistEntry FindAny(WhitelistDocument document, string instanceId) {
        string id = DeviceIdentity.Normalize(instanceId);
        if (id.Length == 0) return null;
        foreach (WhitelistEntry entry in document.Entries) {
            foreach (string candidate in entry.deviceIds) if (DeviceIdentity.Equals(candidate, id)) return entry;
        }
        return null;
    }

    private static bool ContainsId(List<string> ids, string id) {
        foreach (string existing in ids) if (DeviceIdentity.Equals(existing, id)) return true;
        return false;
    }

    internal AtomicWriteResult Save(WhitelistDocument candidate, string expectedRevision) {
        string json;
        try {
            ValidateUnique(candidate);
            json = SerializeDocument(candidate);
        } catch (Exception ex) {
            return new AtomicWriteResult { Kind = OperationResultKind.Failed, Message = ex.Message };
        }
        AtomicWriteResult saved = AtomicJsonStore.WriteText(path, json, expectedRevision, true);
        if (saved.Kind == OperationResultKind.Success) {
            WhitelistLoadResult verify = Load();
            if (!verify.Healthy || verify.Document.Revision != saved.Revision) {
                saved.Kind = OperationResultKind.Failed;
                saved.Message = "保存后名单重读失败：" + verify.Message;
                return saved;
            }
            candidate.Entries = verify.Document.Entries;
            candidate.RawText = verify.Document.RawText;
            candidate.Revision = verify.Document.Revision;
        }
        return saved;
    }

    internal string SerializeDocument(WhitelistDocument document) {
        List<object> output = new List<object>();
        foreach (WhitelistEntry entry in document.Entries) {
            if (entry.Kind == WhitelistEntryKind.Unrecognized && !string.IsNullOrEmpty(entry.OriginalPayload)) {
                output.Add(serializer.Deserialize<object>(entry.OriginalPayload));
                continue;
            }
            Dictionary<string, object> item = new Dictionary<string, object>();
            item["deviceIds"] = entry.deviceIds.ToArray();
            item["description"] = entry.description ?? "未知设备";
            item["category"] = entry.Kind == WhitelistEntryKind.Unrecognized ? "unrecognized" : (entry.category ?? "other");
            item["trustedAt"] = entry.trustedAt;
            if (entry.Kind == WhitelistEntryKind.Unrecognized) item["originalPayload"] = entry.OriginalPayload ?? "";
            output.Add(item);
        }
        return serializer.Serialize(output);
    }

    private static void ValidateUnique(WhitelistDocument document) {
        Dictionary<string, WhitelistEntry> seen = new Dictionary<string, WhitelistEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (WhitelistEntry entry in document.Entries) {
            if (entry.Kind != WhitelistEntryKind.TrustedDevice) continue;
            foreach (string raw in entry.deviceIds) {
                string id = DeviceIdentity.Normalize(raw);
                if (!DeviceIdentity.IsValidInstanceId(id)) continue;
                if (seen.ContainsKey(id)) throw new InvalidDataException("同一完整实例身份出现在多个条目中：" + id);
                seen.Add(id, entry);
            }
        }
    }
}
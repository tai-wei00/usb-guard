using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

static partial class UsbGuard {
    internal sealed class WhiteEntry {
        public List<string> deviceIds = new List<string>();
        public string description;
        public string category;
        public string trustedAt;
        public bool Matches(string id) {
            if (id == null) return false;
            foreach (string d in deviceIds) {
                if (string.Equals(d, id, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }

    static List<WhiteEntry> whiteEntries = new List<WhiteEntry>();
    static Dictionary<string, WhiteEntry> whiteIndex = new Dictionary<string, WhiteEntry>(StringComparer.OrdinalIgnoreCase);
    static bool whitelistHealthy = true;
    static List<string> scanEngineOrder = new List<string> { "kaspersky", "defender" };

    static string EscapeLogField(string s) {
        if (s == null) return "";
        return s.Replace("\r", " ").Replace("\n", " ").Replace("|", "/").Replace(";", ",");
    }

    static bool AtomicWriteUtf8(string path, string content) {
        string tmp = path + ".tmp";
        try {
            File.WriteAllText(tmp, content, new UTF8Encoding(false));
            string back = File.ReadAllText(tmp, Encoding.UTF8);
            if (back != content) { try { File.Delete(tmp); } catch { } return false; }
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
            return true;
        } catch {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            return false;
        }
    }

    static void RebuildWhiteIndex() {
        whiteIndex = new Dictionary<string, WhiteEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (WhiteEntry e in whiteEntries) {
            foreach (string id in e.deviceIds) {
                if (!string.IsNullOrEmpty(id)) whiteIndex[id.Trim().ToUpperInvariant()] = e;
            }
        }
        white = new HashSet<string>(whiteIndex.Keys, StringComparer.OrdinalIgnoreCase);
    }

    static string StripMiVariant(string id) {
        if (string.IsNullOrEmpty(id)) return null;
        if (!id.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase)) return null;
        string[] parts = id.Split('\\');
        if (parts.Length < 3) return null;
        string hw = parts[1];
        int mi = hw.IndexOf("&MI_", StringComparison.OrdinalIgnoreCase);
        if (mi <= 0) return null;
        return parts[0] + "\\" + hw.Substring(0, mi) + "\\" + parts[2];
    }

    static bool IsTrustedId(string id) {
        if (string.IsNullOrEmpty(id)) return true;
        string key = id.Trim().ToUpperInvariant();
        if (whiteIndex.ContainsKey(key)) return true;
        string stripped = StripMiVariant(key);
        if (stripped != null && whiteIndex.ContainsKey(stripped)) return true;
        return false;
    }

    internal static List<WhiteEntry> SnapshotEntries() {
        List<WhiteEntry> copy = new List<WhiteEntry>();
        foreach (WhiteEntry e in whiteEntries) {
            WhiteEntry c = new WhiteEntry();
            c.deviceIds = new List<string>(e.deviceIds);
            c.description = e.description;
            c.category = e.category;
            c.trustedAt = e.trustedAt;
            copy.Add(c);
        }
        copy.Sort(delegate (WhiteEntry a, WhiteEntry b) {
            string ka = a.deviceIds.Count > 0 ? a.deviceIds[0] : "";
            string kb = b.deviceIds.Count > 0 ? b.deviceIds[0] : "";
            return string.Compare(ka, kb, StringComparison.OrdinalIgnoreCase);
        });
        return copy;
    }

    static bool LoadWhiteV2() {
        whiteEntries = new List<WhiteEntry>();
        if (!File.Exists(whitelistPath)) { RebuildWhiteIndex(); return true; }
        string text;
        try { text = File.ReadAllText(whitelistPath, Encoding.UTF8); }
        catch { return false; }
        if (string.IsNullOrWhiteSpace(text)) { RebuildWhiteIndex(); return true; }
        object[] arr;
        try {
            JavaScriptSerializer ser = new JavaScriptSerializer();
            ser.MaxJsonLength = 10 * 1024 * 1024;
            arr = ser.Deserialize<object[]>(text);
        } catch { return false; }
        if (arr == null) return false;
        // 空数组 [] 是"新格式空名单"，绝不能当旧格式走去自动信任当前所有设备：
        // 首次安装前若启动时插着陌生/BadUSB，会被静默信任，违背"陌生设备报警锁屏"。
        // 真正的旧格式迁移由安装脚本在装之前完成；这里空名单就是空名单。
        if (arr.Length == 0) { RebuildWhiteIndex(); return true; }
        bool allString = true, allDict = true;
        foreach (object o in arr) {
            if (o is string) allDict = false;
            else if (o is Dictionary<string, object>) allString = false;
            else { allString = false; allDict = false; }
        }
        if (allString) {
            Dictionary<string, WhiteEntry> groups = new Dictionary<string, WhiteEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (object o in arr) {
                string raw = Convert.ToString(o);
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string id = raw.Trim().ToUpperInvariant();
                string gk = GroupKeyOf(id);
                WhiteEntry e;
                if (gk.StartsWith("UNGROUPED:", StringComparison.Ordinal) || !groups.TryGetValue(gk, out e)) {
                    e = new WhiteEntry();
                    e.description = "未知设备";
                    e.category = ClassifyOf(id, null, false);
                    e.trustedAt = null;
                    whiteEntries.Add(e);
                    if (!gk.StartsWith("UNGROUPED:", StringComparison.Ordinal)) groups[gk] = e;
                }
                if (!e.deviceIds.Contains(id)) e.deviceIds.Add(id);
            }
            RebuildWhiteIndex();
            try {
                Dictionary<string, string[]> snapNow = SnapshotV2();
                if (snapNow != null) {
                    int added = 0;
                    foreach (KeyValuePair<string, string[]> kv in snapNow) {
                        if (!IsMonitorableId(kv.Key)) continue;
                        if (IsTrustedId(kv.Key)) continue;
                        AddTrustedIds(new string[] { kv.Key }, kv.Value[0]);
                        added++;
                    }
                    if (added > 0) Log("INFO", "WHITELIST_MIGRATE", null, "auto-trust current devices +" + added);
                }
            } catch { }
            try {
                string bak = whitelistPath + ".pre-v2.bak";
                if (!File.Exists(bak)) File.Copy(whitelistPath, bak);
            } catch { }
            bool ok = SaveWhiteV2();
            Log("INFO", "WHITELIST_MIGRATE", null, "legacy " + arr.Length + " ids -> " + whiteEntries.Count + " entries, save=" + ok);
            return true;
        }
        if (allDict) {
            foreach (object o in arr) {
                Dictionary<string, object> d = (Dictionary<string, object>)o;
                WhiteEntry e = new WhiteEntry();
                object v;
                if (d.TryGetValue("deviceIds", out v) && v is object[]) {
                    foreach (object x in (object[])v) {
                        string id = Convert.ToString(x);
                        if (!string.IsNullOrWhiteSpace(id)) e.deviceIds.Add(id.Trim().ToUpperInvariant());
                    }
                }
                if (e.deviceIds.Count == 0) { Log("WARN", "WHITELIST_MIGRATE", null, "entry skipped: no deviceIds"); continue; }
                e.description = d.TryGetValue("description", out v) ? Convert.ToString(v) : "未知设备";
                e.category = d.TryGetValue("category", out v) ? Convert.ToString(v) : "other";
                e.trustedAt = d.TryGetValue("trustedAt", out v) ? Convert.ToString(v) : null;
                whiteEntries.Add(e);
            }
            RebuildWhiteIndex();
            return true;
        }
        return false;
    }

    static bool SaveWhiteV2() {
        try {
            List<WhiteEntry> sorted = SnapshotEntries();
            List<Dictionary<string, object>> outList = new List<Dictionary<string, object>>();
            foreach (WhiteEntry e in sorted) {
                List<string> ids = new List<string>(e.deviceIds);
                ids.Sort(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["deviceIds"] = ids.ToArray();
                d["description"] = e.description ?? "未知设备";
                d["category"] = e.category ?? "other";
                d["trustedAt"] = e.trustedAt;
                outList.Add(d);
            }
            JavaScriptSerializer ser = new JavaScriptSerializer();
            ser.MaxJsonLength = 10 * 1024 * 1024;
            return AtomicWriteUtf8(whitelistPath, ser.Serialize(outList));
        } catch { return false; }
    }

    static string NowIso() {
        return DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz");
    }

    static void AddTrustedIds(IEnumerable<string> ids, string name) {
        Dictionary<string, WhiteEntry> groups = new Dictionary<string, WhiteEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (WhiteEntry e in whiteEntries) {
            if (e.deviceIds.Count > 0) {
                string gk = GroupKeyOf(e.deviceIds[0]);
                if (!groups.ContainsKey(gk)) groups[gk] = e;
            }
        }
        foreach (string raw in ids) {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string id = raw.Trim().ToUpperInvariant();
            if (IsTrustedId(id)) continue;
            string gk = GroupKeyOf(id);
            WhiteEntry e;
            if (!groups.TryGetValue(gk, out e)) {
                e = new WhiteEntry();
                e.description = string.IsNullOrEmpty(name) ? "未知设备" : name;
                e.category = ClassifyOf(id, null, false);
                e.trustedAt = NowIso();
                whiteEntries.Add(e);
                groups[gk] = e;
            }
            if (!e.deviceIds.Contains(id)) e.deviceIds.Add(id);
            if (string.IsNullOrEmpty(e.trustedAt)) e.trustedAt = NowIso();
            whiteIndex[id] = e;
            if (!white.Contains(id)) white.Add(id);
        }
    }

    static bool RemoveEntryById(string anyId) {
        if (string.IsNullOrEmpty(anyId)) return false;
        WhiteEntry e;
        if (!whiteIndex.TryGetValue(anyId.Trim().ToUpperInvariant(), out e)) return false;
        whiteEntries.Remove(e);
        RebuildWhiteIndex();
        return true;
    }

    internal static void RemoveEntryAndSave(string anyId) {
        bool removed = RemoveEntryById(anyId);
        if (!removed) return;
        Log("INFO", "DEVICE_REMOVE", anyId, "entry removed");
        if (SaveWhiteV2()) return;
        int rc = RunElevatedSelf("--remove-entry \"" + anyId + "\"");
        LoadWhiteV2();
        try {
            notify.BalloonTipTitle = "USB 哨兵";
            notify.BalloonTipText = rc == 0 ? "已删除并保存" : (rc == -1 ? "已取消授权，名单未变更" : "保存失败，名单未变更");
            notify.BalloonTipIcon = rc == 0 ? ToolTipIcon.Info : ToolTipIcon.Warning;
            notify.ShowBalloonTip(4000);
        } catch { }
    }

    static bool LoadCfgV2() {
        lockOnUnknown = true; persistentLock = true; autoScanRemovable = true;
        scanEngineOrder = new List<string> { "kaspersky", "defender" };
        if (!File.Exists(cfgPath)) return true;
        string text;
        try { text = File.ReadAllText(cfgPath, Encoding.UTF8); }
        catch { return false; }
        Dictionary<string, object> d;
        try {
            JavaScriptSerializer ser = new JavaScriptSerializer();
            d = ser.Deserialize<Dictionary<string, object>>(text);
        } catch {
            try {
                string bak = cfgPath + ".corrupt.bak";
                if (!File.Exists(bak)) File.Copy(cfgPath, bak);
            } catch { }
            SaveCfgV2();
            return true;
        }
        if (d == null) return false;
        object v;
        if (d.TryGetValue("lockOnUnknown", out v)) { try { lockOnUnknown = Convert.ToBoolean(v); } catch { Log("WARN", "CONFIG_CHANGE", null, "lockOnUnknown invalid, default true"); } }
        if (d.TryGetValue("persistentLock", out v)) { try { persistentLock = Convert.ToBoolean(v); } catch { Log("WARN", "CONFIG_CHANGE", null, "persistentLock invalid, default true"); } }
        if (d.TryGetValue("autoScanRemovable", out v)) { try { autoScanRemovable = Convert.ToBoolean(v); } catch { Log("WARN", "CONFIG_CHANGE", null, "autoScanRemovable invalid, default true"); } }
        if (d.TryGetValue("scanEngineOrder", out v) && v is object[]) {
            List<string> order = new List<string>();
            foreach (object x in (object[])v) {
                string s = Convert.ToString(x);
                if (!string.IsNullOrEmpty(s)) order.Add(s.Trim().ToLowerInvariant());
            }
            if (order.Count == 2 && order.Contains("kaspersky") && order.Contains("defender")) scanEngineOrder = order;
            else Log("WARN", "CONFIG_CHANGE", null, "scanEngineOrder invalid, default kaspersky,defender");
        }
        return true;
    }

    static bool SaveCfgV2() {
        try {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["lockOnUnknown"] = lockOnUnknown;
            d["persistentLock"] = persistentLock;
            d["autoScanRemovable"] = autoScanRemovable;
            d["scanEngineOrder"] = scanEngineOrder.ToArray();
            JavaScriptSerializer ser = new JavaScriptSerializer();
            return AtomicWriteUtf8(cfgPath, ser.Serialize(d));
        } catch { return false; }
    }

    static int RunElevatedSelf(string args) {
        try {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = Path.Combine(dir, "UsbGuard.exe");
            if (!File.Exists(psi.FileName)) psi.FileName = System.Reflection.Assembly.GetExecutingAssembly().Location;
            psi.Arguments = args;
            psi.WorkingDirectory = dir;
            psi.UseShellExecute = true;
            psi.Verb = "runas";
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            Process p = Process.Start(psi);
            if (p == null) return -1;
            if (!p.WaitForExit(60000)) { try { p.Kill(); } catch { } return -2; }
            return p.ExitCode;
        } catch (Win32Exception wex) {
            if (wex.NativeErrorCode == 1223) return -1;
            Log("WARN", "CONFIG_CHANGE", null, "elevate failed: " + wex.Message);
            return -2;
        } catch (Exception ex) {
            Log("WARN", "CONFIG_CHANGE", null, "elevate failed: " + ex.Message);
            return -2;
        }
    }

    static void SaveCfgOrElevate() {
        if (SaveCfgV2()) return;
        string args = "--save-config " + (lockOnUnknown ? "1" : "0") + "," + (persistentLock ? "1" : "0") + "," + (autoScanRemovable ? "1" : "0") + "," + string.Join(",", scanEngineOrder.ToArray());
        int rc = RunElevatedSelf(args);
        if (rc == 0) { LoadCfgV2(); return; }
        LoadCfgV2();
        try {
            notify.BalloonTipTitle = "USB 哨兵";
            notify.BalloonTipText = rc == -1 ? "已取消授权，配置未变更" : "配置保存失败，已回滚";
            notify.BalloonTipIcon = ToolTipIcon.Warning;
            notify.ShowBalloonTip(4000);
        } catch { }
    }

    static int RunCliTrustCurrent() {
        LoadWhiteV2();
        Dictionary<string, string[]> snap = SnapshotV2();
        if (snap == null) return 1;
        List<string> ids = new List<string>();
        foreach (KeyValuePair<string, string[]> kv in snap) {
            if (!IsTrustedId(kv.Key) && IsMonitorableId(kv.Key)) ids.Add(kv.Key);
        }
        foreach (string id in ids) {
            string name = snap.ContainsKey(id) ? snap[id][0] : null;
            AddTrustedIds(new string[] { id }, name);
        }
        Log("INFO", "DEVICE_TRUST", null, "cli trust-current +" + ids.Count);
        return SaveWhiteV2() ? 0 : 1;
    }

    static int RunCliTrustIds(string csv) {
        LoadWhiteV2();
        List<string> ids = new List<string>();
        foreach (string s in csv.Split(',')) {
            if (!string.IsNullOrWhiteSpace(s)) ids.Add(s.Trim().ToUpperInvariant());
        }
        Dictionary<string, string[]> snap = SnapshotV2();
        foreach (string id in ids) {
            string name = null;
            if (snap != null && snap.ContainsKey(id)) name = snap[id][0];
            AddTrustedIds(new string[] { id }, name);
        }
        Log("INFO", "DEVICE_TRUST", string.Join(";", ids.ToArray()), "cli trust-ids " + ids.Count);
        return SaveWhiteV2() ? 0 : 1;
    }

    static int RunCliRemoveEntry(string anyId) {
        LoadWhiteV2();
        string id = (anyId ?? "").Trim().Trim('"');
        bool found = RemoveEntryById(id);
        Log("INFO", "DEVICE_REMOVE", id, "cli remove-entry found=" + found);
        if (!found) return 0;
        return SaveWhiteV2() ? 0 : 1;
    }

    static int RunCliSaveConfig(string csv) {
        string[] parts = csv.Split(',');
        if (parts.Length >= 3) {
            lockOnUnknown = parts[0].Trim() == "1";
            persistentLock = parts[1].Trim() == "1";
            autoScanRemovable = parts[2].Trim() == "1";
        }
        if (parts.Length >= 5) {
            string a = parts[3].Trim().ToLowerInvariant(), b = parts[4].Trim().ToLowerInvariant();
            if ((a == "kaspersky" && b == "defender") || (a == "defender" && b == "kaspersky")) {
                scanEngineOrder = new List<string> { a, b };
            }
        }
        Log("INFO", "CONFIG_CHANGE", null, "cli save-config");
        return SaveCfgV2() ? 0 : 1;
    }
}
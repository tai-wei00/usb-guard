using System;
using System.Collections.Generic;
using System.Management;
using System.Text.RegularExpressions;

static partial class UsbGuard {
    static Dictionary<string, string[]> SnapshotV2() {
        Dictionary<string, string[]> map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        try {
            ManagementObjectSearcher q = new ManagementObjectSearcher("SELECT DeviceID, Name, PNPClass FROM Win32_PnPEntity");
            foreach (ManagementObject o in q.Get()) {
                try {
                    string id = Convert.ToString(o["DeviceID"]);
                    if (string.IsNullOrEmpty(id)) continue;
                    string key = id.Trim().ToUpperInvariant();
                    string name = Convert.ToString(o["Name"]);
                    string cls = Convert.ToString(o["PNPClass"]);
                    map[key] = new string[] { name ?? "", cls ?? "" };
                } catch { }
            }
            return map;
        } catch { return null; }
    }

    static bool IsMonitorableId(string id) {
        if (string.IsNullOrEmpty(id)) return false;
        return id.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("USBSTOR\\", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("HID\\", StringComparison.OrdinalIgnoreCase);
    }

    static string ClassifyOf(string id, string pnpClass, bool hasRemovable) {
        if (hasRemovable) return "storage";
        if (!string.IsNullOrEmpty(id) && id.StartsWith("USBSTOR\\", StringComparison.OrdinalIgnoreCase)) return "storage";
        if (string.Equals(pnpClass, "DiskDrive", StringComparison.OrdinalIgnoreCase)) return "storage";
        if (string.Equals(pnpClass, "HIDClass", StringComparison.OrdinalIgnoreCase)) return "hid";
        if (!string.IsNullOrEmpty(id) && id.StartsWith("HID\\", StringComparison.OrdinalIgnoreCase)) return "hid";
        return "other";
    }

    internal static string CategoryName(string category) {
        if (category == "storage") return "U盘";
        if (category == "hid") return "键盘/鼠标类设备";
        return "其他 USB 设备";
    }

    static string CategoryAdvice(string category) {
        if (category == "storage") return "请勿拷贝/打开其中文件";
        if (category == "hid") return "警惕 BadUSB 输入注入（伪装键盘自动执行命令）";
        return "建议立即拔出，确认来源后再信任";
    }

    static string GroupKeyOf(string id) {
        if (string.IsNullOrEmpty(id)) return "UNGROUPED:";
        Match m = Regex.Match(id, "(VID_[0-9A-F]{4}&PID_[0-9A-F]{4})", RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value.ToUpperInvariant();
        return "UNGROUPED:" + id.ToUpperInvariant();
    }

    internal static string VidPidOf(string id) {
        if (string.IsNullOrEmpty(id)) return "—";
        Match m = Regex.Match(id, "(VID_[0-9A-F]{4}&PID_[0-9A-F]{4})", RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value.ToUpperInvariant();
        m = Regex.Match(id, "(VID&[0-9A-F]{4}_PID&[0-9A-F]{4})", RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value.ToUpperInvariant();
        return "—";
    }

    static string NameOfId(Dictionary<string, string[]> snap, string id) {
        if (snap != null && id != null) {
            string[] v;
            if (snap.TryGetValue(id.Trim().ToUpperInvariant(), out v) && !string.IsNullOrEmpty(v[0])) return v[0];
        }
        return null;
    }
}
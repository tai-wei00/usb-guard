using System;
using System.Collections.Generic;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

internal static class DeviceGrouper {
    internal static List<PhysicalDevice> Group(IEnumerable<DeviceNode> input, long generation) {
        List<DeviceNode> nodes = new List<DeviceNode>();
        foreach (DeviceNode node in input) {
            if (DeviceIdentity.IsValidInstanceId(node.InstanceId)) nodes.Add(node.Clone());
        }
        Dictionary<string, HashSet<string>> parentContainers = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> parentCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (DeviceNode node in nodes) {
            string parent = DeviceIdentity.Normalize(node.ParentInstanceId);
            if (parent.Length == 0) continue;
            if (!parentCounts.ContainsKey(parent)) parentCounts[parent] = 0;
            parentCounts[parent]++;
            if (!parentContainers.ContainsKey(parent)) parentContainers[parent] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string container = NormalizeContainer(node.ContainerId);
            if (container.Length > 0) parentContainers[parent].Add(container);
        }
        Dictionary<string, PhysicalDevice> groups = new Dictionary<string, PhysicalDevice>(StringComparer.OrdinalIgnoreCase);
        foreach (DeviceNode node in nodes) {
            string container = NormalizeContainer(node.ContainerId);
            string parent = DeviceIdentity.Normalize(node.ParentInstanceId);
            string key;
            bool complete = true;
            if (container.Length > 0) {
                key = "CONTAINER:" + container;
            } else if (parent.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase)
                && parentCounts.ContainsKey(parent) && parentCounts[parent] > 1
                && parentContainers.ContainsKey(parent) && parentContainers[parent].Count == 0) {
                key = "PARENT:" + parent;
            } else {
                key = "INSTANCE:" + DeviceIdentity.Normalize(node.InstanceId);
                complete = container.Length > 0 || parent.Length > 0;
            }
            PhysicalDevice group;
            if (!groups.TryGetValue(key, out group)) {
                group = new PhysicalDevice();
                group.GroupKey = key;
                group.SnapshotGeneration = generation;
                group.IdentityComplete = complete;
                groups.Add(key, group);
            }
            group.Nodes.Add(node);
            if (!complete) group.IdentityComplete = false;
            if (string.IsNullOrEmpty(group.Description) || group.Description == "未知设备") group.Description = string.IsNullOrEmpty(node.Description) ? "未知设备" : node.Description;
            if (node.Category == DeviceCategory.Storage) group.Category = DeviceCategory.Storage;
            else if (node.Category == DeviceCategory.Hid && group.Category != DeviceCategory.Storage) group.Category = DeviceCategory.Hid;
        }
        List<PhysicalDevice> result = new List<PhysicalDevice>(groups.Values);
        result.Sort(delegate(PhysicalDevice a, PhysicalDevice b) { return string.Compare(a.GroupKey, b.GroupKey, StringComparison.OrdinalIgnoreCase); });
        return result;
    }

    private static string NormalizeContainer(string value) {
        Guid guid;
        if (Guid.TryParse(DeviceIdentity.Normalize(value), out guid)) return guid.ToString("D").ToUpperInvariant();
        return "";
    }
}

internal sealed class DeviceInventory {
    private long generation;

    internal DeviceSnapshot CaptureSnapshot() {
        long current = System.Threading.Interlocked.Increment(ref generation);
        DeviceSnapshot snapshot = new DeviceSnapshot();
        snapshot.Generation = current;
        List<DeviceNode> nodes = new List<DeviceNode>();
        try {
            using (ManagementObjectSearcher query = new ManagementObjectSearcher("SELECT DeviceID, Name, PNPClass FROM Win32_PnPEntity"))
            using (ManagementObjectCollection collection = query.Get()) {
                foreach (ManagementObject item in collection) {
                    try {
                        string id = DeviceIdentity.Normalize(Convert.ToString(item["DeviceID"]));
                        if (!DeviceIdentity.IsValidInstanceId(id)) continue;
                        DeviceNode node = new DeviceNode();
                        node.InstanceId = id;
                        node.Description = Convert.ToString(item["Name"]) ?? "";
                        node.PnpClass = Convert.ToString(item["PNPClass"]) ?? "";
                        node.ContainerId = ReadContainerId(id);
                        node.ParentInstanceId = GetParentInstanceId(id);
                        node.VidPid = VidPidOf(id);
                        node.Category = Classify(id, node.PnpClass, false);
                        nodes.Add(node);
                    } catch { }
                    finally { item.Dispose(); }
                }
            }
            snapshot.Devices = DeviceGrouper.Group(nodes, current);
            snapshot.RemovableDrives = GetRemovableDrives();
            foreach (PhysicalDevice group in snapshot.Devices) {
                foreach (DeviceNode node in group.Nodes) {
                    if (group.Category == DeviceCategory.Storage || node.PnpClass == "DiskDrive") node.HasRemovableVolume = group.Category == DeviceCategory.Storage;
                }
            }
            snapshot.Success = true;
        } catch (Exception ex) {
            snapshot.Success = false;
            snapshot.Error = ex.Message;
            snapshot.Devices = new List<PhysicalDevice>();
        }
        return snapshot;
    }

    internal PhysicalDevice ResolveTarget(TrustRequest request, DeviceSnapshot current) {
        if (request == null || current == null || !current.Success || current.Generation < request.SnapshotGeneration) return null;
        foreach (PhysicalDevice group in current.Devices) {
            if (!string.Equals(group.GroupKey, request.GroupKey, StringComparison.OrdinalIgnoreCase)) continue;
            List<string> actual = group.InstanceIds();
            if (actual.Count != request.ExpectedInstanceIds.Count) return null;
            HashSet<string> expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string id in request.ExpectedInstanceIds) expected.Add(DeviceIdentity.Normalize(id));
            foreach (string id in actual) if (!expected.Contains(id)) return null;
            return group;
        }
        return null;
    }

    internal static DeviceCategory Classify(string id, string pnpClass, bool hasRemovable) {
        if (hasRemovable || id.StartsWith("USBSTOR\\", StringComparison.OrdinalIgnoreCase) || string.Equals(pnpClass, "DiskDrive", StringComparison.OrdinalIgnoreCase)) return DeviceCategory.Storage;
        if (id.StartsWith("HID\\", StringComparison.OrdinalIgnoreCase) || string.Equals(pnpClass, "HIDClass", StringComparison.OrdinalIgnoreCase)) return DeviceCategory.Hid;
        return DeviceCategory.Other;
    }

    internal static string VidPidOf(string id) {
        if (string.IsNullOrEmpty(id)) return "—";
        Match match = Regex.Match(id, "(VID_[0-9A-F]{4}&PID_[0-9A-F]{4})", RegexOptions.IgnoreCase);
        if (match.Success) return match.Groups[1].Value.ToUpperInvariant();
        match = Regex.Match(id, "(VID&[0-9A-F]{4}_PID&[0-9A-F]{4})", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : "—";
    }

    internal static HashSet<string> GetRemovableDrives() {
        HashSet<string> drives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try {
            foreach (DriveInfo drive in DriveInfo.GetDrives()) {
                try { if (drive.DriveType == DriveType.Removable && drive.IsReady) drives.Add(drive.Name.Substring(0, 2).ToUpperInvariant()); } catch { }
            }
        } catch { }
        return drives;
    }

    private static string ReadContainerId(string id) {
        try {
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + id)) {
                if (key == null) return "";
                object value = key.GetValue("ContainerID", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                return value == null ? "" : Convert.ToString(value);
            }
        } catch { return ""; }
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNode(out uint deviceInstance, string deviceId, uint flags);
    [DllImport("cfgmgr32.dll")]
    private static extern int CM_GetParent(out uint parent, uint child, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID(uint deviceInstance, StringBuilder buffer, int bufferLength, uint flags);

    private static string GetParentInstanceId(string id) {
        try {
            uint instance;
            if (CM_Locate_DevNode(out instance, id, 0) != 0) return "";
            uint parent;
            if (CM_GetParent(out parent, instance, 0) != 0) return "";
            StringBuilder buffer = new StringBuilder(1024);
            if (CM_Get_Device_ID(parent, buffer, buffer.Capacity, 0) != 0) return "";
            return DeviceIdentity.Normalize(buffer.ToString());
        } catch { return ""; }
    }
}
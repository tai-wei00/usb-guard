using System;
using System.Collections.Generic;

internal enum GuardMode { Unknown, Protecting, Paused, Repairing, Stopped, WhitelistError }
internal enum OperationResultKind { Success, Failed, Cancelled, Partial, Unchanged, Conflict }
internal enum StateChangeSource { Startup, Tray, MainPanel, Alert, Expiration, Watchdog, Degraded, Repair }
internal enum RuntimeIntentKind { Paused, Repairing }
internal enum DeviceCategory { Storage, Hid, Other, Unrecognized }
internal enum WhitelistEntryKind { TrustedDevice, Unrecognized }
internal enum ScanState { NotScanned, Scanning, Clean, ThreatFound, Cancelled, Failed }
internal enum ScanEngineKind { Kaspersky, Defender }
internal enum MutationKind { Trust, Remove, Config }
internal enum ProtectionAction { Stop, Enable }
internal enum ShellCommandKind {
    ShowMain, DeviceManager, ScanNow, Pause30, Pause120, Repair120, Resume,
    StopProtection, EnableProtection, ShowLog, Retry, RunInstaller,
    ToggleLock, TogglePersistent, ToggleAutoScan, SyncAllow, LockNow, ExitNormal
}

internal static class GuardText {
    internal static string StateName(GuardMode mode) {
        switch (mode) {
            case GuardMode.Protecting: return "防护中";
            case GuardMode.Paused: return "临时暂停";
            case GuardMode.Repairing: return "维修模式";
            case GuardMode.Stopped: return "彻底停止";
            case GuardMode.WhitelistError: return "白名单异常";
            default: return "状态未知";
        }
    }
    internal static string ResultName(OperationResultKind kind) {
        switch (kind) {
            case OperationResultKind.Success: return "成功";
            case OperationResultKind.Cancelled: return "已取消";
            case OperationResultKind.Partial: return "部分完成";
            case OperationResultKind.Unchanged: return "未变化";
            case OperationResultKind.Conflict: return "冲突";
            default: return "失败";
        }
    }
    internal static string CategoryName(DeviceCategory category) {
        switch (category) {
            case DeviceCategory.Storage: return "U盘";
            case DeviceCategory.Hid: return "键盘/鼠标类设备";
            case DeviceCategory.Unrecognized: return "未识别条目";
            default: return "其他 USB 设备";
        }
    }
    internal static string SourceName(StateChangeSource source) {
        switch (source) {
            case StateChangeSource.Tray: return "托盘";
            case StateChangeSource.MainPanel: return "主面板";
            case StateChangeSource.Alert: return "报警窗";
            case StateChangeSource.Expiration: return "到期恢复";
            case StateChangeSource.Watchdog: return "看护恢复";
            case StateChangeSource.Degraded: return "异常降级";
            case StateChangeSource.Repair: return "安装修复";
            default: return "启动";
        }
    }
    internal static string ScanName(ScanState state) {
        switch (state) {
            case ScanState.Scanning: return "扫描中";
            case ScanState.Clean: return "无威胁";
            case ScanState.ThreatFound: return "发现威胁";
            case ScanState.Cancelled: return "已取消";
            case ScanState.Failed: return "扫描失败";
            default: return "尚未扫描";
        }
    }
}

internal sealed class OperationResult {
    public OperationResultKind Kind;
    public string Message = "";
    public List<ControlItemResult> FailedItems = new List<ControlItemResult>();
    internal static OperationResult Create(OperationResultKind kind, string message) {
        OperationResult result = new OperationResult();
        result.Kind = kind;
        result.Message = message ?? "";
        return result;
    }
    internal string Summary() {
        List<string> failures = new List<string>();
        foreach (ControlItemResult item in FailedItems) failures.Add(item.Name + ": " + item.Message);
        return Message + (failures.Count > 0 ? "；" + string.Join("；", failures.ToArray()) : "");
    }
}

internal sealed class ControlItemResult {
    public string Name = "";
    public string ExpectedState = "";
    public string ActualState = "";
    public string ErrorCode = "";
    public string Message = "";
    public bool Success;
    internal ControlItemResult() { Success = false; }
}

internal sealed class GuardStateSnapshot {
    public GuardMode Mode = GuardMode.Unknown;
    public DateTimeOffset? Deadline;
    public StateChangeSource Source = StateChangeSource.Startup;
    public OperationResultKind Result = OperationResultKind.Unchanged;
    public string Detail = "正在初始化";
    public long Revision;
    public bool HasUnknownDevices;
    public bool IsBlockingEnabled() { return Mode == GuardMode.Protecting; }
    public string DisplayName() { return GuardText.StateName(Mode); }
    public GuardStateSnapshot Clone() {
        GuardStateSnapshot copy = (GuardStateSnapshot)MemberwiseClone();
        return copy;
    }
}

internal sealed class RuntimeIntent {
    public string kind = "pause";
    public string until = "";
    public string source = "Startup";
    public string revision = "";
    internal RuntimeIntentKind Kind() { return kind == "repair" ? RuntimeIntentKind.Repairing : RuntimeIntentKind.Paused; }
}

internal enum RuntimeIntentLoadStatus { Missing, Loaded, Expired, Corrupt, IoError }
internal sealed class RuntimeIntentLoadResult {
    public RuntimeIntentLoadStatus Status;
    public RuntimeIntent Intent;
    public string Message = "";
}

internal sealed class GuardStateStatusDocument {
    public string mode = "Unknown";
    public string displayName = "状态未知";
    public string until = "";
    public string source = "Startup";
    public string result = "Unchanged";
    public string detail = "";
    public long revision;
}

internal sealed class ProtectionTransitionResult {
    public ProtectionAction Action;
    public GuardMode ActualMode = GuardMode.Unknown;
    public OperationResultKind Result = OperationResultKind.Failed;
    public string Message = "";
    public bool StopFlagPresent;
    public List<ControlItemResult> FailedItems = new List<ControlItemResult>();
    public List<ControlItemResult> Tasks = new List<ControlItemResult>();
}

internal sealed class DeviceNode {
    public string InstanceId = "";
    public string Description = "";
    public string PnpClass = "";
    public string ContainerId = "";
    public string ParentInstanceId = "";
    public string VidPid = "—";
    public DeviceCategory Category = DeviceCategory.Other;
    public bool HasRemovableVolume;
    public DeviceNode Clone() { return (DeviceNode)MemberwiseClone(); }
}

internal sealed class PhysicalDevice {
    public string GroupKey = "";
    public string Description = "未知设备";
    public DeviceCategory Category = DeviceCategory.Other;
    public long SnapshotGeneration;
    public bool IdentityComplete = true;
    public List<DeviceNode> Nodes = new List<DeviceNode>();
    public bool Contains(string instanceId) {
        string normalized = DeviceIdentity.Normalize(instanceId);
        foreach (DeviceNode node in Nodes) {
            if (DeviceIdentity.Equals(node.InstanceId, normalized)) return true;
        }
        return false;
    }
    public List<string> InstanceIds() {
        List<string> ids = new List<string>();
        foreach (DeviceNode node in Nodes) ids.Add(DeviceIdentity.Normalize(node.InstanceId));
        return ids;
    }
}

internal sealed class DeviceSnapshot {
    public long Generation;
    public bool Success;
    public string Error = "";
    public DateTimeOffset CapturedAt = DateTimeOffset.Now;
    public List<PhysicalDevice> Devices = new List<PhysicalDevice>();
    public HashSet<string> RemovableDrives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}

internal static class DeviceIdentity {
    internal static string Normalize(string id) { return id == null ? "" : id.Trim(); }
    internal static bool Equals(string left, string right) {
        return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
    }
    internal static bool IsMonitorable(string id) {
        string value = Normalize(id);
        return value.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("USBSTOR\\", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("HID\\", StringComparison.OrdinalIgnoreCase);
    }
    internal static bool IsValidInstanceId(string id) {
        string value = Normalize(id);
        if (!IsMonitorable(value) || value.IndexOf('*') >= 0 || value.IndexOf('?') >= 0) return false;
        string[] parts = value.Split('\\');
        if (parts.Length < 3 || parts[0].Length == 0 || parts[1].Length == 0 || parts[2].Length == 0) return false;
        if (parts[1].StartsWith("ROOT_HUB", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }
}

internal sealed class WhitelistEntry {
    public List<string> deviceIds = new List<string>();
    public string description = "未知设备";
    public string category = "other";
    public string trustedAt = null;
    [System.Web.Script.Serialization.ScriptIgnore]
    public WhitelistEntryKind Kind = WhitelistEntryKind.TrustedDevice;
    [System.Web.Script.Serialization.ScriptIgnore]
    public string OriginalPayload = "";
    internal string CategoryText() { return Kind == WhitelistEntryKind.Unrecognized ? "未识别条目" : GuardText.CategoryName(CategoryFromString(category)); }
    internal static DeviceCategory CategoryFromString(string value) {
        if (value == "storage") return DeviceCategory.Storage;
        if (value == "hid") return DeviceCategory.Hid;
        if (value == "unrecognized") return DeviceCategory.Unrecognized;
        return DeviceCategory.Other;
    }
    internal static string CategoryToString(DeviceCategory value) {
        switch (value) {
            case DeviceCategory.Storage: return "storage";
            case DeviceCategory.Hid: return "hid";
            case DeviceCategory.Unrecognized: return "unrecognized";
            default: return "other";
        }
    }
    internal bool Matches(string instanceId) {
        if (Kind != WhitelistEntryKind.TrustedDevice || !DeviceIdentity.IsValidInstanceId(instanceId)) return false;
        foreach (string id in deviceIds) {
            if (DeviceIdentity.IsValidInstanceId(id) && DeviceIdentity.Equals(id, instanceId)) return true;
        }
        return false;
    }
    internal WhitelistEntry Clone() {
        WhitelistEntry copy = new WhitelistEntry();
        copy.deviceIds = new List<string>(deviceIds);
        copy.description = description;
        copy.category = category;
        copy.trustedAt = trustedAt;
        copy.Kind = Kind;
        copy.OriginalPayload = OriginalPayload;
        return copy;
    }
}

internal sealed class WhitelistDocument {
    public List<WhitelistEntry> Entries = new List<WhitelistEntry>();
    public string Revision = "";
    public string RawText = "";
    internal WhitelistDocument Clone() {
        WhitelistDocument copy = new WhitelistDocument();
        copy.Revision = Revision;
        copy.RawText = RawText;
        foreach (WhitelistEntry entry in Entries) copy.Entries.Add(entry.Clone());
        return copy;
    }
    internal bool IsTrusted(string instanceId) {
        if (!DeviceIdentity.IsValidInstanceId(instanceId)) return false;
        foreach (WhitelistEntry entry in Entries) if (entry.Matches(instanceId)) return true;
        return false;
    }
    internal int TrustedCount() {
        int count = 0;
        foreach (WhitelistEntry entry in Entries) if (entry.Kind == WhitelistEntryKind.TrustedDevice) count++;
        return count;
    }
    internal WhitelistEntry Find(string instanceId) {
        foreach (WhitelistEntry entry in Entries) if (entry.Matches(instanceId)) return entry;
        return null;
    }
}

internal enum WhitelistLoadStatus { Loaded, Empty, Corrupt, IoError }
internal sealed class WhitelistLoadResult {
    public WhitelistLoadStatus Status;
    public WhitelistDocument Document = new WhitelistDocument();
    public string Message = "";
    public bool Healthy { get { return Status == WhitelistLoadStatus.Loaded || Status == WhitelistLoadStatus.Empty; } }
}

internal sealed class ConfigDocument {
    public bool lockOnUnknown = true;
    public bool persistentLock = true;
    public bool autoScanRemovable = true;
    public string[] scanEngineOrder = new string[] { "kaspersky", "defender" };
    [System.Web.Script.Serialization.ScriptIgnore]
    public string Revision = "";
    internal ConfigDocument Clone() {
        ConfigDocument copy = (ConfigDocument)MemberwiseClone();
        copy.scanEngineOrder = scanEngineOrder == null ? new string[0] : (string[])scanEngineOrder.Clone();
        return copy;
    }
}

internal sealed class AtomicWriteResult {
    public OperationResultKind Kind = OperationResultKind.Failed;
    public string Message = "";
    public string Revision = "";
    [System.Web.Script.Serialization.ScriptIgnore]
    public bool RequiresElevation;
}

internal sealed class TrustRequest {
    public string GroupKey = "";
    public long SnapshotGeneration;
    public List<string> ExpectedInstanceIds = new List<string>();
    public string Description = "未知设备";
    public string Category = "other";
}

internal sealed class MutationRequest {
    public string RequestId = Guid.NewGuid().ToString("N");
    public MutationKind Operation;
    public string ExpectedRevision = "";
    public TrustRequest Trust;
    public string EntryInstanceId = "";
    public ConfigDocument Config;
    public DateTimeOffset RequestedAt = DateTimeOffset.Now;
}

internal sealed class MutationResult {
    public string RequestId = "";
    public OperationResultKind Kind = OperationResultKind.Failed;
    public string Message = "";
    public string NewRevision = "";
    [System.Web.Script.Serialization.ScriptIgnore]
    public bool RequiresElevation;
    internal OperationResult ToOperationResult() { return OperationResult.Create(Kind, Message); }
}

internal sealed class ScanRequest {
    public string DriveRoot = "";
    public long MountGeneration;
    public bool Manual;
    public StateChangeSource Source = StateChangeSource.MainPanel;
}

internal sealed class ScanSummary {
    public string DriveRoot = "";
    public long MountGeneration;
    public ScanState State = ScanState.NotScanned;
    public ScanEngineKind? Engine;
    public string LastResult = "尚未扫描";
    public DateTimeOffset? CompletedAt;
    public bool IsTerminal;
    public ScanSummary Clone() { return (ScanSummary)MemberwiseClone(); }
}

internal sealed class ScanStateChangedEventArgs : EventArgs {
    public ScanSummary Summary;
    internal ScanStateChangedEventArgs(ScanSummary summary) { Summary = summary; }
}

internal sealed class DiagnosticEvent {
    public DateTimeOffset Timestamp = DateTimeOffset.Now;
    public string Level = "INFO";
    public string Source = "Startup";
    public string EventType = "STATE";
    public string FromState = "";
    public string TargetState = "";
    public OperationResultKind Result = OperationResultKind.Unchanged;
    public string TargetSummary = "";
    public string Message = "";
    internal string Fingerprint() {
        return Source + "|" + EventType + "|" + FromState + "|" + TargetState + "|" + Result + "|" + TargetSummary + "|" + Message;
    }
    internal string FormatLine() {
        return Timestamp.ToString("yyyy-MM-dd HH:mm:ss") + "  " + Level + " | " + EventType
            + " | source=" + Source + " | from=" + FromState + " | target=" + TargetState
            + " | result=" + Result + " | " + (TargetSummary ?? "") + " | " + (Message ?? "");
    }
    public DiagnosticEvent Clone() { return (DiagnosticEvent)MemberwiseClone(); }
}

internal sealed class BuildInfo {
    public string ProductVersion = "";
    public string BuildDate = "";
    public string SourceManifestHash = "";
    public string Consistency = "待核验";
}

internal sealed class MainPanelSnapshot {
    public GuardStateSnapshot Guard = new GuardStateSnapshot();
    public int UnknownCount;
    public int TrustedDeviceCount;
    public List<ScanSummary> Scans = new List<ScanSummary>();
    public List<DiagnosticEvent> RecentEvents = new List<DiagnosticEvent>();
    public ConfigDocument Config = new ConfigDocument();
    public BuildInfo Build = new BuildInfo();
    public string LogHealth = "日志正常";
    public bool Refreshing = true;
    public string OperationMessage = "";
}

internal sealed class ShellCommand {
    public ShellCommandKind Kind;
    public bool Value;
    internal ShellCommand(ShellCommandKind kind) { Kind = kind; }
    internal ShellCommand(ShellCommandKind kind, bool value) { Kind = kind; Value = value; }
}

internal sealed class TrayOperationResult {
    public bool Success;
    public string Message = "";
}

internal sealed class CommandLineOptions {
    public string Operation = "guard";
    public string RequestPath = "";
    public bool ShowMain;
    public string TestCategory = "";
}
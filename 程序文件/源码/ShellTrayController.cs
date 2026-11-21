using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal sealed class ShellTrayController : IDisposable {
    private sealed class TrayMessageWindow : NativeWindow, IDisposable {
        private readonly int taskbarCreated;
        internal event Action Created;
        internal TrayMessageWindow() {
            taskbarCreated = RegisterWindowMessage("TaskbarCreated");
            if (taskbarCreated == 0) throw new InvalidOperationException("TaskbarCreated 消息注册失败");
            CreateHandle(new CreateParams());
        }
        protected override void WndProc(ref Message message) {
            if (message.Msg == taskbarCreated) {
                Action handler = Created;
                if (handler != null) handler();
            }
            base.WndProc(ref message);
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int RegisterWindowMessage(string message);
        public void Dispose() { if (Handle != IntPtr.Zero) ReleaseHandle(); }
    }

    private NotifyIcon notify;
    private ContextMenuStrip menu;
    private TrayMessageWindow messageWindow;
    private Icon currentIcon;
    private GuardStateSnapshot state = new GuardStateSnapshot();
    private ConfigDocument config = new ConfigDocument();
    private readonly System.Windows.Forms.Timer debounce;
    private readonly System.Windows.Forms.Timer retry;
    private int recoveryAttempts;
    private bool disposed;
    private ToolStripMenuItem statusItem;
    private ToolStripMenuItem pauseItem;
    private ToolStripMenuItem repairItem;
    private ToolStripMenuItem stopItem;
    private ToolStripMenuItem enableItem;
    private ToolStripMenuItem lockItem;
    private ToolStripMenuItem persistentItem;
    private ToolStripMenuItem autoScanItem;

    internal event Action<ShellCommand> CommandRequested;
    internal event Action<string> RecoveryWarning;

    internal ShellTrayController() {
        debounce = new System.Windows.Forms.Timer();
        debounce.Interval = 120;
        debounce.Tick += delegate { debounce.Stop(); Recover(); };
        retry = new System.Windows.Forms.Timer();
        retry.Tick += delegate { retry.Stop(); Recover(); };
    }

    internal TrayOperationResult EnsureVisible() {
        try {
            if (disposed) throw new ObjectDisposedException("ShellTrayController");
            if (messageWindow == null) {
                messageWindow = new TrayMessageWindow();
                messageWindow.Created += delegate { debounce.Stop(); debounce.Start(); };
            }
            DestroyTrayOnly();
            notify = new NotifyIcon();
            menu = new ContextMenuStrip();
            BuildMenu();
            notify.ContextMenuStrip = menu;
            notify.DoubleClick += delegate { Command(ShellCommandKind.ShowMain); };
            ApplyVisualState();
            notify.Visible = true;
            recoveryAttempts = 0;
            retry.Stop();
            return new TrayOperationResult { Success = true, Message = "托盘可用" };
        } catch (Exception ex) {
            return new TrayOperationResult { Success = false, Message = ex.Message };
        }
    }

    private void Recover() {
        TrayOperationResult result = EnsureVisible();
        if (result.Success) return;
        recoveryAttempts++;
        if (recoveryAttempts == 2) {
            Action<string> warning = RecoveryWarning;
            if (warning != null) warning("托盘暂不可用：" + result.Message + "；防护仍在运行，请使用主面板重试或安装修复");
        }
        retry.Interval = recoveryAttempts == 1 ? 300 : (recoveryAttempts == 2 ? 1500 : 5000);
        retry.Start();
    }

    internal void ApplyState(GuardStateSnapshot snapshot) {
        state = snapshot == null ? new GuardStateSnapshot() : snapshot.Clone();
        ApplyVisualState();
    }

    internal void ApplyConfig(ConfigDocument document) {
        config = document == null ? new ConfigDocument() : document.Clone();
    }

    private void ApplyVisualState() {
        if (notify == null) return;
        string name = state.DisplayName();
        if (state.HasUnknownDevices && state.Mode == GuardMode.Protecting) name += "·陌生设备";
        string remaining = "";
        if (state.Deadline.HasValue) {
            int minutes = Math.Max(1, (int)Math.Ceiling((state.Deadline.Value - DateTimeOffset.Now).TotalMinutes));
            remaining = "，剩 " + minutes + " 分钟";
        }
        string text = "USB 哨兵：" + name + remaining;
        notify.Text = text.Length > 63 ? text.Substring(0, 63) : text;
        SetIcon(CreateIcon(state));
        if (statusItem != null) statusItem.Text = "状态：" + name + remaining;
        if (pauseItem != null) pauseItem.Enabled = state.Mode != GuardMode.Stopped && state.Mode != GuardMode.WhitelistError;
        if (repairItem != null) repairItem.Text = state.Mode == GuardMode.Repairing ? "提前恢复完整防护" : "维修模式（2 小时）";
        if (stopItem != null) stopItem.Visible = state.Mode != GuardMode.Stopped;
        if (enableItem != null) enableItem.Visible = state.Mode == GuardMode.Stopped;
        if (lockItem != null) lockItem.Checked = config.lockOnUnknown;
        if (persistentItem != null) persistentItem.Checked = config.persistentLock;
        if (autoScanItem != null) autoScanItem.Checked = config.autoScanRemovable;
    }

    private void BuildMenu() {
        statusItem = new ToolStripMenuItem("状态：状态未知");
        statusItem.Enabled = false;
        menu.Items.Add(statusItem);
        Add("打开主面板", ShellCommandKind.ShowMain);
        Add("设备管理…", ShellCommandKind.DeviceManager);
        Add("立即扫描当前 U 盘", ShellCommandKind.ScanNow);
        pauseItem = new ToolStripMenuItem("临时暂停");
        Add(pauseItem, "暂停 30 分钟", ShellCommandKind.Pause30);
        Add(pauseItem, "暂停 2 小时", ShellCommandKind.Pause120);
        Add(pauseItem, "立即恢复防护", ShellCommandKind.Resume);
        menu.Items.Add(pauseItem);
        repairItem = Add("维修模式（2 小时）", ShellCommandKind.Repair120);

        lockItem = AddCheck("陌生设备自动锁屏", ShellCommandKind.ToggleLock);
        persistentItem = AddCheck("陌生设备持续锁屏", ShellCommandKind.TogglePersistent);
        autoScanItem = AddCheck("插入 U 盘自动扫描", ShellCommandKind.ToggleAutoScan);
        Add("同步系统 USB 锁定（需管理员）", ShellCommandKind.SyncAllow);
        stopItem = Add("彻底停止防护（需管理员）", ShellCommandKind.StopProtection);
        enableItem = Add("重新启用防护（需管理员）", ShellCommandKind.EnableProtection);
        Add("查看日志", ShellCommandKind.ShowLog);
        Add("重试状态刷新", ShellCommandKind.Retry);
        Add("运行安装修复", ShellCommandKind.RunInstaller);
        menu.Items.Add(new ToolStripSeparator());
        Add("普通退出（看护可能重新启动）", ShellCommandKind.ExitNormal);
        menu.Opening += delegate { ApplyVisualState(); };
    }

    private ToolStripMenuItem Add(string text, ShellCommandKind kind) {
        ToolStripMenuItem item = new ToolStripMenuItem(text);
        item.Click += delegate { Command(kind); };
        menu.Items.Add(item);
        return item;
    }
    private void Add(ToolStripMenuItem parent, string text, ShellCommandKind kind) {
        ToolStripMenuItem item = new ToolStripMenuItem(text);
        item.Click += delegate { Command(kind); };
        parent.DropDownItems.Add(item);
    }
    private ToolStripMenuItem AddCheck(string text, ShellCommandKind kind) {
        ToolStripMenuItem item = Add(text, kind);
        item.CheckOnClick = false;
        return item;
    }

    private void Command(ShellCommandKind kind) {
        if (kind == ShellCommandKind.Repair120 && state.Mode == GuardMode.Repairing) kind = ShellCommandKind.Resume;
        bool value = kind == ShellCommandKind.ToggleLock ? !config.lockOnUnknown
            : kind == ShellCommandKind.TogglePersistent ? !config.persistentLock
            : kind == ShellCommandKind.ToggleAutoScan && !config.autoScanRemovable;
        Action<ShellCommand> handler = CommandRequested;
        if (handler != null) handler(new ShellCommand(kind, value));
    }

    private void SetIcon(Icon icon) {
        if (notify == null || icon == null) return;
        Icon old = currentIcon;
        currentIcon = icon;
        notify.Icon = icon;
        if (old != null) old.Dispose();
    }

    private static Icon CreateIcon(GuardStateSnapshot snapshot) {
        Bitmap bitmap = new Bitmap(32, 32);
        Graphics graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        Color color = snapshot.Mode == GuardMode.Stopped || snapshot.Mode == GuardMode.Unknown ? Color.Gray
            : snapshot.Mode == GuardMode.WhitelistError ? Color.Goldenrod
            : snapshot.HasUnknownDevices && snapshot.Mode == GuardMode.Protecting ? Color.Firebrick : Color.RoyalBlue;
        using (SolidBrush brush = new SolidBrush(color)) graphics.FillEllipse(brush, 1, 1, 30, 30);
        string mark = snapshot.Mode == GuardMode.Paused ? "Ⅱ" : snapshot.Mode == GuardMode.Repairing ? "R"
            : snapshot.Mode == GuardMode.Unknown ? "?" : snapshot.Mode == GuardMode.WhitelistError ? "!" : "U";
        using (Font font = new Font("Segoe UI", 14, FontStyle.Bold, GraphicsUnit.Pixel))
        using (StringFormat format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            graphics.DrawString(mark, font, Brushes.White, new RectangleF(0, 0, 32, 32), format);
        IntPtr handle = bitmap.GetHicon();
        Icon icon = (Icon)Icon.FromHandle(handle).Clone();
        DestroyIcon(handle);
        graphics.Dispose();
        bitmap.Dispose();
        return icon;
    }

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);

    private void DestroyTrayOnly() {
        if (notify != null) {
            try { notify.Visible = false; } catch { }
            notify.ContextMenuStrip = null;
            notify.Dispose();
            notify = null;
        }
        if (menu != null) { menu.Dispose(); menu = null; }
        if (currentIcon != null) { currentIcon.Dispose(); currentIcon = null; }
    }

    internal void ShowNotification(string title, string text, ToolTipIcon icon) {
        if (notify == null) return;
        notify.BalloonTipTitle = title ?? "USB 哨兵";
        notify.BalloonTipText = text ?? "";
        notify.BalloonTipIcon = icon;
        notify.ShowBalloonTip(5000);
    }

    public void Dispose() {
        disposed = true;
        debounce.Stop(); debounce.Dispose();
        retry.Stop(); retry.Dispose();
        DestroyTrayOnly();
        if (messageWindow != null) { messageWindow.Dispose(); messageWindow = null; }
    }
}
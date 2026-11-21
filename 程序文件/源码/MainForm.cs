using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

internal sealed class MainForm : Form {
    private readonly Action<ShellCommand> onCommand;
    private Label stateLabel;
    private Label detailLabel;
    private Label countLabel;
    private Label refreshLabel;
    private Label scanLabel;
    private Label operationLabel;
    private Label healthLabel;
    private Label aboutLabel;
    private ListBox eventsList;
    private CheckBox lockCheck;
    private CheckBox persistentCheck;
    private CheckBox autoScanCheck;
    private Button stopButton;
    private Button enableButton;
    private bool updating;
    private bool allowClose;

    internal MainForm(Action<ShellCommand> commandHandler) {
        onCommand = commandHandler;
        Text = "USB 哨兵 · 主面板";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 650);
        Width = 1000; Height = 730;
        BackColor = Color.White;
        Font = new Font("Microsoft YaHei UI", 9f);
        BuildLayout();
        FormClosing += delegate(object sender, FormClosingEventArgs args) {
            if (!allowClose && args.CloseReason == CloseReason.UserClosing) {
                args.Cancel = true;
                Hide();
            }
        };
    }

    private GroupBox Group(string text, int x, int y, int width, int height, DockStyle dock = DockStyle.None) {
        GroupBox group = new GroupBox();
        group.Text = text;
        group.SetBounds(x, y, width, height);
        if (dock != DockStyle.None) group.Dock = dock;
        Controls.Add(group);
        return group;
    }

    private Label AddLabel(Control parent, string text, int x, int y, int width, int height, bool bold = false) {
        Label label = new Label();
        label.Text = text;
        label.SetBounds(x, y, width, height);
        if (bold) label.Font = new Font(Font, FontStyle.Bold);
        parent.Controls.Add(label);
        return label;
    }

    private Button AddButton(Control parent, string text, int x, int y, int width, ShellCommandKind kind) {
        Button button = new Button();
        button.Text = text;
        button.SetBounds(x, y, width, 34);
        button.Click += delegate { onCommand(new ShellCommand(kind)); };
        parent.Controls.Add(button);
        return button;
    }

    private void BuildLayout() {
        GroupBox summary = Group("防护状态", 12, 10, 960, 112);
        summary.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        stateLabel = AddLabel(summary, "状态未知", 14, 22, 360, 28, true);
        stateLabel.Font = new Font("Microsoft YaHei UI", 15f, FontStyle.Bold);
        detailLabel = AddLabel(summary, "正在初始化", 380, 25, 560, 45);
        countLabel = AddLabel(summary, "陌生设备：—    已信任设备：—", 14, 62, 560, 30);
        refreshLabel = AddLabel(summary, "正在刷新", 720, 67, 210, 24);
        refreshLabel.ForeColor = Color.Gray;

        GroupBox actions = Group("常用操作", 12, 130, 960, 166);
        actions.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        AddButton(actions, "设备管理", 14, 25, 130, ShellCommandKind.DeviceManager);
        AddButton(actions, "立即扫描", 154, 25, 130, ShellCommandKind.ScanNow);
        AddButton(actions, "暂停 30 分钟", 294, 25, 140, ShellCommandKind.Pause30);
        AddButton(actions, "暂停 2 小时", 444, 25, 140, ShellCommandKind.Pause120);
        AddButton(actions, "提前恢复", 594, 25, 120, ShellCommandKind.Resume);
        AddButton(actions, "查看日志", 724, 25, 100, ShellCommandKind.ShowLog);
        AddButton(actions, "重试", 834, 25, 105, ShellCommandKind.Retry);
        AddButton(actions, "维修 2 小时", 14, 70, 130, ShellCommandKind.Repair120);
        stopButton = AddButton(actions, "彻底停止（需管理员）", 154, 70, 190, ShellCommandKind.StopProtection);
        enableButton = AddButton(actions, "重新启用（需管理员）", 354, 70, 190, ShellCommandKind.EnableProtection);
        AddButton(actions, "运行安装修复", 554, 70, 150, ShellCommandKind.RunInstaller);
        AddButton(actions, "普通退出", 714, 70, 110, ShellCommandKind.ExitNormal);

        lockCheck = AddCheck(actions, "陌生设备自动锁屏", 14, 119, ShellCommandKind.ToggleLock);
        persistentCheck = AddCheck(actions, "持续锁屏", 220, 119, ShellCommandKind.TogglePersistent);
        autoScanCheck = AddCheck(actions, "插入 U 盘自动扫描", 380, 119, ShellCommandKind.ToggleAutoScan);
        operationLabel = AddLabel(actions, "操作结果会在此显示", 600, 116, 340, 40);
        operationLabel.ForeColor = Color.DimGray;

        GroupBox scan = Group("扫描状态（每盘独立保留当前挂载代次结果）", 12, 305, 960, 92);
        scan.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        scanLabel = AddLabel(scan, "尚未扫描", 14, 24, 925, 55);

        GroupBox diagnostics = Group("最近 20 条关键事件（倒序、去噪）", 12, 406, 960, 202);
        diagnostics.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        eventsList = new ListBox();
        eventsList.SetBounds(14, 24, 925, 135);
        eventsList.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        eventsList.HorizontalScrollbar = true;
        diagnostics.Controls.Add(eventsList);
        healthLabel = AddLabel(diagnostics, "日志正常", 14, 165, 925, 25);
        healthLabel.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

        aboutLabel = AddLabel(this, "版本待核验", 20, 620, 940, 50);
        aboutLabel.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        aboutLabel.ForeColor = Color.DimGray;
    }

    private CheckBox AddCheck(Control parent, string text, int x, int y, ShellCommandKind kind) {
        CheckBox box = new CheckBox();
        box.Text = text;
        box.SetBounds(x, y, 200, 32);
        box.CheckedChanged += delegate {
            if (!updating) onCommand(new ShellCommand(kind, box.Checked));
        };
        parent.Controls.Add(box);
        return box;
    }

    internal void ShowOrActivate(MainPanelSnapshot snapshot) {
        ApplyState(snapshot);
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        BringToFront();
        Activate();
    }

    internal void ApplyState(MainPanelSnapshot snapshot) {
        if (snapshot == null || updating) return;
        updating = true;
        try {
            GuardStateSnapshot guard = snapshot.Guard ?? new GuardStateSnapshot();
            string remaining = guard.Deadline.HasValue ? "（截止 " + guard.Deadline.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + "）" : "";
            stateLabel.Text = guard.DisplayName() + (guard.HasUnknownDevices && guard.Mode == GuardMode.Protecting ? " · 陌生设备报警" : "");
            stateLabel.ForeColor = guard.Mode == GuardMode.Protecting && !guard.HasUnknownDevices ? Color.SeaGreen : Color.Firebrick;
            detailLabel.Text = (guard.Detail ?? "") + remaining;
            countLabel.Text = "陌生设备：" + (snapshot.Refreshing ? "正在刷新" : snapshot.UnknownCount.ToString())
                + "    已信任设备：" + snapshot.TrustedDeviceCount;
            refreshLabel.Text = snapshot.Refreshing ? "正在刷新（显示最近有效状态）" : "状态修订 " + guard.Revision;
            stopButton.Visible = guard.Mode != GuardMode.Stopped;
            enableButton.Visible = guard.Mode == GuardMode.Stopped;
            lockCheck.Checked = snapshot.Config.lockOnUnknown;
            persistentCheck.Checked = snapshot.Config.persistentLock;
            autoScanCheck.Checked = snapshot.Config.autoScanRemovable;
            if (!string.IsNullOrEmpty(snapshot.OperationMessage)) operationLabel.Text = snapshot.OperationMessage;
            healthLabel.Text = snapshot.LogHealth;
            aboutLabel.Text = "USB 哨兵 v" + snapshot.Build.ProductVersion + "    构建日期：" + snapshot.Build.BuildDate
                + "    交付状态：" + snapshot.Build.Consistency + "    仅本机运行，不联网、不收集数据";
            UpdateScans(snapshot.Scans);
            UpdateEvents(snapshot.RecentEvents);
        } finally { updating = false; }
    }

    private void UpdateScans(List<ScanSummary> scans) {
        if (scans == null || scans.Count == 0) { scanLabel.Text = "尚未扫描"; return; }
        StringBuilder text = new StringBuilder();
        foreach (ScanSummary scan in scans) {
            text.Append(scan.DriveRoot).Append(" [代次 ").Append(scan.MountGeneration).Append("] ")
                .Append(GuardText.ScanName(scan.State)).Append("：").Append(scan.LastResult);
            if (scan.CompletedAt.HasValue) text.Append("（").Append(scan.CompletedAt.Value.ToLocalTime().ToString("MM-dd HH:mm:ss")).Append("）");
            text.Append("    ");
        }
        scanLabel.Text = text.ToString();
    }

    private void UpdateEvents(List<DiagnosticEvent> events) {
        eventsList.BeginUpdate();
        eventsList.Items.Clear();
        if (events != null) {
            foreach (DiagnosticEvent item in events) {
                eventsList.Items.Add(item.Timestamp.ToLocalTime().ToString("MM-dd HH:mm:ss") + "  " + item.EventType
                    + "  " + item.FromState + "→" + item.TargetState + "  " + GuardText.ResultName(item.Result) + "  " + item.Message);
            }
        }
        if (eventsList.Items.Count == 0) eventsList.Items.Add("暂无关键事件");
        eventsList.EndUpdate();
    }

    internal void ShowOperationResult(OperationResult result) {
        operationLabel.Text = GuardText.ResultName(result.Kind) + "：" + result.Summary();
    }

    internal void ExitFromRuntime() { allowClose = true; }
}
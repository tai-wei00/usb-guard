using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

internal sealed class DeviceManagerForm : Form {
    static DeviceManagerForm current;

    ListView list;
    TextBox detail;
    Button btnRemove, btnRefresh, btnClose;
    Label empty;
    bool saving;

    public static void ShowManager(IWin32Window owner) {
        if (current != null && !current.IsDisposed) {
            try { current.Activate(); return; } catch { }
        }
        DeviceManagerForm f = new DeviceManagerForm();
        current = f;
        if (owner != null) f.Show(owner); else f.Show();
    }

    public DeviceManagerForm() {
        Text = "USB 哨兵：设备管理";
        StartPosition = FormStartPosition.CenterScreen;
        Width = 960; Height = 520;
        MinimumSize = new Size(760, 420);
        BackColor = Color.White;

        list = new ListView();
        list.View = View.Details;
        list.FullRowSelect = true;
        list.MultiSelect = false;
        list.GridLines = true;
        list.SetBounds(12, 12, 720, 340);
        list.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        list.Columns.Add("设备描述", 220);
        list.Columns.Add("分类", 110);
        list.Columns.Add("VID+PID", 150);
        list.Columns.Add("实例 ID 摘要", 160);
        list.Columns.Add("信任时间", 150);
        list.SelectedIndexChanged += delegate { ShowDetail(); };
        Controls.Add(list);

        detail = new TextBox();
        detail.Multiline = true;
        detail.ReadOnly = true;
        detail.ScrollBars = ScrollBars.Vertical;
        detail.SetBounds(12, 360, 720, 110);
        detail.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        detail.Font = new Font("Consolas", 9f);
        Controls.Add(detail);

        btnRemove = new Button();
        btnRemove.Text = "删除选中";
        btnRemove.SetBounds(748, 12, 190, 34);
        btnRemove.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnRemove.Click += delegate { RemoveSelected(); };
        Controls.Add(btnRemove);

        btnRefresh = new Button();
        btnRefresh.Text = "刷新";
        btnRefresh.SetBounds(748, 56, 190, 34);
        btnRefresh.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnRefresh.Click += delegate { Reload(); };
        Controls.Add(btnRefresh);

        btnClose = new Button();
        btnClose.Text = "关闭";
        btnClose.SetBounds(748, 100, 190, 34);
        btnClose.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnClose.Click += delegate { Close(); };
        Controls.Add(btnClose);

        empty = new Label();
        empty.Text = "接入设备后在报警弹窗中点击「信任此设备」即可加入名单";
        empty.TextAlign = ContentAlignment.MiddleCenter;
        empty.ForeColor = Color.Gray;
        empty.Font = new Font("Microsoft YaHei UI", 11f);
        empty.SetBounds(12, 12, 720, 340);
        empty.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        empty.Visible = false;
        Controls.Add(empty);
        empty.BringToFront();

        FormClosed += delegate { if (current == this) current = null; };
        Reload();
    }

    static string Shorten(string s, int max) {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.Length <= max) return s;
        int keep = (max - 3) / 2;
        return s.Substring(0, keep) + "..." + s.Substring(s.Length - keep);
    }

    void Reload() {
        list.Items.Clear();
        List<UsbGuard.WhiteEntry> entries = UsbGuard.SnapshotEntries();
        foreach (UsbGuard.WhiteEntry e in entries) {
            string first = e.deviceIds.Count > 0 ? e.deviceIds[0] : "";
            string summary = Shorten(first, 54);
            if (e.deviceIds.Count > 1) summary += "（共 " + e.deviceIds.Count + " 个）";
            string vp = UsbGuard.VidPidOf(first);
            string desc = e.description;
            if (string.IsNullOrEmpty(desc) || (vp == "—" && desc == "未知设备")) desc = "未识别条目";
            ListViewItem it = new ListViewItem(desc);
            it.SubItems.Add(UsbGuard.CategoryName(e.category));
            it.SubItems.Add(vp);
            it.SubItems.Add(summary);
            it.SubItems.Add(string.IsNullOrEmpty(e.trustedAt) ? "—" : e.trustedAt);
            it.Tag = e;
            list.Items.Add(it);
        }
        bool has = entries.Count > 0;
        empty.Visible = !has;
        btnRemove.Enabled = has && !saving;
        detail.Text = "";
    }

    void ShowDetail() {
        if (list.SelectedItems.Count == 0) { detail.Text = ""; return; }
        UsbGuard.WhiteEntry e = list.SelectedItems[0].Tag as UsbGuard.WhiteEntry;
        if (e == null) { detail.Text = ""; return; }
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("设备描述：" + (string.IsNullOrEmpty(e.description) ? "未识别条目" : e.description));
        sb.AppendLine("分类：" + UsbGuard.CategoryName(e.category));
        sb.AppendLine("信任时间：" + (string.IsNullOrEmpty(e.trustedAt) ? "—" : e.trustedAt));
        sb.AppendLine("实例 ID（共 " + e.deviceIds.Count + " 个）：");
        foreach (string id in e.deviceIds) sb.AppendLine("  " + id);
        detail.Text = sb.ToString();
    }

    void RemoveSelected() {
        if (saving || list.SelectedItems.Count == 0) return;
        UsbGuard.WhiteEntry e = list.SelectedItems[0].Tag as UsbGuard.WhiteEntry;
        if (e == null || e.deviceIds.Count == 0) return;
        string desc = string.IsNullOrEmpty(e.description) ? "未识别条目" : e.description;
        DialogResult dr = MessageBox.Show(this,
            "确定删除该设备吗？\n\n设备：" + desc + "\n接口数：" + e.deviceIds.Count + "\n\n删除后下次接入将按陌生设备报警并锁屏。",
            "USB 哨兵：删除确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (dr != DialogResult.Yes) return;
        saving = true;
        btnRemove.Enabled = false;
        btnRefresh.Enabled = false;
        btnRemove.Text = "正在保存…";
        string firstId = e.deviceIds[0];
        try {
            UsbGuard.RemoveEntryAndSave(firstId);
        } finally {
            saving = false;
            btnRemove.Text = "删除选中";
            btnRefresh.Enabled = true;
            Reload();
        }
    }
}
using System;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

internal sealed class DeviceManagerForm : Form {
    private static DeviceManagerForm current;
    internal static DeviceManagerForm Current { get { return current; } }

    private readonly Func<WhitelistDocument> documentProvider;
    private readonly Action<WhitelistEntry> removeHandler;
    private readonly Action retryHandler;
    private ListView list;
    private TextBox detail;
    private Button removeButton;
    private Button refreshButton;
    private Label emptyLabel;
    private Label resultLabel;
    private bool saving;

    internal static void ShowManager(IWin32Window owner, Func<WhitelistDocument> provider, Action<WhitelistEntry> remove, Action retry) {
        if (current != null && !current.IsDisposed) {
            current.Show(); current.Activate(); return;
        }
        current = new DeviceManagerForm(provider, remove, retry);
        if (owner != null) current.Show(owner); else current.Show();
    }

    private DeviceManagerForm(Func<WhitelistDocument> provider, Action<WhitelistEntry> remove, Action retry) {
        documentProvider = provider;
        removeHandler = remove;
        retryHandler = retry;
        Text = "USB 哨兵：设备管理";
        StartPosition = FormStartPosition.CenterScreen;
        Width = 1000; Height = 590;
        MinimumSize = new Size(800, 480);
        BackColor = Color.White;
        Font = new Font("Microsoft YaHei UI", 9f);

        list = new ListView();
        list.View = View.Details;
        list.FullRowSelect = true;
        list.MultiSelect = false;
        list.GridLines = true;
        list.SetBounds(12, 12, 750, 390);
        list.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        list.Columns.Add("设备描述", 210);
        list.Columns.Add("分类", 120);
        list.Columns.Add("VID/PID", 135);
        list.Columns.Add("接口数量", 70);
        list.Columns.Add("信任时间", 155);
        list.SelectedIndexChanged += delegate { ShowDetail(); };
        Controls.Add(list);

        detail = new TextBox();
        detail.Multiline = true; detail.ReadOnly = true; detail.ScrollBars = ScrollBars.Vertical;
        detail.SetBounds(12, 414, 750, 105);
        detail.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        detail.Font = new Font("Consolas", 9f);
        Controls.Add(detail);

        removeButton = SideButton("删除选中", 778, 12);
        removeButton.Click += RemoveSelected;
        refreshButton = SideButton("刷新/重试", 778, 56);
        refreshButton.Click += delegate { retryHandler(); };
        Button close = SideButton("关闭", 778, 100);
        close.Click += delegate { Close(); };
        Button guide = SideButton("安全添加设备指引", 778, 144);
        guide.Click += delegate {
            MessageBox.Show(this, "运行根目录「2-添加新设备.bat」，核对每个完整实例并逐项确认；也可在陌生设备报警中只信任当前目标。程序启动、升级和打开本窗口不会自动信任设备。", "USB 哨兵", MessageBoxButtons.OK, MessageBoxIcon.Information);
        };

        emptyLabel = new Label();
        emptyLabel.Text = "尚无信任设备。请通过单目标报警或「2-添加新设备.bat」明确确认后添加。";
        emptyLabel.TextAlign = ContentAlignment.MiddleCenter;
        emptyLabel.ForeColor = Color.Gray;
        emptyLabel.SetBounds(12, 12, 750, 390);
        emptyLabel.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        Controls.Add(emptyLabel);
        emptyLabel.BringToFront();

        resultLabel = new Label();
        resultLabel.SetBounds(778, 195, 195, 250);
        resultLabel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        Controls.Add(resultLabel);
        FormClosed += delegate { if (current == this) current = null; };
        Reload();
    }

    private Button SideButton(string text, int x, int y) {
        Button button = new Button();
        button.Text = text;
        button.SetBounds(x, y, 195, 34);
        button.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        Controls.Add(button);
        return button;
    }

    internal void Reload() {
        WhitelistDocument document = documentProvider == null ? null : documentProvider();
        if (document == null) {
            resultLabel.Text = "名单读取失败；保留当前视图，请点“刷新/重试”。";
            return;
        }
        string selectedId = SelectedId();
        list.BeginUpdate();
        list.Items.Clear();
        foreach (WhitelistEntry entry in document.Entries) {
            string first = entry.deviceIds.Count > 0 ? entry.deviceIds[0] : "";
            ListViewItem item = new ListViewItem(entry.Kind == WhitelistEntryKind.Unrecognized ? "未识别条目" : (string.IsNullOrEmpty(entry.description) ? "未知设备" : entry.description));
            item.SubItems.Add(entry.CategoryText());
            item.SubItems.Add(DeviceInventory.VidPidOf(first));
            item.SubItems.Add(entry.deviceIds.Count.ToString());
            item.SubItems.Add(string.IsNullOrEmpty(entry.trustedAt) ? "未知时间" : entry.trustedAt);
            item.Tag = entry;
            list.Items.Add(item);
            if (!string.IsNullOrEmpty(selectedId) && Contains(entry, selectedId)) item.Selected = true;
        }
        list.EndUpdate();
        bool has = document.Entries.Count > 0;
        emptyLabel.Visible = !has;
        removeButton.Enabled = has && !saving;
        if (list.SelectedItems.Count == 0) detail.Text = "";
    }

    private static bool Contains(WhitelistEntry entry, string id) {
        foreach (string value in entry.deviceIds) if (DeviceIdentity.Equals(value, id)) return true;
        return false;
    }

    private string SelectedId() {
        if (list.SelectedItems.Count == 0) return "";
        WhitelistEntry entry = list.SelectedItems[0].Tag as WhitelistEntry;
        return entry != null && entry.deviceIds.Count > 0 ? entry.deviceIds[0] : "";
    }

    private void ShowDetail() {
        if (list.SelectedItems.Count == 0) { detail.Text = ""; return; }
        WhitelistEntry entry = list.SelectedItems[0].Tag as WhitelistEntry;
        if (entry == null) { detail.Text = ""; return; }
        StringBuilder text = new StringBuilder();
        text.AppendLine("设备描述：" + (entry.Kind == WhitelistEntryKind.Unrecognized ? "未识别条目" : entry.description));
        text.AppendLine("分类：" + entry.CategoryText());
        text.AppendLine("信任时间：" + (string.IsNullOrEmpty(entry.trustedAt) ? "未知时间" : entry.trustedAt));
        text.AppendLine("完整实例 ID（共 " + entry.deviceIds.Count + " 个）：");
        foreach (string id in entry.deviceIds) text.AppendLine("  " + id);
        if (entry.Kind == WhitelistEntryKind.Unrecognized) text.AppendLine("该历史条目只读保留，不参与设备放行。");
        detail.Text = text.ToString();
    }

    private void RemoveSelected(object sender, EventArgs args) {
        if (saving || list.SelectedItems.Count == 0) return;
        WhitelistEntry entry = list.SelectedItems[0].Tag as WhitelistEntry;
        if (entry == null || entry.deviceIds.Count == 0) return;
        string description = entry.Kind == WhitelistEntryKind.Unrecognized ? "未识别历史条目" : entry.description;
        if (MessageBox.Show(this, "确定删除该条目吗？\n\n设备：" + description + "\n接口数：" + entry.deviceIds.Count + "\n\n仅在保存成功并重读后更新列表；失败、取消或冲突会保留当前选中项。",
            "USB 哨兵：删除确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        saving = true;
        removeButton.Enabled = false;
        removeButton.Text = "正在保存…";
        resultLabel.Text = "正在后台保存，其他安全操作仍可使用。";
        removeHandler(entry);
    }

    internal void ShowOperationResult(OperationResult result, bool reload) {
        saving = false;
        removeButton.Text = "删除选中";
        removeButton.Enabled = true;
        resultLabel.Text = GuardText.ResultName(result.Kind) + "：" + result.Summary();
        if (reload) Reload();
    }
}
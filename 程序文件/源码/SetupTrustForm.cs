using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

internal sealed class SetupTrustForm : Form {
    private readonly DeviceInventory inventory = new DeviceInventory();
    private readonly MaintenanceService maintenance;
    private readonly SynchronizationContext ui;
    private DeviceSnapshot snapshot;
    private WhitelistDocument whitelist;
    private ListView devices;
    private Label status;
    private Button trustButton;
    private Button retryButton;

    internal static int Run() {
        Application.EnableVisualStyles();
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        using (SetupTrustForm form = new SetupTrustForm()) {
            Application.Run(form);
            return form.Saved ? 0 : 5;
        }
    }

    internal bool Saved { get; private set; }

    private SetupTrustForm() {
        ui = SynchronizationContext.Current;
        maintenance = new MaintenanceService(AppDomain.CurrentDomain.BaseDirectory, inventory, ui);
        Text = "USB 哨兵：显式信任一台设备";
        Width = 940; Height = 520;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9f);
        Label guide = new Label();
        guide.Text = "仅在核对后选择一台物理设备。不会预选、不会批量信任；同型号不同实例分别显示。保存成功后窗口关闭，可再次运行以添加下一台。";
        guide.SetBounds(12, 12, 890, 48);
        Controls.Add(guide);
        devices = new ListView();
        devices.View = View.Details;
        devices.FullRowSelect = true;
        devices.MultiSelect = false;
        devices.SetBounds(12, 66, 890, 330);
        devices.Columns.Add("设备", 220);
        devices.Columns.Add("分类", 120);
        devices.Columns.Add("物理组证据", 230);
        devices.Columns.Add("完整实例范围", 300);
        devices.SelectedIndexChanged += delegate { trustButton.Enabled = devices.SelectedItems.Count == 1 && snapshot != null; };
        Controls.Add(devices);
        trustButton = new Button();
        trustButton.Text = "只信任选中的这一台";
        trustButton.SetBounds(12, 410, 200, 36);
        trustButton.Enabled = false;
        trustButton.Click += TrustSelected;
        Controls.Add(trustButton);
        retryButton = new Button();
        retryButton.Text = "重新枚举";
        retryButton.SetBounds(224, 410, 120, 36);
        retryButton.Click += delegate { LoadDevices(); };
        Controls.Add(retryButton);
        Button cancel = new Button();
        cancel.Text = "取消（不改名单）";
        cancel.SetBounds(356, 410, 160, 36);
        cancel.Click += delegate { Close(); };
        Controls.Add(cancel);
        status = new Label();
        status.SetBounds(12, 456, 890, 40);
        Controls.Add(status);
        Shown += delegate { LoadDevices(); };
    }

    private void LoadDevices() {
        trustButton.Enabled = false;
        retryButton.Enabled = false;
        status.Text = "正在后台枚举完整实例，界面可取消；不会写入名单…";
        ThreadPool.QueueUserWorkItem(delegate {
            DeviceSnapshot captured = inventory.CaptureSnapshot();
            WhitelistLoadResult loaded = new WhitelistStore(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "usb-whitelist.json")).Load();
            ui.Post(delegate {
                retryButton.Enabled = true;
                if (!captured.Success || !loaded.Healthy) {
                    status.Text = !captured.Success ? captured.Error : loaded.Message;
                    return;
                }
                snapshot = captured;
                whitelist = loaded.Document;
                devices.Items.Clear();
                foreach (PhysicalDevice group in captured.Devices) {
                    ListViewItem item = new ListViewItem(group.Description);
                    item.SubItems.Add(GuardText.CategoryName(group.Category));
                    item.SubItems.Add(group.GroupKey);
                    item.SubItems.Add(string.Join("；", group.InstanceIds().ToArray()));
                    item.Tag = group;
                    devices.Items.Add(item);
                }
                status.Text = captured.Devices.Count == 0 ? "未发现可选择的 USB/HID 完整实例。" : "请选择一台；当前未作任何更改。";
            }, null);
        });
    }

    private void TrustSelected(object sender, EventArgs args) {
        if (devices.SelectedItems.Count != 1 || snapshot == null || whitelist == null) return;
        PhysicalDevice group = devices.SelectedItems[0].Tag as PhysicalDevice;
        if (group == null) return;
        trustButton.Enabled = false;
        status.Text = "正在后台复核并保存；取消授权或范围变化不会改名单…";
        MutationRequest request = new MutationRequest();
        request.Operation = MutationKind.Trust;
        request.ExpectedRevision = whitelist.Revision;
        request.Trust = new TrustRequest {
            GroupKey = group.GroupKey,
            SnapshotGeneration = group.SnapshotGeneration,
            ExpectedInstanceIds = group.InstanceIds(),
            Description = group.Description,
            Category = WhitelistEntry.CategoryToString(group.Category)
        };
        maintenance.ApplyAsync(request, delegate(MutationResult result) {
            status.Text = OperationFeedback.MutationMessage(result, "名单");
            if (result.Kind == OperationResultKind.Success || result.Kind == OperationResultKind.Unchanged) {
                Saved = true;
                MessageBox.Show(this, status.Text, "USB 哨兵", MessageBoxButtons.OK, MessageBoxIcon.Information);
                Close();
            } else trustButton.Enabled = true;
        });
    }
}
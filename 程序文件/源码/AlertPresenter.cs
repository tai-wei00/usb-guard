using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

internal sealed class AlertPresenter {
    private readonly Queue<PhysicalDevice> queue = new Queue<PhysicalDevice>();
    private readonly Action<TrustRequest> onTrust;
    private readonly Action<ShellCommand> onCommand;
    private readonly Action requestLock;
    private readonly bool testMode;
    private readonly Action testFinished;
    private Form form;
    private Label content;
    private Label countdownLabel;
    private Label resultLabel;
    private Button trustButton;
    private System.Windows.Forms.Timer timer;
    private PhysicalDevice current;
    private int secondsLeft;
    private bool closing;

    internal AlertPresenter(Action<TrustRequest> trustHandler, Action<ShellCommand> commandHandler, Action lockHandler, bool promptTest = false, Action onTestFinished = null) {
        onTrust = trustHandler;
        onCommand = commandHandler;
        requestLock = lockHandler;
        testMode = promptTest;
        testFinished = onTestFinished;
    }

    internal bool HasActiveAlert { get { return current != null; } }

    internal void Enqueue(IEnumerable<PhysicalDevice> devices) {
        if (devices == null) return;
        List<PhysicalDevice> latest = new List<PhysicalDevice>();
        foreach (PhysicalDevice device in devices) if (device != null) latest.Add(device);
        if (current != null) {
            PhysicalDevice match = null;
            foreach (PhysicalDevice device in latest) if (device.GroupKey == current.GroupKey) { match = device; break; }
            if (match == null) { current = null; timer.Stop(); }
            else current = match;
        }
        queue.Clear();
        HashSet<string> added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PhysicalDevice device in latest) {
            if (current != null && current.GroupKey == device.GroupKey) continue;
            if (added.Add(device.GroupKey)) queue.Enqueue(device);
        }
        if (current == null) ShowNext();
        else if (form != null && !form.Visible) ShowCurrent();
    }

    private void EnsureForm() {
        if (form != null) return;
        form = new Form();
        form.Text = "USB 哨兵：检测到陌生设备";
        form.FormBorderStyle = FormBorderStyle.FixedDialog;
        form.MaximizeBox = false; form.MinimizeBox = false;
        form.StartPosition = FormStartPosition.CenterScreen;
        form.TopMost = true;
        form.Width = 650; form.Height = 405;
        form.BackColor = Color.White;
        content = new Label();
        content.SetBounds(16, 14, 600, 170);
        content.Font = new Font("Microsoft YaHei UI", 10f);
        form.Controls.Add(content);
        trustButton = Button("信任此设备", 16, 190, 130);
        trustButton.Click += TrustCurrent;
        form.Controls.Add(trustButton);
        Button pause = Button("暂停 30 分钟", 158, 190, 130);
        pause.Click += delegate { if (testMode) FinishTest(); else onCommand(new ShellCommand(ShellCommandKind.Pause30)); };
        form.Controls.Add(pause);
        Button manager = Button("管理白名单", 300, 190, 120);
        manager.Click += delegate { if (!testMode) onCommand(new ShellCommand(ShellCommandKind.DeviceManager)); };
        form.Controls.Add(manager);
        Button lockNow = Button("立即锁定", 432, 190, 110);
        lockNow.Click += delegate { if (testMode) FinishTest(); else LockAndHide(); };
        form.Controls.Add(lockNow);
        countdownLabel = new Label();
        countdownLabel.SetBounds(16, 240, 600, 35);
        countdownLabel.ForeColor = Color.Firebrick;
        form.Controls.Add(countdownLabel);
        resultLabel = new Label();
        resultLabel.SetBounds(16, 280, 600, 55);
        form.Controls.Add(resultLabel);
        timer = new System.Windows.Forms.Timer();
        timer.Interval = 1000;
        timer.Tick += Tick;
        form.FormClosing += delegate(object sender, FormClosingEventArgs args) {
            if (!closing && !testMode && args.CloseReason == CloseReason.UserClosing) {
                args.Cancel = true;
                LockAndHide();
            }
        };
    }

    private Button Button(string text, int x, int y, int width) {
        Button button = new Button();
        button.Text = text;
        button.SetBounds(x, y, width, 32);
        return button;
    }

    private void ShowNext() {
        if (queue.Count == 0) { current = null; if (form != null) form.Hide(); return; }
        current = queue.Dequeue();
        EnsureForm();
        StringBuilder text = new StringBuilder();
        if (testMode) text.Append("【测试模式，不执行信任或锁屏】\n\n");
        text.Append("⚠ 检测到陌生").Append(GuardText.CategoryName(current.Category)).Append("！\n\n");
        text.Append("设备：").Append(current.Description).Append("\n接口范围（仅这些完整实例会被信任）：\n");
        foreach (string id in current.InstanceIds()) text.Append("  ").Append(id).Append("\n");
        text.Append(current.Category == DeviceCategory.Storage ? "请勿拷贝/打开其中文件。" :
            current.Category == DeviceCategory.Hid ? "警惕 BadUSB 输入注入（伪装键盘自动执行命令）。" : "建议立即拔出，确认来源后再信任。");
        content.Text = text.ToString();
        resultLabel.Text = current.IdentityComplete ? "保存成功后才解除此目标报警；其余陌生设备继续排队提示。" : "归组证据不完整，仅按此独立实例处理。";
        trustButton.Enabled = !testMode;
        ShowCurrent();
    }

    private void ShowCurrent() {
        if (current == null) return;
        EnsureForm();
        secondsLeft = testMode ? 6 : 25;
        countdownLabel.Text = testMode ? secondsLeft + " 秒后自动关闭测试" : secondsLeft + " 秒后自动锁屏；关闭窗口也会锁屏";
        form.Show();
        form.Activate();
        timer.Start();
    }

    private void TrustCurrent(object sender, EventArgs args) {
        if (testMode || current == null) { FinishTest(); return; }
        trustButton.Enabled = false;
        resultLabel.Text = "正在复核目标范围并保存；取消或失败不会解除报警…";
        TrustRequest request = new TrustRequest();
        request.GroupKey = current.GroupKey;
        request.SnapshotGeneration = current.SnapshotGeneration;
        request.ExpectedInstanceIds = current.InstanceIds();
        request.Description = current.Description;
        request.Category = WhitelistEntry.CategoryToString(current.Category);
        onTrust(request);
    }

    internal void TrustCompleted(MutationResult result) {
        if (current == null) return;
        if (result.Kind == OperationResultKind.Success || result.Kind == OperationResultKind.Unchanged) {
            resultLabel.Text = "目标已保存并生效";
            ShowNext();
            return;
        }
        trustButton.Enabled = true;
        resultLabel.Text = OperationFeedback.MutationMessage(result, "名单") + "；当前报警保持";
    }

    private void Tick(object sender, EventArgs args) {
        secondsLeft--;
        countdownLabel.Text = testMode ? secondsLeft + " 秒后自动关闭测试" : secondsLeft + " 秒后自动锁屏；关闭窗口也会锁屏";
        if (secondsLeft > 0) return;
        timer.Stop();
        if (testMode) FinishTest(); else LockAndHide();
    }

    private void LockAndHide() {
        timer.Stop();
        if (form != null) form.Hide();
        if (requestLock != null) requestLock();
    }

    private void FinishTest() {
        timer.Stop();
        if (form != null) form.Hide();
        if (testFinished != null) testFinished();
    }

    internal void DismissAll() {
        queue.Clear();
        current = null;
        if (timer != null) {
            timer.Stop();
            timer.Dispose();
            timer = null;
        }
        if (form != null) {
            closing = true;
            try {
                form.Close();
                form.Dispose();
            } finally {
                form = null;
                closing = false;
            }
        }
        content = null;
        countdownLabel = null;
        resultLabel = null;
        trustButton = null;
    }
}
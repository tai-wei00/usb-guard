using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

internal sealed class SingleInstanceActivator : IDisposable {
    private readonly string directory;
    private Mutex mutex;
    private EventWaitHandle activationEvent;
    private Thread listener;
    private SynchronizationContext uiContext;
    private Action showMain;
    private bool ownsMutex;
    private volatile bool disposed;
    internal string ActivationError = "";

    internal SingleInstanceActivator(string baseDirectory) { directory = baseDirectory; }

    private string Prefix() {
        using (SHA256 sha = SHA256.Create()) {
            string hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(directory.ToUpperInvariant()))).Replace("-", "");
            return "Local\\UsbGuard_" + hash.Substring(0, 20);
        }
    }

    internal bool ActivateOrTakeOwnership(bool requestShowMain, SynchronizationContext context, Action activate) {
        uiContext = context;
        showMain = activate;
        string prefix = Prefix();
        bool created;
        mutex = new Mutex(true, prefix + "_Mutex", out created);
        if (!created) {
            mutex.Dispose();
            mutex = null;
            if (requestShowMain) SignalExisting(prefix + "_ShowMain");
            return false;
        }
        ownsMutex = true;
        activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, prefix + "_ShowMain");
        listener = new Thread(Listen);
        listener.IsBackground = true;
        listener.Name = "UsbGuardSingleInstance";
        listener.Start();
        return true;
    }

    private void SignalExisting(string name) {
        DateTime deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline) {
            try {
                using (EventWaitHandle existing = EventWaitHandle.OpenExisting(name)) {
                    if (existing.Set()) return;
                }
            } catch { Thread.Sleep(50); }
        }
        ActivationError = "已有实例未响应主面板激活请求";
    }

    private void Listen() {
        while (!disposed) {
            if (!activationEvent.WaitOne(1000)) continue;
            if (disposed) break;
            if (uiContext != null && showMain != null) uiContext.Post(delegate { showMain(); }, null);
        }
    }

    public void Dispose() {
        if (disposed) return;
        disposed = true;
        try { if (activationEvent != null) activationEvent.Set(); } catch { }
        if (listener != null) listener.Join(2000);
        if (activationEvent != null) activationEvent.Dispose();
        if (ownsMutex && mutex != null) { try { mutex.ReleaseMutex(); } catch { } }
        if (mutex != null) mutex.Dispose();
    }
}
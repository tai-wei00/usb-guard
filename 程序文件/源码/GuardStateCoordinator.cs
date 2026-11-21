using System;

internal sealed class GuardStateChangedEventArgs : EventArgs {
    public GuardStateSnapshot Previous;
    public GuardStateSnapshot Current;
    internal GuardStateChangedEventArgs(GuardStateSnapshot previous, GuardStateSnapshot current) {
        Previous = previous;
        Current = current;
    }
}

internal sealed class GuardStateCoordinator {
    private readonly object sync = new object();
    private readonly GuardStateStore store;
    private GuardStateSnapshot current = new GuardStateSnapshot();
    private RuntimeIntent intent;
    private bool known;
    private bool stopped;
    private bool whitelistHealthy = true;
    private string whitelistReason = "";
    private OperationResultKind lastResult = OperationResultKind.Unchanged;
    private string lastDetail = "";

    internal event EventHandler<GuardStateChangedEventArgs> StateChanged;
    internal event EventHandler RefreshRequested;
    internal GuardStateSnapshot Current { get { lock (sync) return current.Clone(); } }

    internal GuardStateCoordinator(GuardStateStore stateStore) { store = stateStore; }

    internal OperationResult Initialize(bool isStopped, bool isWhitelistHealthy, string healthReason) {
        lock (sync) {
            stopped = isStopped;
            whitelistHealthy = isWhitelistHealthy;
            whitelistReason = healthReason ?? "";
            known = true;
        }
        RuntimeIntentLoadResult loaded = store.Load();
        if (loaded.Status == RuntimeIntentLoadStatus.Loaded) {
            intent = loaded.Intent;
            lastDetail = loaded.Message;
        } else {
            intent = null;
            if (loaded.Status == RuntimeIntentLoadStatus.Corrupt || loaded.Status == RuntimeIntentLoadStatus.IoError)
                lastDetail = loaded.Message;
        }
        Publish(StateChangeSource.Startup, OperationResultKind.Success, string.IsNullOrEmpty(lastDetail) ? "状态已恢复" : lastDetail);
        return OperationResult.Create(OperationResultKind.Success, "运行状态已加载");
    }

    internal void SetUnknown(string reason) {
        lock (sync) { known = false; }
        Publish(StateChangeSource.Degraded, OperationResultKind.Failed, string.IsNullOrEmpty(reason) ? "当前状态无法确认" : reason);
    }

    internal void SetWhitelistHealth(bool healthy, string reason) {
        lock (sync) {
            whitelistHealthy = healthy;
            whitelistReason = reason ?? "";
            known = true;
        }
        Publish(StateChangeSource.Degraded, healthy ? OperationResultKind.Success : OperationResultKind.Failed,
            healthy ? "名单已恢复，立即重新检查设备" : whitelistReason);
        if (healthy) RaiseRefresh();
    }

    internal OperationResult RequestPause(TimeSpan duration, StateChangeSource source) {
        if (duration != TimeSpan.FromMinutes(30) && duration != TimeSpan.FromHours(2))
            return OperationResult.Create(OperationResultKind.Failed, "暂停时长只允许 30 分钟或 2 小时");
        return SetIntent(RuntimeIntentKind.Paused, duration, source);
    }

    internal OperationResult RequestRepair(TimeSpan duration, StateChangeSource source) {
        if (duration != TimeSpan.FromHours(2)) return OperationResult.Create(OperationResultKind.Failed, "维修模式最长 2 小时");
        return SetIntent(RuntimeIntentKind.Repairing, duration, source);
    }

    private OperationResult SetIntent(RuntimeIntentKind kind, TimeSpan duration, StateChangeSource source) {
        RuntimeIntent candidate = new RuntimeIntent();
        candidate.kind = kind == RuntimeIntentKind.Repairing ? "repair" : "pause";
        candidate.until = DateTimeOffset.Now.Add(duration).ToString("o");
        candidate.source = source.ToString();
        AtomicWriteResult saved = store.Save(candidate);
        if (saved.Kind != OperationResultKind.Success && saved.Kind != OperationResultKind.Unchanged) {
            Publish(source, OperationResultKind.Failed, "运行状态保存失败，仍保持原防护状态：" + saved.Message);
            return OperationResult.Create(OperationResultKind.Failed, "暂停/维修状态未持久化，状态未变更");
        }
        lock (sync) { intent = candidate; known = true; }
        Publish(source, OperationResultKind.Success, (kind == RuntimeIntentKind.Repairing ? "维修模式" : "临时暂停") + "已保存，截止 " + candidate.until);
        return OperationResult.Create(OperationResultKind.Success, "状态已保存并生效");
    }

    internal OperationResult ResumeProtection(StateChangeSource source) {
        string revision = "";
        lock (sync) { if (intent != null) revision = intent.revision ?? ""; }
        AtomicWriteResult cleared = store.Clear(revision);
        if (cleared.Kind == OperationResultKind.Conflict) {
            Publish(source, OperationResultKind.Conflict, "运行状态已变化，请重试");
            return OperationResult.Create(OperationResultKind.Conflict, cleared.Message);
        }
        if (cleared.Kind == OperationResultKind.Failed) {
            Publish(source, OperationResultKind.Failed, "无法清除暂停记录，状态未变更：" + cleared.Message);
            return OperationResult.Create(OperationResultKind.Failed, "状态未变更");
        }
        lock (sync) { intent = null; known = true; }
        Publish(source, OperationResultKind.Success, "已恢复完整防护，立即重新检查当前设备");
        RaiseRefresh();
        return OperationResult.Create(OperationResultKind.Success, "已恢复完整防护");
    }

    internal void ApplyProtectionResult(ProtectionTransitionResult transition) {
        bool nowStopped = transition.StopFlagPresent;
        lock (sync) {
            stopped = nowStopped;
            known = transition.ActualMode != GuardMode.Unknown || nowStopped || transition.Result == OperationResultKind.Success;
            lastResult = transition.Result;
            lastDetail = transition.Message;
        }
        Publish(transition.Action == ProtectionAction.Stop ? StateChangeSource.MainPanel : StateChangeSource.Watchdog,
            transition.Result, transition.Message);
        if (!nowStopped && transition.Result == OperationResultKind.Success) RaiseRefresh();
    }

    internal bool Tick(DateTimeOffset now) {
        bool expired = false;
        lock (sync) {
            if (intent != null) {
                DateTimeOffset until;
                if (!DateTimeOffset.TryParse(intent.until, out until) || until <= now) expired = true;
            }
        }
        if (expired) {
            ResumeProtection(StateChangeSource.Expiration);
            return true;
        }
        GuardStateSnapshot before = Current;
        GuardStateSnapshot computed = Compute();
        if (before.Mode != computed.Mode || before.Detail != computed.Detail) {
            Publish(StateChangeSource.Expiration, OperationResultKind.Unchanged, computed.Detail);
        }
        return false;
    }

    internal void SetUnknownDevices(bool hasUnknown) {
        GuardStateChangedEventArgs args = null;
        lock (sync) {
            if (current.HasUnknownDevices == hasUnknown) return;
            GuardStateSnapshot previous = current.Clone();
            current.HasUnknownDevices = hasUnknown;
            current.Revision++;
            args = new GuardStateChangedEventArgs(previous, current.Clone());
        }
        EventHandler<GuardStateChangedEventArgs> handler = StateChanged;
        if (handler != null) handler(this, args);
    }

    private GuardStateSnapshot Compute() {
        GuardStateSnapshot next = new GuardStateSnapshot();
        next.Revision = current.Revision;
        next.Result = lastResult;
        next.Source = current.Source;
        next.HasUnknownDevices = current.HasUnknownDevices;
        if (!known) {
            next.Mode = GuardMode.Unknown;
            next.Detail = string.IsNullOrEmpty(lastDetail) ? "当前状态无法确认" : lastDetail;
            return next;
        }
        if (stopped) {
            next.Mode = GuardMode.Stopped;
            next.Detail = string.IsNullOrEmpty(lastDetail) ? "监控、扫描、报警锁屏和看护已停止" : lastDetail;
            next.Result = lastResult;
            return next;
        }
        if (!whitelistHealthy) {
            next.Mode = GuardMode.WhitelistError;
            next.Detail = string.IsNullOrEmpty(whitelistReason) ? "名单不可读，自动锁屏已禁用" : whitelistReason;
            return next;
        }
        if (intent != null) {
            DateTimeOffset until;
            if (DateTimeOffset.TryParse(intent.until, out until) && until > DateTimeOffset.Now) {
                next.Mode = intent.Kind() == RuntimeIntentKind.Repairing ? GuardMode.Repairing : GuardMode.Paused;
                next.Deadline = until;
                next.Detail = next.Mode == GuardMode.Repairing ? "只记录设备事件，不报警、不锁屏" : "临时暂停，只记录、不报警、不锁屏";
                return next;
            }
        }
        next.Mode = GuardMode.Protecting;
        next.Detail = string.IsNullOrEmpty(lastDetail) ? "完整监控与阻断已启用" : lastDetail;
        return next;
    }

    private void Publish(StateChangeSource source, OperationResultKind result, string detail) {
        GuardStateChangedEventArgs args = null;
        lock (sync) {
            lastResult = result;
            lastDetail = detail ?? "";
            current.Source = source;
            GuardStateSnapshot next = Compute();
            next.Source = source;
            next.Result = result;
            next.Detail = detail ?? next.Detail;
            next.Revision = current.Revision + 1;
            bool changed = next.Mode != current.Mode || next.Deadline != current.Deadline || next.Source != current.Source
                || next.Result != current.Result || next.Detail != current.Detail || next.HasUnknownDevices != current.HasUnknownDevices;
            if (!changed) return;
            GuardStateSnapshot previous = current.Clone();
            current = next;
            args = new GuardStateChangedEventArgs(previous, current.Clone());
        }
        EventHandler<GuardStateChangedEventArgs> handler = StateChanged;
        if (handler != null) handler(this, args);
    }

    private void RaiseRefresh() {
        EventHandler handler = RefreshRequested;
        if (handler != null) handler(this, EventArgs.Empty);
    }
}
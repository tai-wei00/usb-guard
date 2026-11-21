using System;

internal static class OperationFeedback {
    internal static string MutationMessage(MutationResult result, string subject) {
        if (result == null) return subject + "操作失败，未变更";
        switch (result.Kind) {
            case OperationResultKind.Success: return subject + "已保存并生效";
            case OperationResultKind.Unchanged: return subject + "未变化";
            case OperationResultKind.Cancelled: return "已取消授权，" + subject + "未变更";
            case OperationResultKind.Conflict: return subject + "已变化，请刷新后重试；本次未覆盖较新内容";
            case OperationResultKind.Partial: return subject + "部分完成：" + result.Message;
            default: return subject + "保存失败，" + subject + "未变更：" + result.Message;
        }
    }

    internal static string ProtectionMessage(ProtectionTransitionResult result) {
        if (result == null) return "状态切换失败，实际状态未知";
        if (result.Result == OperationResultKind.Cancelled) return "已取消授权，状态未变更";
        if (result.Result == OperationResultKind.Partial) return "状态切换未完成：" + result.Message;
        if (result.Result == OperationResultKind.Failed) return "状态切换失败：" + result.Message;
        return result.Action == ProtectionAction.Stop ? "防护已彻底停止，看护已停用" : "防护与看护已重新启用";
    }

    internal static DiagnosticEvent FromResult(string source, string eventType, OperationResult result) {
        DiagnosticEvent item = new DiagnosticEvent();
        item.Source = source;
        item.EventType = eventType;
        item.Result = result == null ? OperationResultKind.Failed : result.Kind;
        item.Message = result == null ? "无操作结果" : result.Summary();
        item.Level = result != null && result.Kind == OperationResultKind.Success ? "INFO" : "WARN";
        return item;
    }
}
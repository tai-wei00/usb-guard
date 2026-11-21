using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

internal static class AtomicJsonStore {
    internal static string RevisionOf(string content) {
        using (SHA256 sha = SHA256.Create()) {
            byte[] bytes = Encoding.UTF8.GetBytes(content ?? "");
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
        }
    }

    private static string MutexName(string path) {
        string directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
        return "Local\\UsbGuardJson_" + RevisionOf(directory.ToUpperInvariant()).Substring(0, 24);
    }

    internal static AtomicWriteResult WriteText(string path, string content, string expectedRevision, bool keepBackup) {
        AtomicWriteResult result = new AtomicWriteResult();
        Mutex mutex = null;
        bool owned = false;
        string temporary = null;
        string backup = keepBackup ? path + ".last-valid.bak" : null;
        bool existed = false;
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            mutex = new Mutex(false, MutexName(path));
            try { owned = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { owned = true; }
            if (!owned) { result.Kind = OperationResultKind.Failed; result.Message = "写互斥等待超时"; return result; }

            existed = File.Exists(path);
            string current = existed ? File.ReadAllText(path, Encoding.UTF8) : "";
            string actualRevision = RevisionOf(current);
            if (expectedRevision != null && actualRevision != expectedRevision) {
                result.Kind = OperationResultKind.Conflict;
                result.Message = "文件已在保存前发生变化，请刷新后重试";
                result.Revision = actualRevision;
                return result;
            }
            if (current == content) {
                result.Kind = OperationResultKind.Unchanged;
                result.Message = "内容未变化";
                result.Revision = actualRevision;
                return result;
            }

            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
            string readBack = File.ReadAllText(temporary, Encoding.UTF8);
            if (readBack != content) throw new IOException("临时文件回读校验不一致");

            if (existed) File.Replace(temporary, path, backup, true);
            else File.Move(temporary, path);
            temporary = null;

            string committed = File.ReadAllText(path, Encoding.UTF8);
            if (committed != content) throw new IOException("替换后重读校验不一致");
            result.Kind = OperationResultKind.Success;
            result.Revision = RevisionOf(committed);
            result.Message = "保存成功";
            return result;
        } catch (UnauthorizedAccessException ex) {
            result.Kind = OperationResultKind.Failed;
            result.RequiresElevation = true;
            result.Message = ex.Message;
            RestoreAfterReadFailure(path, backup, existed);
            return result;
        } catch (Exception ex) {
            result.Kind = OperationResultKind.Failed;
            result.Message = ex.Message;
            RestoreAfterReadFailure(path, backup, existed);
            return result;
        } finally {
            if (temporary != null) { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
            if (owned && mutex != null) { try { mutex.ReleaseMutex(); } catch { } }
            if (mutex != null) mutex.Dispose();
        }
    }

    private static void RestoreAfterReadFailure(string path, string backup, bool existed) {
        try {
            if (existed && backup != null && File.Exists(backup) && File.Exists(path)) {
                string expected = File.ReadAllText(backup, Encoding.UTF8);
                string actual = File.ReadAllText(path, Encoding.UTF8);
                if (expected != actual) File.Copy(backup, path, true);
            }
        } catch { }
    }
}
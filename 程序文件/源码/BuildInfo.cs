using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

internal static class BuildInfoProvider {
    internal const string ProductVersion = "2.1.0";
    internal const string BuildDate = "2026-11-20";

    internal static BuildInfo GetBuildInfo() {
        BuildInfo info = new BuildInfo();
        info.ProductVersion = ProductVersion;
        info.BuildDate = BuildDate;
        try {
            Assembly assembly = Assembly.GetExecutingAssembly();
            FileVersionInfo version = FileVersionInfo.GetVersionInfo(assembly.Location);
            if (version.FileVersion != "2.1.0.0" || version.ProductVersion != ProductVersion) info.Consistency = "程序集版本不一致";
            else info.Consistency = CheckManifest(assembly.Location, info);
        } catch (Exception ex) {
            info.Consistency = "版本核验失败：" + ex.Message;
        }
        return info;
    }

    private static string CheckManifest(string exePath, BuildInfo info) {
        string manifest = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "build-manifest.json");
        if (!File.Exists(manifest)) return "清单待发布核验";
        string json = File.ReadAllText(manifest, Encoding.UTF8);
        BuildManifestDocument document = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<BuildManifestDocument>(json);
        if (document == null) return "清单不可读";
        if (document.productVersion != ProductVersion || document.buildDate != BuildDate) return "清单版本不一致";
        if (Sha256File(exePath) != document.exeSha256) return "EXE 哈希不一致";
        string baseDirectory = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
        string sourceDirectory = Path.Combine(baseDirectory, "源码");
        string root = Directory.GetParent(baseDirectory).FullName;
        if (!Directory.Exists(sourceDirectory)) {
            sourceDirectory = Path.Combine(baseDirectory, "程序文件", "源码");
            root = baseDirectory;
        }
        string usagePath = Path.Combine(root, "使用说明.txt");
        if (!File.Exists(usagePath) || Sha256File(usagePath) != document.usageSha256) return "使用说明哈希不一致";
        List<string> sourceParts = new List<string>();
        foreach (BuildManifestSource source in document.sources) {
            string sourcePath = Path.Combine(sourceDirectory, source.name);
            if (!File.Exists(sourcePath)) return "清单源文件缺失：" + source.name;
            string hash = Sha256File(sourcePath);
            if (hash != source.sha256) return "源文件哈希不一致：" + source.name;
            sourceParts.Add(source.name.ToUpperInvariant() + ":" + hash);
        }
        string sourceSetHash = Sha256Text(string.Join("\n", sourceParts.ToArray()));
        if (sourceSetHash != document.sourceSetSha256) return "源文件集合哈希不一致";
        info.SourceManifestHash = document.sourceSetSha256 ?? "";
        return "交付一致";
    }

    internal static string Sha256File(string path) {
        using (FileStream stream = File.OpenRead(path))
        using (SHA256 sha = SHA256.Create()) {
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        }
    }

    private static string Sha256Text(string text) {
        using (SHA256 sha = SHA256.Create()) {
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? ""))).Replace("-", "");
        }
    }
}

internal sealed class BuildManifestDocument {
    public string productVersion = "";
    public string buildDate = "";
    public string exeSha256 = "";
    public string sourceSetSha256 = "";
    public string usageSha256 = "";
    public BuildManifestSource[] sources = new BuildManifestSource[0];
}

internal sealed class BuildManifestSource {
    public string name = "";
    public string sha256 = "";
}
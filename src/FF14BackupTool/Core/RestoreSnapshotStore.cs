using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;

namespace FF14BackupTool.Core
{
    [DataContract]
    public sealed class RestoreSnapshotManifest
    {
        [DataMember(Name = "configRoot")]
        public string ConfigRoot { get; set; }

        [DataMember(Name = "createdAt")]
        public string CreatedAt { get; set; }

        [DataMember(Name = "targets")]
        public List<RestoreSnapshotTarget> Targets { get; set; } = new List<RestoreSnapshotTarget>();

        [DataMember(Name = "configFile")]
        public RestoreSnapshotFile ConfigFile { get; set; }

        [DataMember(Name = "sharedMacroFile")]
        public RestoreSnapshotFile SharedMacroFile { get; set; }
    }

    [DataContract]
    public sealed class RestoreSnapshotTarget
    {
        [DataMember(Name = "folder")]
        public string Folder { get; set; }

        [DataMember(Name = "existed")]
        public bool Existed { get; set; }

        [DataMember(Name = "files")]
        public List<RestoreSnapshotFile> Files { get; set; } = new List<RestoreSnapshotFile>();
    }

    [DataContract]
    public sealed class RestoreSnapshotFile
    {
        [DataMember(Name = "path")]
        public string Path { get; set; }

        [DataMember(Name = "size")]
        public long Size { get; set; }

        [DataMember(Name = "sha256")]
        public string Sha256 { get; set; }
    }

    public static class RestoreSnapshotStore
    {
        public static string Create(string configRoot, IEnumerable<string> targetFolders)
        {
            if (string.IsNullOrWhiteSpace(configRoot) || !Directory.Exists(configRoot))
                throw new DirectoryNotFoundException("目标配置目录不存在：" + configRoot);

            string root = Path.Combine(Path.GetTempPath(), "FF14BackupTool", "restore-snapshots");
            Directory.CreateDirectory(root);
            string path = Path.Combine(root, "snapshot_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N") + ".zip");
            var manifest = new RestoreSnapshotManifest
            {
                ConfigRoot = Path.GetFullPath(configRoot),
                CreatedAt = DateTimeOffset.Now.ToString("o")
            };

            string configPath = Path.Combine(configRoot, "FFXIV.cfg");
            manifest.ConfigFile = DescribeFile(configPath, "FFXIV.cfg");
            string sharedMacroPath = Path.Combine(configRoot, "MACROSYS.DAT");
            manifest.SharedMacroFile = DescribeFile(sharedMacroPath, "MACROSYS.DAT");

            foreach (string folder in (targetFolders ?? Enumerable.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!PathGuard.IsValidCharacterId(folder))
                    throw new InvalidDataException("目标角色 ID 不合法：" + folder);
                string directory = Path.Combine(configRoot, folder);
                var target = new RestoreSnapshotTarget
                {
                    Folder = folder,
                    Existed = Directory.Exists(directory)
                };
                if (target.Existed)
                {
                    foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                    {
                        string relative = file.Substring(configRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        target.Files.Add(DescribeFile(file, relative));
                    }
                }
                manifest.Targets.Add(target);
            }

            try
            {
                using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
                {
                    var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
                    using (var stream = manifestEntry.Open())
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                        writer.Write(JsonHelper.Serialize(manifest));

                    AddFile(archive, configPath, manifest.ConfigFile);
                    AddFile(archive, sharedMacroPath, manifest.SharedMacroFile);
                    foreach (var target in manifest.Targets)
                        foreach (var file in target.Files)
                            AddFile(archive, Path.Combine(configRoot, file.Path), file);
                }
                return path;
            }
            catch
            {
                TryDelete(path);
                throw;
            }
        }

        public static string Restore(string snapshotPath, string configRoot)
        {
            if (string.IsNullOrWhiteSpace(snapshotPath) || !File.Exists(snapshotPath))
                throw new FileNotFoundException("恢复前快照不存在。", snapshotPath);
            RestoreSnapshotManifest manifest;
            using (var archive = ZipFile.OpenRead(snapshotPath))
            {
                var entry = archive.GetEntry("manifest.json");
                if (entry == null) throw new InvalidDataException("快照清单不存在。");
                using (var reader = new StreamReader(entry.Open(), Encoding.UTF8))
                    manifest = JsonHelper.Deserialize<RestoreSnapshotManifest>(reader.ReadToEnd());
                if (manifest == null || !string.Equals(Path.GetFullPath(configRoot), manifest.ConfigRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("快照目标目录与当前目标目录不一致。");

                foreach (var target in manifest.Targets ?? new List<RestoreSnapshotTarget>())
                {
                    if (!PathGuard.IsValidCharacterId(target.Folder))
                        throw new InvalidDataException("快照包含非法角色目录。");
                    string directory = Path.Combine(configRoot, target.Folder);
                    if (Directory.Exists(directory)) Directory.Delete(directory, true);
                    if (!target.Existed) continue;
                    Directory.CreateDirectory(directory);
                    foreach (var file in target.Files ?? new List<RestoreSnapshotFile>())
                        ExtractVerified(archive, file, configRoot);
                }

                RestoreConfigFile(archive, manifest.ConfigFile, configRoot);
                RestoreSharedMacroFile(archive, manifest.SharedMacroFile, configRoot);
            }
            return snapshotPath;
        }

        private static RestoreSnapshotFile DescribeFile(string fullPath, string relative)
        {
            if (!File.Exists(fullPath))
                return new RestoreSnapshotFile { Path = relative, Size = -1, Sha256 = string.Empty };
            return new RestoreSnapshotFile
            {
                Path = relative.Replace(Path.DirectorySeparatorChar, '/'),
                Size = new FileInfo(fullPath).Length,
                Sha256 = Hash(fullPath)
            };
        }

        private static void AddFile(ZipArchive archive, string fullPath, RestoreSnapshotFile file)
        {
            if (file == null || file.Size < 0 || !File.Exists(fullPath)) return;
            string entryName = "files/" + file.Path.Replace('\\', '/');
            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            using (var input = File.OpenRead(fullPath))
            using (var output = entry.Open()) input.CopyTo(output);
        }

        private static void ExtractVerified(ZipArchive archive, RestoreSnapshotFile file, string configRoot)
        {
            if (file == null || file.Size < 0) return;
            string safe = PathGuard.CombineUnderRoot(configRoot, file.Path);
            var entry = archive.GetEntry("files/" + file.Path.Replace('\\', '/'));
            if (entry == null) throw new InvalidDataException("快照文件缺失：" + file.Path);
            string directory = Path.GetDirectoryName(safe);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            using (var input = entry.Open())
            using (var output = File.Create(safe)) input.CopyTo(output);
            if (new FileInfo(safe).Length != file.Size || !string.Equals(Hash(safe), file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("快照文件校验失败：" + file.Path);
        }

        private static void RestoreConfigFile(ZipArchive archive, RestoreSnapshotFile file, string configRoot)
        {
            string path = Path.Combine(configRoot, "FFXIV.cfg");
            if (file == null || file.Size < 0)
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            ExtractVerified(archive, file, configRoot);
        }

        private static void RestoreSharedMacroFile(ZipArchive archive, RestoreSnapshotFile file, string configRoot)
        {
            string path = Path.Combine(configRoot, "MACROSYS.DAT");
            if (file == null || file.Size < 0)
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            ExtractVerified(archive, file, configRoot);
        }

        private static string Hash(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}

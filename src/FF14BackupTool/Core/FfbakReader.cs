using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace FF14BackupTool.Core
{
    public static class FfbakReader
    {
        private const int MaxManifestBytes = 50 * 1024 * 1024;

        public static BackupInspection Inspect(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException("备份文件不存在。", path);

            string fullPath = Path.GetFullPath(path);
            using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, false))
            {
                var manifestEntry = FindEntry(archive, "manifest.json");
                if (manifestEntry != null)
                {
                    var manifest = ReadManifest(manifestEntry);
                    manifest.Validate();
                    return new BackupInspection
                    {
                        Path = fullPath,
                        IsDirectBackup = true,
                        Manifest = manifest,
                        Message = "FF14 备份"
                    };
                }

                var candidates = archive.Entries
                    .Where(e => !string.IsNullOrEmpty(e.Name) &&
                                e.FullName.EndsWith(".ffbak", StringComparison.OrdinalIgnoreCase))
                    .Select(e => new NestedBackupCandidate
                    {
                        EntryName = e.FullName,
                        Size = SafeLength(e)
                    })
                    .OrderByDescending(c => c.Size)
                    .ToList();

                if (candidates.Count == 0)
                    throw new InvalidDataException("这个 ZIP 里没有找到 .ffbak 备份。");

                return new BackupInspection
                {
                    Path = fullPath,
                    IsDirectBackup = false,
                    NestedCandidates = candidates,
                    Message = candidates.Count == 1
                        ? "便携转移包：发现 1 个备份"
                        : "便携转移包：发现 " + candidates.Count + " 个备份，请选择"
                };
            }
        }

        public static string ExtractNestedBackup(
            string outerPath,
            string entryName,
            string tempRoot,
            IProgress<OperationProgress> progress = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(entryName)) throw new ArgumentException("条目名为空。", "entryName");
            Directory.CreateDirectory(tempRoot);
            string tempPath = Path.Combine(tempRoot, Guid.NewGuid().ToString("N") + ".ffbak");

            using (var stream = new FileStream(outerPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, false))
            {
                var entry = FindEntry(archive, entryName);
                if (entry == null) throw new InvalidDataException("ZIP 中找不到备份条目：" + entryName);
                if (!PathGuard.IsSafeArchiveEntry(entry.FullName))
                    throw new InvalidDataException("ZIP 条目路径不合法：" + entry.FullName);
                long total = SafeLength(entry);
                long processed = 0;
                using (var input = entry.Open())
                using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.SequentialScan))
                {
                    byte[] buffer = new byte[81920];
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        output.Write(buffer, 0, read);
                        processed += read;
                        if (progress != null)
                        {
                            int percent = total <= 0 ? 0 : (int)Math.Min(99, processed * 100L / total);
                            progress.Report(new OperationProgress(percent, "正在读取便携包中的备份"));
                        }
                    }
                }
                return tempPath;
            }
        }

        public static FfbakSession OpenSession(string ffbakPath)
        {
            return new FfbakSession(ffbakPath);
        }

        private static FfbakManifest ReadManifest(ZipArchiveEntry entry)
        {
            if (entry.Length > MaxManifestBytes)
                throw new InvalidDataException("manifest.json 过大，备份可能已损坏。");
            using (var stream = entry.Open())
            using (var memory = new MemoryStream())
            {
                stream.CopyTo(memory);
                var manifest = JsonHelper.Deserialize<FfbakManifest>(memory.ToArray());
                if (manifest == null) throw new InvalidDataException("无法读取备份清单。");
                return manifest;
            }
        }

        internal static ZipArchiveEntry FindEntry(ZipArchive archive, string name)
        {
            if (archive == null || string.IsNullOrEmpty(name)) return null;
            string normalized = name.Replace('\\', '/');
            return archive.Entries.FirstOrDefault(e =>
                string.Equals(e.FullName, normalized, StringComparison.OrdinalIgnoreCase));
        }

        private static long SafeLength(ZipArchiveEntry entry)
        {
            try { return entry.Length; }
            catch { return 0; }
        }
    }

    public sealed class FfbakSession : IDisposable
    {
        private readonly FileStream _stream;
        private readonly ZipArchive _archive;

        public FfbakSession(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException("备份文件不存在。", path);
            Path = System.IO.Path.GetFullPath(path);
            _stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            _archive = new ZipArchive(_stream, ZipArchiveMode.Read, false);
            var manifestEntry = FfbakReader.FindEntry(_archive, "manifest.json");
            if (manifestEntry == null) throw new InvalidDataException("备份缺少 manifest.json。");
            Manifest = ReadManifestFromEntry(manifestEntry);
            Manifest.Validate();
        }

        public string Path { get; private set; }
        public FfbakManifest Manifest { get; private set; }

        public void ExtractCharacterFile(ManifestCharacter character, ManifestFile file, string destinationPath,
            IProgress<OperationProgress> progress = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (character == null || file == null) throw new ArgumentNullException();
            string archivePath = PathGuard.GetArchivePath(character.Folder, file.Path);
            var entry = FfbakReader.FindEntry(_archive, archivePath);
            if (entry == null)
                throw new InvalidDataException("备份中缺少文件：" + archivePath);
            if (entry.Length != file.Size)
                throw new InvalidDataException("备份文件大小不一致：" + file.Path);

            string directory = System.IO.Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

            string hash;
            using (var input = entry.Open())
            using (var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.SequentialScan))
            {
                hash = FfbakWriter.CopyAndHash(input, output, null, cancellationToken);
            }

            if (!string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("文件校验失败：" + file.Path);
            var info = new FileInfo(destinationPath);
            if (info.Length != file.Size)
                throw new InvalidDataException("解压后的文件大小不一致：" + file.Path);
        }

        public SystemSettingsSnapshot ReadSystemSettings(string destinationPath,
            IProgress<OperationProgress> progress = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (Manifest.SystemSettings == null || !Manifest.SystemSettings.Included) return null;
            string archivePath = string.IsNullOrWhiteSpace(Manifest.SystemSettings.ArchivePath)
                ? PathGuard.GetSystemArchivePath()
                : Manifest.SystemSettings.ArchivePath;
            var entry = FfbakReader.FindEntry(_archive, archivePath);
            if (entry == null) return null;

            string directory = System.IO.Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            using (var input = entry.Open())
            using (var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.SequentialScan))
            {
                input.CopyTo(output);
            }
            return SystemSettingsService.ReadSafeSnapshot(destinationPath);
        }

        public void ExtractSharedMacro(string destinationPath,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (Manifest.SharedMacro == null || !Manifest.SharedMacro.Included) return;
            string archivePath = string.IsNullOrWhiteSpace(Manifest.SharedMacro.ArchivePath)
                ? PathGuard.GetSharedMacroArchivePath()
                : Manifest.SharedMacro.ArchivePath;
            var entry = FfbakReader.FindEntry(_archive, archivePath);
            if (entry == null) throw new InvalidDataException("备份中缺少公用宏文件。");
            if (entry.Length != Manifest.SharedMacro.Size)
                throw new InvalidDataException("公用宏文件大小不一致。");
            string directory = System.IO.Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            string hash;
            using (var input = entry.Open())
            using (var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.SequentialScan))
            {
                hash = FfbakWriter.CopyAndHash(input, output, null, cancellationToken);
            }
            if (!string.Equals(hash, Manifest.SharedMacro.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("公用宏文件校验失败。");
            if (new FileInfo(destinationPath).Length != Manifest.SharedMacro.Size)
                throw new InvalidDataException("解压后的公用宏文件大小不一致。");
        }

        public void Dispose()
        {
            try { _archive.Dispose(); } catch { }
            try { _stream.Dispose(); } catch { }
        }

        private static FfbakManifest ReadManifestFromEntry(ZipArchiveEntry entry)
        {
            if (entry.Length > 50 * 1024 * 1024)
                throw new InvalidDataException("manifest.json 过大。");
            using (var stream = entry.Open())
            using (var memory = new MemoryStream())
            {
                stream.CopyTo(memory);
                var manifest = JsonHelper.Deserialize<FfbakManifest>(memory.ToArray());
                if (manifest == null) throw new InvalidDataException("无法读取备份清单。");
                return manifest;
            }
        }
    }
}

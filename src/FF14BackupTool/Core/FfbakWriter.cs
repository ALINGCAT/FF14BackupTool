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
    public static class FfbakWriter
    {
        private const long MaxArchiveBytes = 3L * 1024 * 1024 * 1024; // conservative for .NET Framework ZIP

        public static BackupWriteResult Write(
            string configRoot,
            BackupSelection selection,
            string destinationPath,
            CompressionLevel compressionLevel = CompressionLevel.Optimal,
            IProgress<OperationProgress> progress = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (selection == null) throw new ArgumentNullException("selection");
            if (string.IsNullOrWhiteSpace(destinationPath)) throw new ArgumentException("保存路径为空。", "destinationPath");

            var systemSnapshot = selection.SystemSettings;
            if (selection.IncludeSystemSettings && systemSnapshot == null && !string.IsNullOrWhiteSpace(configRoot))
                systemSnapshot = SystemSettingsService.CreateSnapshot(configRoot);

            string sharedMacroPath = Path.Combine(configRoot ?? string.Empty, "MACROSYS.DAT");
            bool includeSharedMacro = selection.IncludeSharedMacro && File.Exists(sharedMacroPath);
            FileInfo sharedMacroInfo = includeSharedMacro ? new FileInfo(sharedMacroPath) : null;

            var manifest = new FfbakManifest
            {
                Schema = FfbakManifest.SchemaName,
                FormatVersion = FfbakManifest.CurrentFormatVersion,
                CreatedAt = DateTimeOffset.UtcNow.ToString("o"),
                CreatedBy = new CreatedByInfo
                {
                    App = "FF14BackupTool",
                    Version = typeof(FfbakWriter).Assembly.GetName().Version.ToString(3)
                },
                Client = GamePathResolver.DetectClient(configRoot),
                Characters = new List<ManifestCharacter>(),
                SystemSettings = new ManifestSystemSettings
                {
                    Included = systemSnapshot != null && systemSnapshot.HasSections,
                    ArchivePath = systemSnapshot != null && systemSnapshot.HasSections ? PathGuard.GetSystemArchivePath() : null,
                    Sections = systemSnapshot != null
                        ? systemSnapshot.Sections.Select(s => s.Name).ToList()
                        : new List<string>()
                },
                SharedMacro = new ManifestSharedMacro
                {
                    Included = includeSharedMacro,
                    ArchivePath = includeSharedMacro ? PathGuard.GetSharedMacroArchivePath() : null,
                    Size = includeSharedMacro ? sharedMacroInfo.Length : 0,
                    Sha256 = includeSharedMacro ? ComputeSha256(sharedMacroPath) : null,
                    Mtime = includeSharedMacro ? ToIso(sharedMacroInfo.LastWriteTimeUtc) : null
                }
            };

            long totalBytes = 0;
            var characterPlans = new List<CharacterPlan>();
            foreach (var characterSelection in selection.Characters ?? new List<CharacterSelection>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (characterSelection == null || characterSelection.Character == null) continue;

                var files = characterSelection.SelectedFiles
                    .Where(f => f != null && File.Exists(f.FullPath))
                    .GroupBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (files.Count == 0) continue;

                var planned = new CharacterPlan
                {
                    Character = characterSelection.Character,
                    Files = files
                };
                characterPlans.Add(planned);
                totalBytes += files.Sum(f => f.Size);
            }

            string safeSettingsTemp = null;
            if (systemSnapshot != null && systemSnapshot.HasSections)
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "FF14BackupTool", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                safeSettingsTemp = Path.Combine(tempDir, "FFXIV.cfg.safe");
                SystemSettingsService.WriteSafeSnapshot(systemSnapshot, safeSettingsTemp);
                totalBytes += new FileInfo(safeSettingsTemp).Length;
            }

            if (includeSharedMacro)
                totalBytes += sharedMacroInfo.Length;

            if (characterPlans.Count == 0 && safeSettingsTemp == null && !includeSharedMacro)
                throw new InvalidOperationException("没有选择任何要导出的内容。");

            if (totalBytes > MaxArchiveBytes)
                throw new InvalidOperationException("备份内容超过 3 GB，当前版本不支持。请取消聊天记录或分批备份。");

            string destinationFull = Path.GetFullPath(destinationPath);
            string directory = Path.GetDirectoryName(destinationFull);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            string partialPath = destinationFull + ".partial";

            try
            {
                try
                {
                    var drive = new DriveInfo(Path.GetPathRoot(destinationFull));
                    if (drive.IsReady && drive.AvailableFreeSpace < totalBytes + 64L * 1024 * 1024)
                        throw new IOException("目标磁盘空间不足。请清理空间或取消聊天记录后重试。");
                }
                catch (IOException)
                {
                    throw;
                }
                catch
                {
                    // Drive information can fail for UNC/removable paths; let the write attempt report a useful error.
                }

                if (File.Exists(partialPath)) File.Delete(partialPath);

                using (var fileStream = new FileStream(partialPath, FileMode.CreateNew, FileAccess.ReadWrite,
                           FileShare.None, 81920, FileOptions.SequentialScan))
                using (var archive = new ZipArchive(fileStream, ZipArchiveMode.Create, false))
                {
                    long processed = 0;
                    int fileCount = 0;
                    int characterCount = 0;

                    foreach (var plan in characterPlans)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var manifestCharacter = new ManifestCharacter
                        {
                            Folder = plan.Character.FolderId,
                            Alias = plan.Character.Alias,
                            DetectedName = plan.Character.DetectedName,
                            Job = plan.Character.Job,
                            NameSource = plan.Character.NameSource,
                            LastActiveAt = ToIso(plan.Character.LastActiveUtc),
                            Files = new List<ManifestFile>()
                        };

                        foreach (var file in plan.Files)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            string archivePath = PathGuard.GetArchivePath(plan.Character.FolderId, file.RelativePath);
                            var entry = archive.CreateEntry(archivePath, compressionLevel);
                            SetEntryTime(entry, file.LastWriteTimeUtc);

                            string hash;
                            using (var input = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read,
                                       FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.SequentialScan))
                            using (var output = entry.Open())
                            {
                                hash = CopyAndHash(input, output, bytes =>
                                {
                                    processed += bytes;
                                    Report(progress, processed, totalBytes, "正在压缩 " + plan.Character.DisplayName + " / " + file.RelativePath);
                                }, cancellationToken);
                            }

                            manifestCharacter.Files.Add(new ManifestFile
                            {
                                Path = file.RelativePath.Replace('\\', '/'),
                                Size = file.Size,
                                Sha256 = hash,
                                Mtime = ToIso(file.LastWriteTimeUtc),
                                Group = file.Group.ToString()
                            });
                            fileCount++;
                        }

                        manifest.Characters.Add(manifestCharacter);
                        characterCount++;
                    }

                    if (safeSettingsTemp != null)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var entry = archive.CreateEntry(PathGuard.GetSystemArchivePath(), compressionLevel);
                        SetEntryTime(entry, DateTime.UtcNow);
                        using (var input = File.OpenRead(safeSettingsTemp))
                        using (var output = entry.Open())
                        {
                            input.CopyTo(output);
                        }
                        processed += new FileInfo(safeSettingsTemp).Length;
                        Report(progress, processed, totalBytes, "正在写入系统设置");
                    }

                    if (includeSharedMacro)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var entry = archive.CreateEntry(PathGuard.GetSharedMacroArchivePath(), compressionLevel);
                        SetEntryTime(entry, sharedMacroInfo.LastWriteTimeUtc);
                        using (var input = new FileStream(sharedMacroPath, FileMode.Open, FileAccess.Read,
                                   FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.SequentialScan))
                        using (var output = entry.Open())
                        {
                            CopyAndHash(input, output, bytes =>
                            {
                                processed += bytes;
                                Report(progress, processed, totalBytes, "正在写入公用宏");
                            }, cancellationToken);
                        }
                    }

                    manifest.Totals = new ManifestTotals
                    {
                        Files = fileCount + (includeSharedMacro ? 1 : 0),
                        Bytes = manifest.Characters.Sum(c => c.Files.Sum(f => f.Size)) +
                                (includeSharedMacro ? sharedMacroInfo.Length : 0)
                    };

                    var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
                    SetEntryTime(manifestEntry, DateTime.UtcNow);
                    using (var stream = manifestEntry.Open())
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                    {
                        writer.Write(JsonHelper.Serialize(manifest));
                    }

                    Report(progress, totalBytes, totalBytes, "正在完成备份");
                }

                AtomicReplace(partialPath, destinationFull);
                var info = new FileInfo(destinationFull);
                return new BackupWriteResult
                {
                    Path = destinationFull,
                    Size = info.Length,
                        FileCount = manifest.Totals == null ? 0 : manifest.Totals.Files,
                    CharacterCount = manifest.Characters.Count
                };
            }
            catch
            {
                try { if (File.Exists(partialPath)) File.Delete(partialPath); }
                catch { /* ignore */ }
                throw;
            }
            finally
            {
                if (safeSettingsTemp != null)
                {
                    try
                    {
                        string dir = Path.GetDirectoryName(safeSettingsTemp);
                        if (Directory.Exists(dir)) Directory.Delete(dir, true);
                    }
                    catch { /* ignore */ }
                }
            }
        }

        public static string ComputeSha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.SequentialScan))
            {
                byte[] hash = sha.ComputeHash(stream);
                return ToHex(hash);
            }
        }

        public static string CopyAndHash(Stream input, Stream output, Action<int> bytesProcessed,
            CancellationToken cancellationToken)
        {
            using (var sha = SHA256.Create())
            {
                byte[] buffer = new byte[81920];
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, read);
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    if (bytesProcessed != null) bytesProcessed(read);
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return ToHex(sha.Hash);
            }
        }

        private static void Report(IProgress<OperationProgress> progress, long processed, long total, string message)
        {
            if (progress == null) return;
            int percent = total <= 0 ? 0 : (int)Math.Min(99, processed * 100L / total);
            progress.Report(new OperationProgress(percent, message));
        }

        private static void SetEntryTime(ZipArchiveEntry entry, DateTime utc)
        {
            DateTime value = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
            if (value.Year < 1980) value = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            if (value.Year > 2107) value = new DateTime(2107, 12, 31, 0, 0, 0, DateTimeKind.Utc);
            entry.LastWriteTime = new DateTimeOffset(value);
        }

        private static string ToIso(DateTime utc)
        {
            return DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("o");
        }

        public static string ToHex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) builder.Append(b.ToString("X2"));
            return builder.ToString();
        }

        private static void AtomicReplace(string source, string destination)
        {
            if (File.Exists(destination))
            {
                File.Replace(source, destination, null);
            }
            else
            {
                File.Move(source, destination);
            }
        }

        private sealed class CharacterPlan
        {
            public CharacterInfo Character { get; set; }
            public List<CharacterFile> Files { get; set; }
        }
    }
}

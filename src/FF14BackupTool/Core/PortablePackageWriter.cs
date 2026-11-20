using System;
using System.IO;
using System.IO.Compression;
using System.Threading;

namespace FF14BackupTool.Core
{
    public sealed class PortablePackageResult
    {
        public string Path { get; set; }
        public long Size { get; set; }
    }

    public static class PortablePackageWriter
    {
        public static PortablePackageResult Create(
            string ffbakPath,
            string executablePath,
            string destinationZipPath,
            IProgress<OperationProgress> progress = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!File.Exists(ffbakPath)) throw new FileNotFoundException("备份文件不存在。", ffbakPath);
            if (!File.Exists(executablePath)) throw new FileNotFoundException("工具程序不存在。", executablePath);
            if (string.IsNullOrWhiteSpace(destinationZipPath)) throw new ArgumentException("保存路径为空。", "destinationZipPath");

            string destinationFull = Path.GetFullPath(destinationZipPath);
            string directory = Path.GetDirectoryName(destinationFull);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            string partial = destinationFull + ".partial";
            long ffbakSize = new FileInfo(ffbakPath).Length;
            long exeSize = new FileInfo(executablePath).Length;
            long total = ffbakSize + exeSize;
            long processed = 0;

            try
            {
                if (File.Exists(partial)) File.Delete(partial);
                using (var outputStream = new FileStream(partial, FileMode.CreateNew, FileAccess.ReadWrite,
                           FileShare.None, 81920, FileOptions.SequentialScan))
                using (var archive = new ZipArchive(outputStream, ZipArchiveMode.Create, false))
                {
                    var backupEntry = archive.CreateEntry(Path.GetFileName(ffbakPath), CompressionLevel.NoCompression);
                    backupEntry.LastWriteTime = new DateTimeOffset(File.GetLastWriteTimeUtc(ffbakPath), TimeSpan.Zero);
                    using (var input = new FileStream(ffbakPath, FileMode.Open, FileAccess.Read,
                               FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.SequentialScan))
                    using (var entryStream = backupEntry.Open())
                    {
                        processed += CopyWithProgress(input, entryStream, ffbakSize, processed, total,
                            "正在打包备份文件", progress, cancellationToken);
                    }

                    var exeEntry = archive.CreateEntry(Path.GetFileName(executablePath), CompressionLevel.Optimal);
                    exeEntry.LastWriteTime = new DateTimeOffset(File.GetLastWriteTimeUtc(executablePath), TimeSpan.Zero);
                    using (var input = new FileStream(executablePath, FileMode.Open, FileAccess.Read,
                               FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.SequentialScan))
                    using (var entryStream = exeEntry.Open())
                    {
                        processed += CopyWithProgress(input, entryStream, exeSize, processed, total,
                            "正在打包本工具", progress, cancellationToken);
                    }
                }

                if (File.Exists(destinationFull))
                    File.Replace(partial, destinationFull, null);
                else
                    File.Move(partial, destinationFull);

                progress?.Report(new OperationProgress(100, "便携转移包已生成"));
                return new PortablePackageResult
                {
                    Path = destinationFull,
                    Size = new FileInfo(destinationFull).Length
                };
            }
            catch
            {
                try { if (File.Exists(partial)) File.Delete(partial); }
                catch { /* ignore */ }
                throw;
            }
        }

        private static long CopyWithProgress(
            Stream input,
            Stream output,
            long sourceLength,
            long alreadyProcessed,
            long total,
            string message,
            IProgress<OperationProgress> progress,
            CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[81920];
            long copied = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                output.Write(buffer, 0, read);
                copied += read;
                if (progress != null && total > 0)
                {
                    int percent = (int)Math.Min(99, (alreadyProcessed + copied) * 100L / total);
                    progress.Report(new OperationProgress(percent, message));
                }
            }
            return copied;
        }
    }
}

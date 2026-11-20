using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace FF14BackupTool.Core
{
    public sealed class RestoreFailedException : Exception
    {
        public RestoreFailedException(string message, RestoreResult result, Exception inner)
            : base(message, inner)
        {
            Result = result;
        }

        public RestoreResult Result { get; private set; }
    }

    public static class RestoreService
    {
        public static RestoreResult Restore(
            string ffbakPath,
            string configRoot,
            IList<RestoreMapping> mappings,
            RestoreOptions options,
            IProgress<OperationProgress> progress = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (options == null) options = new RestoreOptions();
            if (mappings == null || mappings.Count == 0)
                throw new InvalidOperationException("没有选择要恢复的角色。");
            if (string.IsNullOrWhiteSpace(configRoot) || !Directory.Exists(configRoot))
                throw new DirectoryNotFoundException("目标配置目录不存在：" + configRoot);
            if (GameProcessProbe.IsGameRunning())
                throw new InvalidOperationException("检测到游戏正在运行。请先完全退出游戏，再执行恢复。");
            if (!GamePathResolver.TestWritable(configRoot))
                throw new UnauthorizedAccessException("目标配置目录不可写。国服默认安装在 Program Files 下时，需要以管理员身份运行。");

            var result = new RestoreResult();
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            string backupRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FF14BackupTool", "pre-restore", timestamp);
            Directory.CreateDirectory(backupRoot);
            result.PreRestoreBackupPath = backupRoot;

            string stageRoot = Path.Combine(configRoot, ".ffbak_stage_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stageRoot);
            var actions = new List<RestoreAction>();
            var targetIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                using (var session = FfbakReader.OpenSession(ffbakPath))
                {
                    var manifest = session.Manifest;
                    var characterMap = manifest.Characters.ToDictionary(c => c.Folder, c => c, StringComparer.OrdinalIgnoreCase);

                    var plans = new List<RestorePlan>();
                    foreach (var mapping in mappings)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (mapping == null || string.IsNullOrWhiteSpace(mapping.SourceFolderId)) continue;
                        ManifestCharacter source;
                        if (!characterMap.TryGetValue(mapping.SourceFolderId, out source))
                        {
                            result.Warnings.Add("备份中找不到角色：" + mapping.SourceFolderId);
                            continue;
                        }

                        string targetId = string.IsNullOrWhiteSpace(mapping.TargetFolderId)
                            ? source.Folder
                            : mapping.TargetFolderId;
                        if (!PathGuard.IsValidCharacterId(targetId))
                            throw new InvalidDataException("目标角色 ID 不合法：" + targetId);
                        if (!targetIds.Add(targetId))
                            throw new InvalidOperationException("同一个目标角色只能映射一个备份角色：" + targetId);

                        var plan = new RestorePlan
                        {
                            Source = source,
                            TargetFolderId = targetId,
                            CrossCharacter = !string.Equals(source.Folder, targetId, StringComparison.OrdinalIgnoreCase),
                            Files = source.Files
                        };
                        plans.Add(plan);
                    }

                    if (plans.Count == 0)
                        throw new InvalidOperationException("没有匹配到任何可恢复的角色。");

                    long requiredBytes = plans.Sum(p => p.Files
                        .Where(f => ShouldRestore(f, p.CrossCharacter, options))
                        .Sum(f => f.Size)) + 64L * 1024 * 1024;
                    try
                    {
                        var drive = new DriveInfo(Path.GetPathRoot(configRoot));
                        if (drive.IsReady && drive.AvailableFreeSpace < requiredBytes)
                            throw new IOException("目标磁盘空间不足，无法安全恢复。");
                    }
                    catch (IOException)
                    {
                        throw;
                    }
                    catch
                    {
                        // UNC/removable paths can fail; the write attempt will report a useful error.
                    }

                    int totalFiles = plans.Sum(p => p.Files.Count(f =>
                        ShouldRestore(f, p.CrossCharacter, options)));
                    int processedFiles = 0;

                    foreach (var plan in plans)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string targetDirectory = Path.Combine(configRoot, plan.TargetFolderId);
                        bool targetExists = Directory.Exists(targetDirectory);
                        if (!targetExists)
                        {
                            if (!options.CreateMissingTargetCharacter)
                                throw new InvalidOperationException("目标角色文件夹不存在：" + plan.TargetFolderId);
                            Directory.CreateDirectory(targetDirectory);
                        }

                        string fullBackupDir = null;
                        if (plan.CrossCharacter && targetExists)
                        {
                            fullBackupDir = Path.Combine(backupRoot, plan.TargetFolderId);
                            progress?.Report(new OperationProgress(0, "正在备份目标角色 " + plan.TargetFolderId));
                            int copied = CopyDirectory(targetDirectory, fullBackupDir, cancellationToken);
                            result.BackedUpFiles += copied;
                        }

                        bool skippedUiSave = false;
                        bool skippedChat = false;

                        foreach (var file in plan.Files)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (!ShouldRestore(file, plan.CrossCharacter, options))
                            {
                                if (plan.CrossCharacter && IsUiSave(file.Path)) skippedUiSave = true;
                                if (plan.CrossCharacter && IsChatHistory(file)) skippedChat = true;
                                result.SkippedFiles++;
                                continue;
                            }

                            string relative = PathGuard.NormalizeRelativePath(file.Path);
                            string stagePath = PathGuard.CombineUnderRoot(stageRoot, plan.TargetFolderId + "/" + relative);
                            session.ExtractCharacterFile(plan.Source, file, stagePath, progress, cancellationToken);

                            string targetPath = PathGuard.CombineUnderRoot(configRoot, plan.TargetFolderId + "/" + relative);
                            bool existed = File.Exists(targetPath);
                            string backupPath = null;
                            if (existed)
                            {
                                if (fullBackupDir != null)
                                {
                                    backupPath = PathGuard.CombineUnderRoot(fullBackupDir, relative);
                                }
                                else
                                {
                                    string perFileBackupRoot = Path.Combine(backupRoot, "files", plan.TargetFolderId);
                                    backupPath = PathGuard.CombineUnderRoot(perFileBackupRoot, relative);
                                    string backupDirectory = Path.GetDirectoryName(backupPath);
                                    if (!string.IsNullOrWhiteSpace(backupDirectory)) Directory.CreateDirectory(backupDirectory);
                                    ClearReadOnly(targetPath);
                                    File.Copy(targetPath, backupPath, true);
                                    result.BackedUpFiles++;
                                }
                            }

                            string targetDirectoryForFile = Path.GetDirectoryName(targetPath);
                            if (!string.IsNullOrWhiteSpace(targetDirectoryForFile))
                                Directory.CreateDirectory(targetDirectoryForFile);

                            ClearReadOnly(targetPath);
                            if (File.Exists(targetPath))
                            {
                                File.Replace(stagePath, targetPath, null);
                            }
                            else
                            {
                                File.Move(stagePath, targetPath);
                            }

                            actions.Add(new RestoreAction
                            {
                                TargetPath = targetPath,
                                BackupPath = backupPath,
                                Created = !existed
                            });
                            result.RestoredFiles++;
                            processedFiles++;
                            if (progress != null)
                            {
                                int percent = totalFiles <= 0 ? 0 : (int)Math.Min(99, processedFiles * 100L / totalFiles);
                                progress.Report(new OperationProgress(percent,
                                    "正在恢复 " + plan.Source.DisplayName + " / " + relative));
                            }
                        }

                        result.CharactersRestored++;
                        if (skippedUiSave)
                            result.Warnings.Add("跨角色恢复已跳过 UISAVE.DAT；肖像/计时等数据可能不完整。");
                        if (skippedChat)
                            result.Warnings.Add("跨角色恢复已跳过聊天记录/ACQ.DAT。");
                    }

                    if (options.IncludeSystemSettings && manifest.SystemSettings != null && manifest.SystemSettings.Included)
                    {
                        string safePath = Path.Combine(stageRoot, "system", "FFXIV.cfg.safe");
                        var snapshot = session.ReadSystemSettings(safePath, progress, cancellationToken);
                        if (snapshot != null && snapshot.HasSections)
                        {
                            var merge = SystemSettingsService.MergeInto(
                                snapshot, configRoot, Path.Combine(backupRoot, "system"));
                            if (merge.Restored) result.SystemSettingsRestored = true;
                            else
                            {
                                result.SystemSettingsSkipped = true;
                                if (!string.IsNullOrWhiteSpace(merge.Message))
                                    result.Warnings.Add(merge.Message);
                            }
                        }
                        else
                        {
                            result.SystemSettingsSkipped = true;
                            result.Warnings.Add("备份中的系统设置无法读取，已跳过。");
                        }
                    }
                    else if (options.IncludeSystemSettings)
                    {
                        result.SystemSettingsSkipped = true;
                    }

                    if (manifest.SharedMacro != null && manifest.SharedMacro.Included)
                    {
                        string sharedMacroPath = Path.Combine(configRoot, "MACROSYS.DAT");
                        string sharedMacroBackup = null;
                        if (File.Exists(sharedMacroPath))
                        {
                            sharedMacroBackup = Path.Combine(backupRoot, "global", "MACROSYS.DAT");
                            string directory = Path.GetDirectoryName(sharedMacroBackup);
                            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                            ClearReadOnly(sharedMacroPath);
                            File.Copy(sharedMacroPath, sharedMacroBackup, true);
                            result.BackedUpFiles++;
                        }

                        string stageMacro = Path.Combine(stageRoot, "global", "MACROSYS.DAT");
                        session.ExtractSharedMacro(stageMacro, cancellationToken);
                        string targetDirectory = Path.GetDirectoryName(sharedMacroPath);
                        if (!string.IsNullOrWhiteSpace(targetDirectory)) Directory.CreateDirectory(targetDirectory);
                        bool existed = File.Exists(sharedMacroPath);
                        ClearReadOnly(sharedMacroPath);
                        if (existed) File.Replace(stageMacro, sharedMacroPath, null);
                        else File.Move(stageMacro, sharedMacroPath);
                        actions.Add(new RestoreAction
                        {
                            TargetPath = sharedMacroPath,
                            BackupPath = sharedMacroBackup,
                            Created = !existed
                        });
                        result.RestoredFiles++;
                    }
                }

                result.Message = "恢复完成：写入 " + result.RestoredFiles + " 个文件，跳过 " +
                                 result.SkippedFiles + " 个文件。";
                progress?.Report(new OperationProgress(100, "恢复完成"));
                return result;
            }
            catch (OperationCanceledException)
            {
                var rollbackWarnings = Rollback(actions);
                result.Warnings.AddRange(rollbackWarnings);
                AppLog.Info("Restore cancelled; rollback actions=" + actions.Count);
                throw;
            }
            catch (Exception ex)
            {
                var rollbackWarnings = Rollback(actions);
                result.Warnings.AddRange(rollbackWarnings);
                result.Message = rollbackWarnings.Count == 0
                    ? "恢复失败，已自动回滚。目标原文件备份在：\n" + result.PreRestoreBackupPath
                    : "恢复失败，部分文件回滚失败。请从以下目录手动恢复：\n" + result.PreRestoreBackupPath;
                AppLog.Error("Restore failed", ex);
                throw new RestoreFailedException(result.Message + "\n\n原因：" + ex.Message, result, ex);
            }
            finally
            {
                try
                {
                    if (Directory.Exists(stageRoot)) Directory.Delete(stageRoot, true);
                }
                catch
                {
                    // A locked staging file is harmless; it is under the config folder.
                }
            }
        }

        private static bool ShouldRestore(ManifestFile file, bool crossCharacter, RestoreOptions options)
        {
            if (file == null) return false;
            if (!crossCharacter) return true;
            if (IsUiSave(file.Path) && !options.IncludeCrossCharacterUiSave) return false;
            if (IsChatHistory(file) && !options.IncludeCrossCharacterChatHistory) return false;
            return true;
        }

        private static bool IsUiSave(string relativePath)
        {
            return string.Equals(Path.GetFileName(relativePath ?? string.Empty), "UISAVE.DAT",
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsChatHistory(ManifestFile file)
        {
            if (file == null) return false;
            if (string.Equals(file.Group, FileGroup.ChatHistory.ToString(), StringComparison.OrdinalIgnoreCase))
                return true;
            return (file.Path ?? string.Empty).StartsWith("log/", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileName(file.Path ?? string.Empty), "ACQ.DAT", StringComparison.OrdinalIgnoreCase);
        }

        private static int CopyDirectory(string source, string destination, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(destination);
            int count = 0;
            foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string relative = directory.Substring(source.Length).TrimStart(Path.DirectorySeparatorChar);
                Directory.CreateDirectory(Path.Combine(destination, relative));
            }
            foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string relative = file.Substring(source.Length).TrimStart(Path.DirectorySeparatorChar);
                string target = Path.Combine(destination, relative);
                string dir = Path.GetDirectoryName(target);
                if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
                File.Copy(file, target, true);
                count++;
            }
            return count;
        }

        private static void ClearReadOnly(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                }
            }
            catch
            {
                // The subsequent operation will report a useful error.
            }
        }

        private static List<string> Rollback(List<RestoreAction> actions)
        {
            var warnings = new List<string>();
            for (int i = actions.Count - 1; i >= 0; i--)
            {
                var action = actions[i];
                try
                {
                    if (!string.IsNullOrWhiteSpace(action.BackupPath) && File.Exists(action.BackupPath))
                    {
                        string dir = Path.GetDirectoryName(action.TargetPath);
                        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
                        ClearReadOnly(action.TargetPath);
                        File.Copy(action.BackupPath, action.TargetPath, true);
                    }
                    else if (action.Created && File.Exists(action.TargetPath))
                    {
                        File.Delete(action.TargetPath);
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add("回滚失败：" + action.TargetPath + "（" + ex.Message + "）");
                }
            }
            return warnings;
        }

        private sealed class RestorePlan
        {
            public ManifestCharacter Source { get; set; }
            public string TargetFolderId { get; set; }
            public bool CrossCharacter { get; set; }
            public List<ManifestFile> Files { get; set; }
        }

        private sealed class RestoreAction
        {
            public string TargetPath { get; set; }
            public string BackupPath { get; set; }
            public bool Created { get; set; }
        }
    }
}

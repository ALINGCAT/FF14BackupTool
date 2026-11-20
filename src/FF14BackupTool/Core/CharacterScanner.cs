using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace FF14BackupTool.Core
{
    public static class CharacterScanner
    {
        public static List<CharacterInfo> Scan(
            string configRoot,
            bool resolveNames = true,
            IProgress<OperationProgress> progress = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var result = new List<CharacterInfo>();
            if (string.IsNullOrWhiteSpace(configRoot) || !Directory.Exists(configRoot))
                return result;

            string[] directories;
            try
            {
                directories = Directory.EnumerateDirectories(configRoot, "FFXIV_CHR*", SearchOption.TopDirectoryOnly).ToArray();
            }
            catch
            {
                return result;
            }

            var valid = directories
                .Where(d => PathGuard.IsValidCharacterId(Path.GetFileName(d)))
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                .ToList();

            int index = 0;
            foreach (string directory in valid)
            {
                cancellationToken.ThrowIfCancellationRequested();
                index++;
                if (progress != null)
                {
                    int percent = valid.Count == 0 ? 0 : (int)((index - 1) * 100.0 / valid.Count);
                    progress.Report(new OperationProgress(percent, "正在扫描角色 " + Path.GetFileName(directory)));
                }

                try
                {
                    var character = ScanCharacter(directory, resolveNames, cancellationToken);
                    if (character != null) result.Add(character);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Skip an unreadable folder but keep scanning the rest.
                }
            }

            return result
                .OrderByDescending(c => c.LastActiveUtc)
                .ThenBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static CharacterInfo ScanCharacter(string directory, bool resolveNames, CancellationToken cancellationToken)
        {
            string folderId = Path.GetFileName(directory);
            if (!PathGuard.IsValidCharacterId(folderId)) return null;

            var files = new List<CharacterFile>();

            foreach (string filePath in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string name = Path.GetFileName(filePath);
                if (string.Equals(name, "log", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var info = new FileInfo(filePath);
                    string relative = name;
                    files.Add(new CharacterFile
                    {
                        RelativePath = relative,
                        FullPath = filePath,
                        Size = info.Length,
                        LastWriteTimeUtc = info.LastWriteTimeUtc,
                        Group = FileGroups.Classify(relative)
                    });
                }
                catch
                {
                    // ignore
                }
            }

            string logDirectory = Path.Combine(directory, "log");
            if (Directory.Exists(logDirectory))
            {
                try
                {
                    foreach (string filePath in Directory.EnumerateFiles(logDirectory, "*", SearchOption.TopDirectoryOnly))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            var info = new FileInfo(filePath);
                            string relative = "log/" + Path.GetFileName(filePath);
                            files.Add(new CharacterFile
                            {
                                RelativePath = relative,
                                FullPath = filePath,
                                Size = info.Length,
                                LastWriteTimeUtc = info.LastWriteTimeUtc,
                                Group = FileGroup.ChatHistory
                            });
                        }
                        catch
                        {
                            // ignore
                        }
                    }
                }
                catch
                {
                    // ignore
                }
            }

            files = files
                .OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var character = new CharacterInfo
            {
                FolderId = folderId,
                FolderPath = directory,
                Files = files,
                TotalSize = files.Sum(f => f.Size),
                Alias = SettingsStore.Current.GetAlias(folderId)
            };

            if (files.Count > 0)
            {
                character.LastActiveUtc = files.Max(f => f.LastWriteTimeUtc);
            }
            else
            {
                try { character.LastActiveUtc = Directory.GetLastWriteTimeUtc(directory); }
                catch { character.LastActiveUtc = DateTime.UtcNow; }
            }

            if (resolveNames)
            {
                try
                {
                    var name = CharacterNameResolver.TryResolve(directory);
                    if (name != null)
                    {
                        character.DetectedName = name.Name;
                        character.Job = name.Job;
                        character.NameSource = name.Source;
                    }
                }
                catch
                {
                    // Name is optional.
                }
            }

            return character;
        }
    }
}

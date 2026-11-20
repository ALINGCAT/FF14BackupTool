using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace FF14BackupTool.Core
{
    public sealed class SystemSettingsMergeResult
    {
        public bool Restored { get; set; }
        public bool Skipped { get; set; }
        public string BackupPath { get; set; }
        public string Message { get; set; }
    }

    public static class SystemSettingsService
    {
        public static readonly string[] SafeSectionNames =
        {
            "Sound Settings",
            "SoundPlay Settings",
            "Font Settings",
            "Mouse Settings",
            "UI Settings",
            "Move Operation",
            "Cutscene Settings",
            "GamePad Button Settings"
        };

        public static readonly string[] PreservedSectionNames =
        {
            "Version",
            "Network Settings",
            "Display Settings",
            "Graphics Settings",
            "Graphics Settings DX11",
            "GamePad Settings",
            "CUSTOM CONFIGURATION"
        };

        private static readonly Regex SectionRegex = new Regex(@"^\s*<([^>]+)>\s*$", RegexOptions.Compiled);

        public static SystemSettingsSnapshot CreateSnapshot(string configRoot)
        {
            string path = Path.Combine(configRoot ?? string.Empty, "FFXIV.cfg");
            if (!File.Exists(path)) return null;

            var document = CfgDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            var snapshot = new SystemSettingsSnapshot { SourceConfigPath = path };
            foreach (string safeName in SafeSectionNames)
            {
                var section = document.FindSection(safeName);
                if (section == null) continue;
                snapshot.Sections.Add(new CfgSection
                {
                    Name = section.Name,
                    Lines = new List<string>(section.Lines)
                });
            }
            return snapshot.HasSections ? snapshot : null;
        }

        public static void WriteSafeSnapshot(SystemSettingsSnapshot snapshot, string destinationPath)
        {
            if (snapshot == null || !snapshot.HasSections)
                throw new InvalidOperationException("没有可写入的系统设置。");

            var document = new CfgDocument();
            document.Preamble.Add("<FINAL FANTASY XIV Config File>");
            document.Preamble.Add(string.Empty);
            foreach (var section in snapshot.Sections)
            {
                document.Sections.Add(new CfgSection
                {
                    Name = section.Name,
                    Lines = new List<string>(section.Lines)
                });
            }
            string directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(destinationPath, document.ToText(), new UTF8Encoding(false));
        }

        public static SystemSettingsSnapshot ReadSafeSnapshot(string path)
        {
            if (!File.Exists(path)) return null;
            var document = CfgDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            var snapshot = new SystemSettingsSnapshot { SourceConfigPath = path };
            foreach (var section in document.Sections)
            {
                if (SafeSectionNames.Contains(section.Name, StringComparer.OrdinalIgnoreCase))
                {
                    snapshot.Sections.Add(section);
                }
            }
            return snapshot.HasSections ? snapshot : null;
        }

        public static SystemSettingsMergeResult MergeInto(
            SystemSettingsSnapshot snapshot,
            string targetConfigRoot,
            string backupDirectory)
        {
            var result = new SystemSettingsMergeResult();
            if (snapshot == null || !snapshot.HasSections)
            {
                result.Skipped = true;
                result.Message = "备份中没有系统设置。";
                return result;
            }

            string targetPath = Path.Combine(targetConfigRoot ?? string.Empty, "FFXIV.cfg");
            if (!File.Exists(targetPath))
            {
                result.Skipped = true;
                result.Message = "目标目录没有 FFXIV.cfg，已跳过系统设置。建议先启动一次游戏。";
                return result;
            }

            byte[] originalBytes = File.ReadAllBytes(targetPath);
            bool hasBom = originalBytes.Length >= 3 &&
                          originalBytes[0] == 0xEF && originalBytes[1] == 0xBB && originalBytes[2] == 0xBF;
            string originalText = new UTF8Encoding(false).GetString(
                originalBytes, hasBom ? 3 : 0, originalBytes.Length - (hasBom ? 3 : 0));

            var targetDocument = CfgDocument.Parse(originalText);
            var originalPreserved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in PreservedSectionNames)
            {
                var section = targetDocument.FindSection(name);
                if (section != null) originalPreserved[name] = section.ToRawText();
            }

            Directory.CreateDirectory(backupDirectory);
            string backupPath = Path.Combine(backupDirectory, "FFXIV.cfg.bak");
            File.Copy(targetPath, backupPath, true);

            try
            {
                foreach (var sourceSection in snapshot.Sections)
                {
                    if (!SafeSectionNames.Contains(sourceSection.Name, StringComparer.OrdinalIgnoreCase))
                        continue;
                    targetDocument.ReplaceSection(sourceSection.Name, sourceSection.Lines);
                }

                string mergedText = targetDocument.ToText();
                var verify = CfgDocument.Parse(mergedText);

                foreach (var pair in originalPreserved)
                {
                    var after = verify.FindSection(pair.Key);
                    if (after == null || !string.Equals(after.ToRawText(), pair.Value, StringComparison.Ordinal))
                        throw new InvalidDataException("合并校验失败：受保护的设置段被改动：" + pair.Key);
                }

                string tempPath = targetPath + ".ffbak_tmp_" + Guid.NewGuid().ToString("N");
                File.WriteAllText(tempPath, mergedText, new UTF8Encoding(hasBom));
                if (File.Exists(targetPath))
                {
                    File.Replace(tempPath, targetPath, null);
                }
                else
                {
                    File.Move(tempPath, targetPath);
                }

                result.Restored = true;
                result.BackupPath = backupPath;
                result.Message = "系统设置已合并非显示/画面/设备/网络段。";
                return result;
            }
            catch (Exception ex)
            {
                try
                {
                    if (File.Exists(backupPath))
                    {
                        File.Copy(backupPath, targetPath, true);
                    }
                }
                catch
                {
                    // Keep the backup for manual recovery.
                }
                result.Skipped = true;
                result.BackupPath = backupPath;
                result.Message = "系统设置合并失败，已保留原文件备份：" + ex.Message;
                return result;
            }
        }

        public static bool IsSafeSection(string name)
        {
            return SafeSectionNames.Contains(name, StringComparer.OrdinalIgnoreCase);
        }

        private sealed class CfgDocument
        {
            public List<string> Preamble { get; } = new List<string>();
            public List<CfgSection> Sections { get; } = new List<CfgSection>();
            public string NewLine { get; set; } = "\r\n";

            public CfgSection FindSection(string name)
            {
                return Sections.FirstOrDefault(s =>
                    string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            }

            public void ReplaceSection(string name, IList<string> lines)
            {
                var section = FindSection(name);
                if (section != null)
                {
                    section.Lines = new List<string>(lines ?? new List<string>());
                    return;
                }

                var newSection = new CfgSection
                {
                    Name = name,
                    Lines = new List<string>(lines ?? new List<string>())
                };
                int insertAt = Sections.Count;
                for (int i = 0; i < Sections.Count; i++)
                {
                    if (string.Equals(Sections[i].Name, "CUSTOM CONFIGURATION", StringComparison.OrdinalIgnoreCase))
                    {
                        insertAt = i;
                        break;
                    }
                }
                Sections.Insert(insertAt, newSection);
            }

            public string ToText()
            {
                var builder = new StringBuilder();
                foreach (string line in Preamble)
                {
                    builder.Append(line).Append(NewLine);
                }
                if (Preamble.Count > 0 && !string.IsNullOrEmpty(Preamble[Preamble.Count - 1]))
                    builder.Append(NewLine);

                foreach (var section in Sections)
                {
                    builder.Append('<').Append(section.Name).Append('>').Append(NewLine);
                    foreach (string line in section.Lines)
                    {
                        builder.Append(line).Append(NewLine);
                    }
                    builder.Append(NewLine);
                }
                return builder.ToString();
            }

            public static CfgDocument Parse(string text)
            {
                var document = new CfgDocument
                {
                    NewLine = text != null && text.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n"
                };
                if (string.IsNullOrEmpty(text)) return document;

                string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                CfgSection current = null;
                foreach (string line in lines)
                {
                    Match match = SectionRegex.Match(line ?? string.Empty);
                    if (match.Success)
                    {
                        string name = match.Groups[1].Value.Trim();
                        if (string.Equals(name, "FINAL FANTASY XIV Config File", StringComparison.OrdinalIgnoreCase))
                        {
                            current = null;
                            document.Preamble.Add(line);
                            continue;
                        }
                        current = new CfgSection { Name = name };
                        document.Sections.Add(current);
                        continue;
                    }

                    if (current == null)
                    {
                        document.Preamble.Add(line ?? string.Empty);
                    }
                    else
                    {
                        current.Lines.Add(line ?? string.Empty);
                    }
                }

                // Remove trailing empty lines from each section; ToText adds spacing.
                foreach (var section in document.Sections)
                {
                    while (section.Lines.Count > 0 && string.IsNullOrWhiteSpace(section.Lines[section.Lines.Count - 1]))
                        section.Lines.RemoveAt(section.Lines.Count - 1);
                }
                while (document.Preamble.Count > 0 && string.IsNullOrWhiteSpace(document.Preamble[document.Preamble.Count - 1]))
                    document.Preamble.RemoveAt(document.Preamble.Count - 1);

                return document;
            }
        }
    }

    internal static class CfgSectionExtensions
    {
        public static string ToRawText(this CfgSection section)
        {
            var builder = new StringBuilder();
            builder.Append('<').Append(section.Name).Append('>').Append('\n');
            foreach (string line in section.Lines)
                builder.Append(line).Append('\n');
            return builder.ToString();
        }
    }
}

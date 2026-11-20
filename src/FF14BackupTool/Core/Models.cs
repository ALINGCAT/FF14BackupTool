using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FF14BackupTool.Core
{
    public enum FileGroup
    {
        Hud = 0,
        Character = 1,
        Hotbar = 2,
        Macro = 3,
        Portrait = 4,
        Inventory = 5,
        ChatSettings = 6,
        ChatHistory = 7,
        GoldSaucer = 8,
        Other = 9
    }

    public sealed class CharacterFile
    {
        public string RelativePath { get; set; }
        public string FullPath { get; set; }
        public long Size { get; set; }
        public DateTime LastWriteTimeUtc { get; set; }
        public FileGroup Group { get; set; }

        public bool IsLog
        {
            get { return RelativePath.StartsWith("log/", StringComparison.OrdinalIgnoreCase); }
        }
    }

    public sealed class CharacterInfo
    {
        public string FolderId { get; set; }
        public string FolderPath { get; set; }
        public string Alias { get; set; }
        public string DetectedName { get; set; }
        public string Job { get; set; }
        public string NameSource { get; set; }
        public DateTime LastActiveUtc { get; set; }
        public long TotalSize { get; set; }
        public List<CharacterFile> Files { get; set; } = new List<CharacterFile>();

        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Alias)) return Alias.Trim();
                if (!string.IsNullOrWhiteSpace(DetectedName)) return DetectedName.Trim();
                return FolderId;
            }
        }

        public string DetailLine
        {
            get
            {
                var bits = new List<string>();
                if (!string.IsNullOrWhiteSpace(DetectedName)) bits.Add(DetectedName);
                if (!string.IsNullOrWhiteSpace(Job)) bits.Add(Job);
                bits.Add(FolderId);
                return string.Join(" · ", bits);
            }
        }

        public IEnumerable<CharacterFile> GetFiles(FileGroup group)
        {
            return Files.Where(f => f.Group == group);
        }
    }

    public sealed class CharacterNameResult
    {
        public string Name { get; set; }
        public string Job { get; set; }
        public string Source { get; set; }
        public DateTimeOffset? Timestamp { get; set; }
    }

    public sealed class ConfigLocation
    {
        public string SelectedPath { get; set; }
        public string ConfigRoot { get; set; }
        public string ExpectedPath { get; set; }
        public string GameRoot { get; set; }
        public string Client { get; set; }
        public bool Exists { get; set; }
        public bool IsWritable { get; set; }
        public bool CanCreate { get; set; }
    }

    public sealed class CfgSection
    {
        public string Name { get; set; }
        public List<string> Lines { get; set; } = new List<string>();
    }

    public sealed class SystemSettingsSnapshot
    {
        public string SourceConfigPath { get; set; }
        public List<CfgSection> Sections { get; set; } = new List<CfgSection>();

        public bool HasSections
        {
            get { return Sections != null && Sections.Count > 0; }
        }
    }

    public sealed class CharacterSelection
    {
        public CharacterInfo Character { get; set; }
        public HashSet<FileGroup> Groups { get; set; } = new HashSet<FileGroup>();

        public IEnumerable<CharacterFile> SelectedFiles
        {
            get
            {
                if (Character == null || Character.Files == null) yield break;
                foreach (var file in Character.Files)
                {
                    if (Groups.Contains(file.Group)) yield return file;
                }
            }
        }
    }

    public sealed class BackupSelection
    {
        public List<CharacterSelection> Characters { get; set; } = new List<CharacterSelection>();
        public bool IncludeSystemSettings { get; set; }
        public bool IncludeSharedMacro { get; set; }
        public SystemSettingsSnapshot SystemSettings { get; set; }
    }

    public sealed class RestoreMapping
    {
        public string SourceFolderId { get; set; }
        public string TargetFolderId { get; set; }
        public bool CrossCharacter
        {
            get { return !string.Equals(SourceFolderId, TargetFolderId, StringComparison.OrdinalIgnoreCase); }
        }
    }

    public sealed class RestoreOptions
    {
        public bool IncludeSystemSettings { get; set; } = true;
        public bool IncludeCrossCharacterUiSave { get; set; }
        public bool IncludeCrossCharacterChatHistory { get; set; }
        public bool CreateMissingTargetCharacter { get; set; } = true;
        public bool VerifyHashes { get; set; } = true;
    }

    public sealed class RestoreResult
    {
        public int RestoredFiles { get; set; }
        public int SkippedFiles { get; set; }
        public int BackedUpFiles { get; set; }
        public int CharactersRestored { get; set; }
        public bool SystemSettingsRestored { get; set; }
        public bool SystemSettingsSkipped { get; set; }
        public string PreRestoreBackupPath { get; set; }
        public string RestoreSnapshotPath { get; set; }
        public string Message { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public sealed class OperationProgress
    {
        public OperationProgress(int percent, string message)
        {
            Percent = Math.Max(0, Math.Min(100, percent));
            Message = message ?? string.Empty;
        }

        public int Percent { get; private set; }
        public string Message { get; private set; }
        public bool IsIndeterminate { get; set; }
    }

    public static class FileGroups
    {
        public static readonly FileGroup[] All =
        {
            FileGroup.Hud,
            FileGroup.Character,
            FileGroup.Hotbar,
            FileGroup.Macro,
            FileGroup.Portrait,
            FileGroup.Inventory,
            FileGroup.ChatSettings,
            FileGroup.ChatHistory,
            FileGroup.GoldSaucer,
            FileGroup.Other
        };

        public static readonly FileGroup[] DefaultOn =
        {
            FileGroup.Hud,
            FileGroup.Character,
            FileGroup.Hotbar,
            FileGroup.Macro,
            FileGroup.Portrait,
            FileGroup.Inventory,
            FileGroup.ChatSettings,
            FileGroup.GoldSaucer,
            FileGroup.Other
        };

        private static readonly Dictionary<string, FileGroup> KnownFiles =
            new Dictionary<string, FileGroup>(StringComparer.OrdinalIgnoreCase)
            {
                { "ADDON.DAT", FileGroup.Hud },
                { "COMMON.DAT", FileGroup.Character },
                { "CONTROL0.DAT", FileGroup.Character },
                { "CONTROL1.DAT", FileGroup.Character },
                { "HOTBAR.DAT", FileGroup.Hotbar },
                { "KEYBIND.DAT", FileGroup.Hotbar },
                { "MACRO.DAT", FileGroup.Macro },
                { "GEARSET.DAT", FileGroup.Portrait },
                { "UISAVE.DAT", FileGroup.Portrait },
                { "ITEMFDR.DAT", FileGroup.Inventory },
                { "ITEMODR.DAT", FileGroup.Inventory },
                { "LOGFLTR.DAT", FileGroup.ChatSettings },
                { "ACQ.DAT", FileGroup.ChatHistory },
                { "GS.DAT", FileGroup.GoldSaucer }
            };

        public static string Label(FileGroup group)
        {
            switch (group)
            {
                case FileGroup.Hud: return "界面/HUD";
                case FileGroup.Character: return "角色设置";
                case FileGroup.Hotbar: return "热键/键位";
                case FileGroup.Macro: return "用户宏";
                case FileGroup.Portrait: return "套装/肖像";
                case FileGroup.Inventory: return "物品/雇员";
                case FileGroup.ChatSettings: return "消息窗口设置";
                case FileGroup.ChatHistory: return "聊天记录/悄悄话";
                case FileGroup.GoldSaucer: return "九宫幻卡";
                default: return "其他文件";
            }
        }

        public static string ShortLabel(FileGroup group)
        {
            switch (group)
            {
                case FileGroup.Hud: return "HUD";
                case FileGroup.Character: return "角色";
                case FileGroup.Hotbar: return "热键";
                case FileGroup.Macro: return "宏";
                case FileGroup.Portrait: return "肖像";
                case FileGroup.Inventory: return "物品";
                case FileGroup.ChatSettings: return "消息";
                case FileGroup.ChatHistory: return "记录";
                case FileGroup.GoldSaucer: return "幻卡";
                default: return "其他";
            }
        }

        public static string Description(FileGroup group)
        {
            switch (group)
            {
                case FileGroup.Hud: return "ADDON.DAT";
                case FileGroup.Character: return "COMMON.DAT / CONTROL0.DAT / CONTROL1.DAT";
                case FileGroup.Hotbar: return "HOTBAR.DAT / KEYBIND.DAT";
                case FileGroup.Macro: return "MACRO.DAT";
                case FileGroup.Portrait: return "GEARSET.DAT + UISAVE.DAT（肖像需成对）";
                case FileGroup.Inventory: return "ITEMFDR.DAT / ITEMODR.DAT";
                case FileGroup.ChatSettings: return "LOGFLTR.DAT";
                case FileGroup.ChatHistory: return "log/ 与 ACQ.DAT（默认不导出）";
                case FileGroup.GoldSaucer: return "GS.DAT";
                default: return "未识别文件（游戏更新后可能出现）";
            }
        }

        public static FileGroup Classify(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath)) return FileGroup.Other;
            string normalized = relativePath.Replace('\\', '/');
            if (normalized.StartsWith("log/", StringComparison.OrdinalIgnoreCase))
                return FileGroup.ChatHistory;
            string fileName = Path.GetFileName(normalized);
            FileGroup group;
            if (KnownFiles.TryGetValue(fileName, out group)) return group;
            return FileGroup.Other;
        }

        public static bool IsDefaultOn(FileGroup group)
        {
            return group != FileGroup.ChatHistory && group != FileGroup.Other
                ? DefaultOn.Contains(group)
                : group == FileGroup.Other;
        }

        public static bool IsPortraitFile(string relativePath)
        {
            string name = Path.GetFileName(relativePath ?? string.Empty);
            return string.Equals(name, "GEARSET.DAT", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "UISAVE.DAT", StringComparison.OrdinalIgnoreCase);
        }
    }
}

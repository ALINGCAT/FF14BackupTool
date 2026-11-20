using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;

namespace FF14BackupTool.Core
{
    [DataContract]
    public sealed class CreatedByInfo
    {
        [DataMember(Name = "app")]
        public string App { get; set; }

        [DataMember(Name = "version")]
        public string Version { get; set; }
    }

    [DataContract]
    public sealed class ManifestFile
    {
        [DataMember(Name = "path")]
        public string Path { get; set; }

        [DataMember(Name = "size")]
        public long Size { get; set; }

        [DataMember(Name = "sha256")]
        public string Sha256 { get; set; }

        [DataMember(Name = "mtime")]
        public string Mtime { get; set; }

        [DataMember(Name = "group")]
        public string Group { get; set; }
    }

    [DataContract]
    public sealed class ManifestCharacter
    {
        [DataMember(Name = "folder")]
        public string Folder { get; set; }

        [DataMember(Name = "alias")]
        public string Alias { get; set; }

        [DataMember(Name = "detectedName")]
        public string DetectedName { get; set; }

        [DataMember(Name = "job")]
        public string Job { get; set; }

        [DataMember(Name = "nameSource")]
        public string NameSource { get; set; }

        [DataMember(Name = "lastActiveAt")]
        public string LastActiveAt { get; set; }

        [DataMember(Name = "files")]
        public List<ManifestFile> Files { get; set; } = new List<ManifestFile>();

        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Alias)) return Alias;
                if (!string.IsNullOrWhiteSpace(DetectedName)) return DetectedName;
                return Folder;
            }
        }
    }

    [DataContract]
    public sealed class ManifestSystemSettings
    {
        [DataMember(Name = "included")]
        public bool Included { get; set; }

        [DataMember(Name = "archivePath")]
        public string ArchivePath { get; set; }

        [DataMember(Name = "sections")]
        public List<string> Sections { get; set; } = new List<string>();
    }

    [DataContract]
    public sealed class ManifestSharedMacro
    {
        [DataMember(Name = "included")]
        public bool Included { get; set; }

        [DataMember(Name = "archivePath")]
        public string ArchivePath { get; set; }

        [DataMember(Name = "size")]
        public long Size { get; set; }

        [DataMember(Name = "sha256")]
        public string Sha256 { get; set; }

        [DataMember(Name = "mtime")]
        public string Mtime { get; set; }
    }

    [DataContract]
    public sealed class ManifestTotals
    {
        [DataMember(Name = "files")]
        public int Files { get; set; }

        [DataMember(Name = "bytes")]
        public long Bytes { get; set; }
    }

    [DataContract]
    public sealed class FfbakManifest
    {
        public const string SchemaName = "ff14-backup";
        public const int CurrentFormatVersion = 1;

        [DataMember(Name = "schema")]
        public string Schema { get; set; }

        [DataMember(Name = "formatVersion")]
        public int FormatVersion { get; set; }

        [DataMember(Name = "createdAt")]
        public string CreatedAt { get; set; }

        [DataMember(Name = "createdBy")]
        public CreatedByInfo CreatedBy { get; set; }

        [DataMember(Name = "client")]
        public string Client { get; set; }

        [DataMember(Name = "characters")]
        public List<ManifestCharacter> Characters { get; set; } = new List<ManifestCharacter>();

        [DataMember(Name = "systemSettings")]
        public ManifestSystemSettings SystemSettings { get; set; }

        [DataMember(Name = "sharedMacro")]
        public ManifestSharedMacro SharedMacro { get; set; }

        [DataMember(Name = "totals")]
        public ManifestTotals Totals { get; set; }

        public void Validate()
        {
            if (!string.Equals(Schema, SchemaName, StringComparison.Ordinal))
                throw new InvalidDataException("这不是有效的 FF14 备份文件（schema 不匹配）。");
            if (FormatVersion <= 0 || FormatVersion > CurrentFormatVersion)
                throw new InvalidDataException("备份格式版本不受支持：" + FormatVersion + "。请升级工具。");

            if (Characters == null) Characters = new List<ManifestCharacter>();
            if (Characters.Count > 1000) throw new InvalidDataException("备份中的角色数量异常。");

            var folderSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int fileCount = 0;
            long totalBytes = 0;

            foreach (var character in Characters)
            {
                if (character == null) throw new InvalidDataException("备份中的角色信息为空。");
                if (!PathGuard.IsValidCharacterId(character.Folder))
                    throw new InvalidDataException("备份中的角色文件夹名不合法：" + character.Folder);
                if (!folderSet.Add(character.Folder))
                    throw new InvalidDataException("备份中存在重复角色：" + character.Folder);

                if (character.Files == null) character.Files = new List<ManifestFile>();
                if (character.Files.Count > 100000) throw new InvalidDataException("单个角色的文件数量异常。");
                var pathSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in character.Files)
                {
                    if (file == null) throw new InvalidDataException("备份中的文件信息为空。");
                    string normalized = PathGuard.NormalizeRelativePath(file.Path);
                    if (!string.Equals(normalized, file.Path, StringComparison.Ordinal))
                        throw new InvalidDataException("备份中的文件路径未规范化：" + file.Path);
                    if (!pathSet.Add(normalized))
                        throw new InvalidDataException("角色 " + character.Folder + " 中存在重复文件：" + normalized);
                    if (file.Size < 0) throw new InvalidDataException("文件大小异常：" + normalized);
                    if (string.IsNullOrWhiteSpace(file.Sha256) || !Regex.IsMatch(file.Sha256, "^[0-9A-Fa-f]{64}$"))
                        throw new InvalidDataException("文件哈希异常：" + normalized);

                    fileCount++;
                    totalBytes += file.Size;
                }
            }

            if (SystemSettings != null && SystemSettings.Included)
            {
                if (string.IsNullOrWhiteSpace(SystemSettings.ArchivePath))
                    throw new InvalidDataException("系统设置归档路径为空。");
                PathGuard.NormalizeRelativePath(SystemSettings.ArchivePath);
            }

            if (SharedMacro != null && SharedMacro.Included)
            {
                if (string.IsNullOrWhiteSpace(SharedMacro.ArchivePath))
                    throw new InvalidDataException("公用宏归档路径为空。");
                PathGuard.NormalizeRelativePath(SharedMacro.ArchivePath);
                if (SharedMacro.Size < 0)
                    throw new InvalidDataException("公用宏文件大小异常。");
                if (string.IsNullOrWhiteSpace(SharedMacro.Sha256) ||
                    !Regex.IsMatch(SharedMacro.Sha256, "^[0-9A-Fa-f]{64}$"))
                    throw new InvalidDataException("公用宏文件哈希异常。");
                fileCount++;
                totalBytes += SharedMacro.Size;
            }

            if (fileCount == 0 && (SystemSettings == null || !SystemSettings.Included) &&
                (SharedMacro == null || !SharedMacro.Included))
                throw new InvalidDataException("备份中没有任何文件。");

            if (Totals != null)
            {
                if (Totals.Files != 0 && Totals.Files != fileCount)
                    throw new InvalidDataException("备份文件数量与清单不一致。");
                if (Totals.Bytes != 0 && Totals.Bytes != totalBytes)
                    throw new InvalidDataException("备份文件总大小与清单不一致。");
            }
        }
    }

    public sealed class BackupWriteResult
    {
        public string Path { get; set; }
        public long Size { get; set; }
        public int FileCount { get; set; }
        public int CharacterCount { get; set; }
    }

    public sealed class BackupInspection
    {
        public string Path { get; set; }
        public bool IsDirectBackup { get; set; }
        public FfbakManifest Manifest { get; set; }
        public List<NestedBackupCandidate> NestedCandidates { get; set; } = new List<NestedBackupCandidate>();
        public string Message { get; set; }
    }

    public sealed class NestedBackupCandidate
    {
        public string EntryName { get; set; }
        public long Size { get; set; }

        public override string ToString()
        {
            string size = Size < 1024 ? Size + " B"
                : Size < 1024L * 1024 ? (Size / 1024.0).ToString("0.0") + " KB"
                : (Size / (1024.0 * 1024)).ToString("0.0") + " MB";
            return EntryName + "  (" + size + ")";
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace FF14BackupTool.Core
{
    public static class PathGuard
    {
        private static readonly Regex CharacterFolderRegex =
            new Regex("^FFXIV_CHR[0-9A-Fa-f]{16}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly HashSet<string> ReservedNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "CON", "PRN", "AUX", "NUL",
                "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
                "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
            };

        public static bool IsValidCharacterId(string folderId)
        {
            return !string.IsNullOrWhiteSpace(folderId) && CharacterFolderRegex.IsMatch(folderId);
        }

        public static string NormalizeRelativePath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                throw new InvalidDataException("文件路径为空。");

            string normalized = relativePath.Replace('\\', '/').Trim();
            if (normalized.StartsWith("/", StringComparison.Ordinal) ||
                normalized.StartsWith("\\", StringComparison.Ordinal))
                throw new InvalidDataException("不允许绝对路径：" + relativePath);

            if (normalized.IndexOf(':') >= 0)
                throw new InvalidDataException("路径中不允许包含盘符或数据流：" + relativePath);

            string[] parts = normalized.Split('/');
            if (parts.Length == 0) throw new InvalidDataException("文件路径为空。");

            var clean = new List<string>();
            foreach (string rawPart in parts)
            {
                string part = rawPart.Trim();
                if (part.Length == 0 || part == ".")
                    throw new InvalidDataException("路径中包含空段：" + relativePath);
                if (part == "..")
                    throw new InvalidDataException("路径中不允许包含上级目录：" + relativePath);

                if (part.EndsWith(" ", StringComparison.Ordinal) ||
                    part.EndsWith(".", StringComparison.Ordinal))
                    throw new InvalidDataException("文件名不能以空格或句点结尾：" + relativePath);

                foreach (char c in part)
                {
                    if (c < 0x20 || "<>:\"|?*".IndexOf(c) >= 0)
                        throw new InvalidDataException("文件名包含非法字符：" + relativePath);
                }

                string nameWithoutExtension = Path.GetFileNameWithoutExtension(part);
                if (ReservedNames.Contains(part) || ReservedNames.Contains(nameWithoutExtension))
                    throw new InvalidDataException("文件名是 Windows 保留设备名：" + relativePath);

                clean.Add(part);
            }

            return string.Join("/", clean);
        }

        public static string CombineUnderRoot(string root, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(root))
                throw new ArgumentException("根目录为空。", "root");

            string normalized = NormalizeRelativePath(relativePath);
            string rootFull = Path.GetFullPath(root);
            if (!rootFull.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                rootFull += Path.DirectorySeparatorChar;

            string combined = Path.GetFullPath(Path.Combine(rootFull, normalized.Replace('/', Path.DirectorySeparatorChar)));
            if (!combined.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("路径越界：" + relativePath);

            return combined;
        }

        public static string GetArchivePath(string characterFolder, string relativePath)
        {
            if (!IsValidCharacterId(characterFolder))
                throw new InvalidDataException("角色文件夹名不合法：" + characterFolder);
            string normalized = NormalizeRelativePath(relativePath);
            return "payload/" + characterFolder + "/" + normalized;
        }

        public static string GetSystemArchivePath()
        {
            return "system/FFXIV.cfg.safe";
        }

        public static string GetSharedMacroArchivePath()
        {
            return "global/MACROSYS.DAT";
        }

        public static bool IsSafeArchiveEntry(string entryName)
        {
            if (string.IsNullOrWhiteSpace(entryName)) return false;
            try
            {
                NormalizeRelativePath(entryName);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static void EnsureDirectory(string path)
        {
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
        }

        public static bool IsUnderRoot(string path, string root)
        {
            try
            {
                string fullPath = Path.GetFullPath(path);
                string fullRoot = Path.GetFullPath(root);
                if (!fullRoot.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                    fullRoot += Path.DirectorySeparatorChar;
                return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public static string SafeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "backup";
            var invalid = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                builder.Append(invalid.Contains(c) ? '_' : c);
            }
            string result = builder.ToString().Trim().TrimEnd('.');
            if (result.Length == 0) result = "backup";
            return result;
        }
    }
}

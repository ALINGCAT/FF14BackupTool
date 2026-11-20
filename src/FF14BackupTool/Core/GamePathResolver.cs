using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FF14BackupTool.Core
{
    public static class GamePathResolver
    {
        public const string ConfigFolderName = "FINAL FANTASY XIV - A Realm Reborn";

        public static ConfigLocation Resolve(string selectedPath, bool allowDocumentsFallback = true)
        {
            var candidates = new List<string>();

            string normalizedSelected = NormalizeSelectedPath(selectedPath);

            if (!string.IsNullOrWhiteSpace(normalizedSelected))
            {
                if (PathGuard.IsValidCharacterId(Path.GetFileName(normalizedSelected)))
                {
                    string parent = Path.GetDirectoryName(normalizedSelected);
                    if (!string.IsNullOrWhiteSpace(parent)) candidates.Add(parent);
                }

                candidates.Add(normalizedSelected);
                candidates.Add(Path.Combine(normalizedSelected, "game", "My Games", ConfigFolderName));
                candidates.Add(Path.Combine(normalizedSelected, "My Games", ConfigFolderName));

                if (string.Equals(Path.GetFileName(normalizedSelected), "game", StringComparison.OrdinalIgnoreCase))
                {
                    candidates.Add(Path.Combine(normalizedSelected, "My Games", ConfigFolderName));
                }
                if (string.Equals(Path.GetFileName(normalizedSelected), "My Games", StringComparison.OrdinalIgnoreCase))
                {
                    candidates.Add(Path.Combine(normalizedSelected, ConfigFolderName));
                }
            }

            if (allowDocumentsFallback)
            {
                foreach (string docsCandidate in GetDocumentsCandidates())
                {
                    candidates.Add(docsCandidate);
                }
            }

            var distinct = candidates
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Select(SafeFullPath)
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (string candidate in distinct)
            {
                if (Directory.Exists(candidate) && IsConfigRoot(candidate))
                {
                    return BuildLocation(selectedPath, candidate, true);
                }
            }

            string expected = BuildExpectedPath(normalizedSelected, allowDocumentsFallback);
            bool canCreate = !string.IsNullOrWhiteSpace(expected)
                && Directory.Exists(Path.GetDirectoryName(expected))
                && LooksLikeGameRoot(normalizedSelected);

            return new ConfigLocation
            {
                SelectedPath = selectedPath,
                ConfigRoot = expected,
                ExpectedPath = expected,
                GameRoot = TryGetGameRoot(expected),
                Client = DetectClient(expected),
                Exists = false,
                IsWritable = false,
                CanCreate = canCreate
            };
        }

        public static ConfigLocation CreateConfigRoot(ConfigLocation location)
        {
            if (location == null || string.IsNullOrWhiteSpace(location.ConfigRoot))
                throw new InvalidOperationException("没有可创建的配置目录。");

            Directory.CreateDirectory(location.ConfigRoot);
            var resolved = Resolve(location.ConfigRoot, false);
            if (!resolved.Exists)
            {
                resolved.Exists = Directory.Exists(location.ConfigRoot);
                resolved.ConfigRoot = location.ConfigRoot;
                resolved.GameRoot = TryGetGameRoot(location.ConfigRoot);
                resolved.Client = DetectClient(location.ConfigRoot);
                resolved.IsWritable = TestWritable(location.ConfigRoot);
            }
            return resolved;
        }

        public static bool IsConfigRoot(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return false;
                if (File.Exists(Path.Combine(path, "FFXIV.cfg")) ||
                    File.Exists(Path.Combine(path, "FFXIV_BOOT.cfg")))
                    return true;

                foreach (string dir in Directory.EnumerateDirectories(path, "FFXIV_CHR*", SearchOption.TopDirectoryOnly))
                {
                    if (PathGuard.IsValidCharacterId(Path.GetFileName(dir))) return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        public static bool LooksLikeGameRoot(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return false;
                if (Directory.Exists(Path.Combine(path, "game"))) return true;
                if (File.Exists(Path.Combine(path, "ffxiv_dx11.exe")) ||
                    File.Exists(Path.Combine(path, "ffxiv.exe")))
                    return true;
                string game = Path.Combine(path, "game");
                if (Directory.Exists(game) &&
                    (File.Exists(Path.Combine(game, "ffxiv_dx11.exe")) ||
                     File.Exists(Path.Combine(game, "ffxiv.exe"))))
                    return true;
                return false;
            }
            catch
            {
                return false;
            }
        }

        public static bool TestWritable(string configRoot)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(configRoot) || !Directory.Exists(configRoot)) return false;
                string probe = Path.Combine(configRoot, ".ff14_backup_write_test_" + Guid.NewGuid().ToString("N") + ".tmp");
                using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.WriteByte(0);
                }
                File.Delete(probe);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static string TryGetGameRoot(string configRoot)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(configRoot)) return null;
                string marker = Path.DirectorySeparatorChar + "game" + Path.DirectorySeparatorChar + "My Games" + Path.DirectorySeparatorChar;
                int index = configRoot.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (index > 0)
                {
                    return configRoot.Substring(0, index);
                }
            }
            catch
            {
                // ignore
            }
            return null;
        }

        public static string DetectClient(string configRoot)
        {
            if (string.IsNullOrWhiteSpace(configRoot)) return "unknown";
            if (configRoot.IndexOf(Path.DirectorySeparatorChar + "game" + Path.DirectorySeparatorChar + "My Games" + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) >= 0)
                return "cn";
            return "global";
        }

        private static ConfigLocation BuildLocation(string selectedPath, string configRoot, bool exists)
        {
            return new ConfigLocation
            {
                SelectedPath = selectedPath,
                ConfigRoot = configRoot,
                ExpectedPath = configRoot,
                GameRoot = TryGetGameRoot(configRoot),
                Client = DetectClient(configRoot),
                Exists = exists,
                IsWritable = exists && TestWritable(configRoot),
                CanCreate = false
            };
        }

        private static string BuildExpectedPath(string selectedPath, bool allowDocumentsFallback)
        {
            if (!string.IsNullOrWhiteSpace(selectedPath))
            {
                string name = Path.GetFileName(selectedPath);
                if (string.Equals(name, ConfigFolderName, StringComparison.OrdinalIgnoreCase))
                    return selectedPath;

                if (string.Equals(name, "game", StringComparison.OrdinalIgnoreCase))
                    return Path.Combine(selectedPath, "My Games", ConfigFolderName);

                if (string.Equals(name, "My Games", StringComparison.OrdinalIgnoreCase))
                    return Path.Combine(selectedPath, ConfigFolderName);

                if (Directory.Exists(Path.Combine(selectedPath, "game")))
                    return Path.Combine(selectedPath, "game", "My Games", ConfigFolderName);

                return Path.Combine(selectedPath, "My Games", ConfigFolderName);
            }

            return GetDocumentsCandidates().FirstOrDefault();
        }

        private static IEnumerable<string> GetDocumentsCandidates()
        {
            var results = new List<string>();
            try
            {
                string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                if (!string.IsNullOrWhiteSpace(documents))
                    results.Add(Path.Combine(documents, "My Games", ConfigFolderName));
            }
            catch
            {
                // ignore
            }

            try
            {
                string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrWhiteSpace(profile))
                {
                    results.Add(Path.Combine(profile, "Documents", "My Games", ConfigFolderName));
                    results.Add(Path.Combine(profile, "My Documents", "My Games", ConfigFolderName));
                }
            }
            catch
            {
                // ignore
            }

            return results.Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static string NormalizeSelectedPath(string selectedPath)
        {
            if (string.IsNullOrWhiteSpace(selectedPath)) return null;
            try
            {
                string full = Path.GetFullPath(selectedPath.Trim().Trim('"'));
                if (File.Exists(full)) full = Path.GetDirectoryName(full);
                return full;
            }
            catch
            {
                return null;
            }
        }

        private static string SafeFullPath(string path)
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return null;
            }
        }
    }
}

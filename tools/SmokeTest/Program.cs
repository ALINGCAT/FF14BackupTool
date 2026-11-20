using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using FF14BackupTool.Core;

namespace FF14BackupTool.Smoke
{
    internal static class Program
    {
        private static int _failures;

        private static void Main()
        {
            string root = Path.Combine(Path.GetTempPath(), "FF14BackupTool.Smoke", Guid.NewGuid().ToString("N"));
            string snapshot = null;
            Directory.CreateDirectory(root);
            try
            {
                string source = Path.Combine(root, "source", "game", "My Games", "FINAL FANTASY XIV - A Realm Reborn");
                string target = Path.Combine(root, "target", "game", "My Games", "FINAL FANTASY XIV - A Realm Reborn");
                Directory.CreateDirectory(source);
                Directory.CreateDirectory(target);

                string sourceId = "FFXIV_CHR0011223344556677";
                string targetId = "FFXIV_CHR8899AABBCCDDEEFF";
                string sourceChar = Path.Combine(source, sourceId);
                Directory.CreateDirectory(sourceChar);
                Directory.CreateDirectory(Path.Combine(sourceChar, "log"));
                File.WriteAllText(Path.Combine(sourceChar, "ADDON.DAT"), "hud");
                File.WriteAllText(Path.Combine(sourceChar, "COMMON.DAT"), "common");
                File.WriteAllText(Path.Combine(sourceChar, "CONTROL0.DAT"), "control0");
                File.WriteAllText(Path.Combine(sourceChar, "CONTROL1.DAT"), "control1");
                File.WriteAllText(Path.Combine(sourceChar, "GEARSET.DAT"), "gearset");
                File.WriteAllText(Path.Combine(sourceChar, "UISAVE.DAT"), "uisave");
                File.WriteAllText(Path.Combine(sourceChar, "LOGFLTR.DAT"), "filters");
                File.WriteAllText(Path.Combine(sourceChar, "ACQ.DAT"), "acq");
                File.WriteAllBytes(Path.Combine(sourceChar, "log", "00000000.log"), BuildLogBytes());
                File.WriteAllText(Path.Combine(source, "FFXIV.cfg"), BuildCfg("1920", "100"));
                File.WriteAllText(Path.Combine(source, "MACROSYS.DAT"), "shared macros");
                File.WriteAllText(Path.Combine(target, "FFXIV.cfg"), BuildCfg("2560", "50"));
                File.WriteAllText(Path.Combine(target, "MACROSYS.DAT"), "old shared macros");

                var location = GamePathResolver.Resolve(source, false);
                Check(location.Exists, "path resolver finds CN config");
                Check(location.Client == "cn", "client is cn");

                var characters = CharacterScanner.Scan(source, true);
                Check(characters.Count == 1, "scanner finds one character");
                Check(characters[0].DetectedName == "玩家一号", "log name resolver");
                Check(characters[0].Job == "白魔法师", "log job resolver");

                var selection = new BackupSelection
                {
                    IncludeSystemSettings = true,
                    IncludeSharedMacro = true
                };
                selection.Characters.Add(new CharacterSelection
                {
                    Character = characters[0],
                    Groups = new HashSet<FileGroup>(FileGroups.All)
                });

                string ffbak = Path.Combine(root, "backup.ffbak");
                var write = FfbakWriter.Write(source, selection, ffbak);
                Check(File.Exists(ffbak), "ffbak created");
                Check(write.FileCount >= 8, "file count");

                var inspection = FfbakReader.Inspect(ffbak);
                Check(inspection.IsDirectBackup, "inspect direct");
                Check(inspection.Manifest.Characters.Count == 1, "manifest character count");
                Check(inspection.Manifest.SystemSettings != null && inspection.Manifest.SystemSettings.Included,
                    "system settings included");
                Check(inspection.Manifest.SharedMacro != null && inspection.Manifest.SharedMacro.Included,
                    "shared macro included");

                string targetChar = Path.Combine(target, targetId);
                Directory.CreateDirectory(targetChar);
                File.WriteAllText(Path.Combine(targetChar, "ADDON.DAT"), "old hud");
                File.WriteAllText(Path.Combine(targetChar, "UISAVE.DAT"), "old uisave");

                snapshot = RestoreSnapshotStore.Create(target, new[] { targetId });
                Check(File.Exists(snapshot), "restore snapshot created");

                var mappings = new List<RestoreMapping>
                {
                    new RestoreMapping { SourceFolderId = sourceId, TargetFolderId = targetId }
                };
                var restored = RestoreService.Restore(ffbak, target, mappings, new RestoreOptions
                {
                    IncludeSystemSettings = true,
                    IncludeCrossCharacterUiSave = false,
                    IncludeCrossCharacterChatHistory = false
                });
                Check(restored.RestoredFiles > 0, "restore files");
                Check(File.Exists(Path.Combine(targetChar, "ADDON.DAT")), "cross restore addon");
                Check(File.ReadAllText(Path.Combine(targetChar, "UISAVE.DAT")) == "old uisave",
                    "cross restore skips uisave");
                Check(File.ReadAllText(Path.Combine(target, "MACROSYS.DAT")) == "shared macros",
                    "shared macro restored");
                Check(!Directory.Exists(Path.Combine(targetChar, "log")), "cross restore skips log");
                Check(Directory.Exists(restored.PreRestoreBackupPath), "pre restore backup exists");

                string targetCfg = File.ReadAllText(Path.Combine(target, "FFXIV.cfg"));
                Check(targetCfg.Contains("ScreenWidth\t2560") || targetCfg.Contains("ScreenWidth 2560"),
                    "display settings preserved");
                Check(targetCfg.Contains("SoundMaster\t80") || targetCfg.Contains("SoundMaster 80") ||
                      targetCfg.Contains("SoundMaster\t100") || targetCfg.Contains("SoundMaster 100"),
                    "system settings merged");

                File.WriteAllText(Path.Combine(targetChar, "ADDON.DAT"), "changed after restore");
                File.WriteAllText(Path.Combine(target, "FFXIV.cfg"), BuildCfg("3840", "10"));
                File.WriteAllText(Path.Combine(target, "MACROSYS.DAT"), "changed shared macros");
                RestoreSnapshotStore.Restore(snapshot, target);
                Check(File.ReadAllText(Path.Combine(targetChar, "ADDON.DAT")) == "old hud",
                    "restore snapshot restores original character file");
                Check(File.ReadAllText(Path.Combine(target, "FFXIV.cfg")).Contains("ScreenWidth\t2560") ||
                      File.ReadAllText(Path.Combine(target, "FFXIV.cfg")).Contains("ScreenWidth 2560"),
                    "restore snapshot restores original system settings");
                Check(File.ReadAllText(Path.Combine(target, "MACROSYS.DAT")) == "old shared macros",
                    "restore snapshot restores original shared macro");

                string package = Path.Combine(root, "portable.zip");
                string exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FF14BackupTool.exe");
                if (!File.Exists(exe))
                {
                    // The app project output is next to the smoke test output; find it in the source tree.
                    exe = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                        "..", "..", "..", "..", "..", "src", "FF14BackupTool", "bin", "Release", "net48", "FF14BackupTool.exe"));
                }
                var portable = PortablePackageWriter.Create(ffbak, exe, package);
                Check(File.Exists(portable.Path), "portable package created");
                var packageInspection = FfbakReader.Inspect(package);
                Check(!packageInspection.IsDirectBackup, "portable package detected");
                Check(packageInspection.NestedCandidates.Count == 1, "nested ffbak found");
                string extracted = FfbakReader.ExtractNestedBackup(package,
                    packageInspection.NestedCandidates[0].EntryName,
                    Path.Combine(root, "extracted"));
                var nestedInspection = FfbakReader.Inspect(extracted);
                Check(nestedInspection.Manifest.Characters.Count == 1, "nested manifest readable");

                if (_failures == 0)
                {
                    Console.WriteLine("SMOKE OK");
                }
                else
                {
                    Console.WriteLine("SMOKE FAILURES: " + _failures);
                    Environment.ExitCode = 1;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("SMOKE EXCEPTION: " + ex);
                Environment.ExitCode = 2;
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
                try { if (!string.IsNullOrWhiteSpace(snapshot) && File.Exists(snapshot)) File.Delete(snapshot); } catch { }
            }
        }

        private static void Check(bool condition, string message)
        {
            if (condition)
            {
                Console.WriteLine("PASS " + message);
            }
            else
            {
                _failures++;
                Console.WriteLine("FAIL " + message);
            }
        }

        private static string BuildCfg(string width, string master)
        {
            return "<FINAL FANTASY XIV Config File>\r\n\r\n" +
                   "<Version>\r\nGuidVersion\t538050324\r\nConfigVersion\t2\r\nLanguage\t1\r\nRegion\t2\r\n\r\n" +
                   "<Display Settings>\r\nMainAdapter\t\r\nScreenLeft\t0\r\nScreenTop\t0\r\n" +
                   "ScreenWidth\t" + width + "\r\nScreenHeight\t1080\r\nScreenMode\t2\r\n\r\n" +
                   "<Graphics Settings DX11>\r\nSSAO_DX11\t3\r\n\r\n" +
                   "<Sound Settings>\r\nIsSoundDisable\t0\r\n\r\n" +
                   "<SoundPlay Settings>\r\nSoundMaster\t" + master + "\r\nSoundBgm\t100\r\n\r\n" +
                   "<Network Settings>\r\nPort\t55296\r\nWorldId\t64\r\n\r\n" +
                   "<CUSTOM CONFIGURATION>\r\n\r\n";
        }

        private static byte[] BuildLogBytes()
        {
            var entries = new List<byte[]>();
            entries.Add(BuildEntry(100, 57, "玩家一号的职业转换成了“白魔法师”。"));
            entries.Add(BuildEntry(101, 15, "队伍消息"));
            int headerLength = 8 + entries.Count * 4;
            int total = headerLength + entries.Sum(e => e.Length);
            byte[] file = new byte[total];
            BitConverter.GetBytes(100u).CopyTo(file, 0);
            BitConverter.GetBytes((uint)(100 + entries.Count)).CopyTo(file, 4);
            int offset = 0;
            for (int i = 0; i < entries.Count; i++)
            {
                offset += entries[i].Length;
                BitConverter.GetBytes((uint)offset).CopyTo(file, 8 + i * 4);
            }
            int pos = headerLength;
            foreach (var entry in entries)
            {
                entry.CopyTo(file, pos);
                pos += entry.Length;
            }
            return file;
        }

        private static byte[] BuildEntry(uint timestamp, uint kind, string body)
        {
            byte[] sender = new byte[0];
            byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
            byte[] payload = new byte[1 + sender.Length + 1 + bodyBytes.Length];
            payload[0] = 0x1F;
            payload[1 + sender.Length] = 0x1F;
            bodyBytes.CopyTo(payload, 1 + sender.Length + 1);
            byte[] entry = new byte[8 + payload.Length];
            BitConverter.GetBytes(timestamp).CopyTo(entry, 0);
            BitConverter.GetBytes(kind).CopyTo(entry, 4);
            payload.CopyTo(entry, 8);
            return entry;
        }
    }
}

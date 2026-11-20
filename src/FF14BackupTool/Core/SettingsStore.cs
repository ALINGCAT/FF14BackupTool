using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;

namespace FF14BackupTool.Core
{
    [DataContract]
    public sealed class AliasEntry
    {
        [DataMember(Name = "id")]
        public string FolderId { get; set; }

        [DataMember(Name = "name")]
        public string Name { get; set; }
    }

    [DataContract]
    public sealed class AppSettings
    {
        [DataMember(Name = "version")]
        public int Version { get; set; } = 1;

        [DataMember(Name = "aliases")]
        public List<AliasEntry> Aliases { get; set; } = new List<AliasEntry>();

        [DataMember(Name = "lastGamePath")]
        public string LastGamePath { get; set; }

        [DataMember(Name = "lastBackupDirectory")]
        public string LastBackupDirectory { get; set; }

        [DataMember(Name = "lastArchivePath")]
        public string LastArchivePath { get; set; }

        [DataMember(Name = "lastTargetGamePath")]
        public string LastTargetGamePath { get; set; }

        [DataMember(Name = "theme")]
        public string Theme { get; set; } = "system";
    }

    public sealed class SettingsStore
    {
        private static readonly object Sync = new object();
        private static SettingsStore _current;
        private readonly string _settingsPath;
        private readonly string _dataDirectory;

        private SettingsStore()
        {
            _dataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "FF14BackupTool");
            Directory.CreateDirectory(_dataDirectory);
            _settingsPath = Path.Combine(_dataDirectory, "settings.json");
            Settings = Load();
        }

        public static SettingsStore Current
        {
            get
            {
                lock (Sync)
                {
                    return _current ?? (_current = new SettingsStore());
                }
            }
        }

        public AppSettings Settings { get; private set; }

        public string DataDirectory
        {
            get { return _dataDirectory; }
        }

        public string GetAlias(string folderId)
        {
            if (string.IsNullOrWhiteSpace(folderId)) return null;
            var entry = Settings.Aliases.FirstOrDefault(a =>
                string.Equals(a.FolderId, folderId, StringComparison.OrdinalIgnoreCase));
            return entry == null ? null : entry.Name;
        }

        public void SetAlias(string folderId, string name)
        {
            if (string.IsNullOrWhiteSpace(folderId)) return;
            var entry = Settings.Aliases.FirstOrDefault(a =>
                string.Equals(a.FolderId, folderId, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(name))
            {
                if (entry != null) Settings.Aliases.Remove(entry);
            }
            else
            {
                if (entry == null)
                {
                    Settings.Aliases.Add(new AliasEntry { FolderId = folderId, Name = name.Trim() });
                }
                else
                {
                    entry.Name = name.Trim();
                }
            }
            Save();
        }

        public void Save()
        {
            lock (Sync)
            {
                try
                {
                    string temp = _settingsPath + ".tmp";
                    File.WriteAllText(temp, JsonHelper.Serialize(Settings), new UTF8Encoding(false));
                    if (File.Exists(_settingsPath))
                    {
                        File.Replace(temp, _settingsPath, null);
                    }
                    else
                    {
                        File.Move(temp, _settingsPath);
                    }
                }
                catch
                {
                    // Settings must never crash the app.
                }
            }
        }

        private AppSettings Load()
        {
            try
            {
                if (!File.Exists(_settingsPath)) return new AppSettings();
                string json = File.ReadAllText(_settingsPath, Encoding.UTF8);
                var settings = JsonHelper.Deserialize<AppSettings>(json);
                if (settings == null) return new AppSettings();
                if (settings.Aliases == null) settings.Aliases = new List<AliasEntry>();
                return settings;
            }
            catch
            {
                try
                {
                    string backup = _settingsPath + ".corrupt_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    if (File.Exists(_settingsPath)) File.Copy(_settingsPath, backup, true);
                }
                catch
                {
                    // ignore
                }
                return new AppSettings();
            }
        }
    }
}

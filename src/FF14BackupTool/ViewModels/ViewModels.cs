using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using FF14BackupTool.Core;

namespace FF14BackupTool.ViewModels
{
    public sealed class BackupItem : INotifyPropertyChanged
    {
        private bool _isSelected = true;
        private bool _includeHud;
        private bool _includeCharacter;
        private bool _includeHotbar;
        private bool _includeMacro;
        private bool _includePortrait;
        private bool _includeInventory;
        private bool _includeChatSettings;
        private bool _includeChatHistory;
        private bool _includeGoldSaucer;
        private bool _includeOther;

        public BackupItem(CharacterInfo character)
        {
            Character = character ?? throw new ArgumentNullException("character");
            SetDefaults();
        }

        public CharacterInfo Character { get; private set; }
        public string DisplayName { get { return Character.DisplayName; } }
        public string DetailLine { get { return Character.DetailLine; } }
        public string LastActiveText
        {
            get
            {
                if (Character.LastActiveUtc == default(DateTime)) return "-";
                return Character.LastActiveUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            }
        }
        public string SizeText { get { return FormatSize(Character.TotalSize); } }
        public string Alias { get { return Character.Alias; } }

        public bool IsSelected
        {
            get { return _isSelected; }
            set { Set(ref _isSelected, value); }
        }

        public bool IncludeHud { get { return _includeHud; } set { Set(ref _includeHud, value); } }
        public bool IncludeCharacter { get { return _includeCharacter; } set { Set(ref _includeCharacter, value); } }
        public bool IncludeHotbar { get { return _includeHotbar; } set { Set(ref _includeHotbar, value); } }
        public bool IncludeMacro { get { return _includeMacro; } set { Set(ref _includeMacro, value); } }
        public bool IncludePortrait { get { return _includePortrait; } set { Set(ref _includePortrait, value); } }
        public bool IncludeInventory { get { return _includeInventory; } set { Set(ref _includeInventory, value); } }
        public bool IncludeChatSettings { get { return _includeChatSettings; } set { Set(ref _includeChatSettings, value); } }
        public bool IncludeChatHistory { get { return _includeChatHistory; } set { Set(ref _includeChatHistory, value); } }
        public bool IncludeGoldSaucer { get { return _includeGoldSaucer; } set { Set(ref _includeGoldSaucer, value); } }
        public bool IncludeOther { get { return _includeOther; } set { Set(ref _includeOther, value); } }

        public HashSet<FileGroup> SelectedGroups()
        {
            var groups = new HashSet<FileGroup>();
            if (IncludeHud) groups.Add(FileGroup.Hud);
            if (IncludeCharacter) groups.Add(FileGroup.Character);
            if (IncludeHotbar) groups.Add(FileGroup.Hotbar);
            if (IncludeMacro) groups.Add(FileGroup.Macro);
            if (IncludePortrait) groups.Add(FileGroup.Portrait);
            if (IncludeInventory) groups.Add(FileGroup.Inventory);
            if (IncludeChatSettings) groups.Add(FileGroup.ChatSettings);
            if (IncludeChatHistory) groups.Add(FileGroup.ChatHistory);
            if (IncludeGoldSaucer) groups.Add(FileGroup.GoldSaucer);
            if (IncludeOther) groups.Add(FileGroup.Other);
            return groups;
        }

        public void SetGroup(FileGroup group, bool value)
        {
            switch (group)
            {
                case FileGroup.Hud: IncludeHud = value; break;
                case FileGroup.Character: IncludeCharacter = value; break;
                case FileGroup.Hotbar: IncludeHotbar = value; break;
                case FileGroup.Macro: IncludeMacro = value; break;
                case FileGroup.Portrait: IncludePortrait = value; break;
                case FileGroup.Inventory: IncludeInventory = value; break;
                case FileGroup.ChatSettings: IncludeChatSettings = value; break;
                case FileGroup.ChatHistory: IncludeChatHistory = value; break;
                case FileGroup.GoldSaucer: IncludeGoldSaucer = value; break;
                case FileGroup.Other: IncludeOther = value; break;
            }
        }

        public bool HasGroup(FileGroup group)
        {
            return Character.Files != null && Character.Files.Any(f => f.Group == group);
        }

        public void SetDefaults()
        {
            foreach (var group in FileGroups.All)
            {
                SetGroup(group, HasGroup(group) && FileGroups.IsDefaultOn(group));
            }
        }

        public void RefreshAlias()
        {
            Character.Alias = SettingsStore.Current.GetAlias(Character.FolderId);
            Raise("DisplayName");
            Raise("Alias");
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            Raise(name);
        }

        private void Raise(string name)
        {
            var handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(name));
        }

        public static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024L * 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / (1024.0 * 1024)).ToString("0.0") + " MB";
            return (bytes / (1024.0 * 1024 * 1024)).ToString("0.00") + " GB";
        }
    }

    public sealed class TargetOption
    {
        public string FolderId { get; set; }
        public string DisplayName { get; set; }
        public string Detail { get; set; }
        public bool IsNew { get; set; }
        public bool IsManual { get; set; }

        public string Label
        {
            get
            {
                bool nameIsId = string.IsNullOrWhiteSpace(DisplayName) ||
                                string.Equals(DisplayName, FolderId, StringComparison.OrdinalIgnoreCase);
                if (IsNew)
                    return "新建 / 未登录：" + (nameIsId ? FolderId : DisplayName + "（" + FolderId + "）");
                return nameIsId ? FolderId : DisplayName + "（" + FolderId + "）";
            }
        }

        public override string ToString()
        {
            return Label;
        }
    }

    public sealed class RestoreMappingItem : INotifyPropertyChanged
    {
        private TargetOption _selectedTarget;
        private bool _isSelected = true;

        public RestoreMappingItem(ManifestCharacter source)
        {
            Source = source ?? throw new ArgumentNullException("source");
            TargetOptions = new System.Collections.ObjectModel.ObservableCollection<TargetOption>();
        }

        public ManifestCharacter Source { get; private set; }
        public System.Collections.ObjectModel.ObservableCollection<TargetOption> TargetOptions { get; private set; }
        public string SourceName { get { return Source.DisplayName; } }
        public string SourceDetail
        {
            get
            {
                var bits = new List<string>();
                if (!string.IsNullOrWhiteSpace(Source.Job)) bits.Add(Source.Job);
                bits.Add(Source.Folder);
                bits.Add(Source.Files.Count + " 个文件");
                return string.Join(" · ", bits);
            }
        }

        public bool IsSelected
        {
            get { return _isSelected; }
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                Raise("IsSelected");
                var handler = SelectionChanged;
                if (handler != null) handler(this, EventArgs.Empty);
            }
        }

        public TargetOption SelectedTarget
        {
            get { return _selectedTarget; }
            set
            {
                if (ReferenceEquals(_selectedTarget, value)) return;
                _selectedTarget = value;
                Raise("SelectedTarget");
                Raise("IsCrossCharacter");
                Raise("CrossBadge");
                var handler = SelectionChanged;
                if (handler != null) handler(this, EventArgs.Empty);
            }
        }

        public bool IsCrossCharacter
        {
            get
            {
                return SelectedTarget != null &&
                       !string.Equals(Source.Folder, SelectedTarget.FolderId, StringComparison.OrdinalIgnoreCase);
            }
        }

        public string CrossBadge { get { return IsCrossCharacter ? "跨角色" : "同角色"; } }

        public event EventHandler SelectionChanged;
        public event PropertyChangedEventHandler PropertyChanged;

        public void SetTargets(IEnumerable<TargetOption> options, string preferredFolderId)
        {
            TargetOptions.Clear();
            foreach (var option in options) TargetOptions.Add(option);
            TargetOption selected = null;
            if (!string.IsNullOrWhiteSpace(preferredFolderId))
                selected = TargetOptions.FirstOrDefault(o =>
                    string.Equals(o.FolderId, preferredFolderId, StringComparison.OrdinalIgnoreCase));
            if (selected == null)
                selected = TargetOptions.FirstOrDefault(o =>
                    string.Equals(o.FolderId, Source.Folder, StringComparison.OrdinalIgnoreCase));
            if (selected == null)
                selected = TargetOptions.FirstOrDefault(o => o.IsNew &&
                    string.Equals(o.FolderId, Source.Folder, StringComparison.OrdinalIgnoreCase));
            if (selected == null)
                selected = TargetOptions.FirstOrDefault();
            SelectedTarget = selected;
        }

        private void Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            Raise(name);
        }

        private void Raise(string name)
        {
            var handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(name));
        }
    }
}

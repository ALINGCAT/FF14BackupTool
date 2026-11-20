using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FF14BackupTool.Core;
using FF14BackupTool.Dialogs;
using FF14BackupTool.ViewModels;
using WinForms = System.Windows.Forms;

namespace FF14BackupTool
{
    public partial class MainWindow : Window
    {
        private readonly ObservableCollection<BackupItem> _backupItems = new ObservableCollection<BackupItem>();
        private readonly ObservableCollection<RestoreMappingItem> _restoreMappings = new ObservableCollection<RestoreMappingItem>();
        private readonly System.Collections.Generic.List<TargetOption> _manualTargets =
            new System.Collections.Generic.List<TargetOption>();

        private ConfigLocation _backupLocation;
        private ConfigLocation _restoreLocation;
        private System.Collections.Generic.List<CharacterInfo> _targetCharacters =
            new System.Collections.Generic.List<CharacterInfo>();
        private FfbakManifest _loadedManifest;
        private string _loadedFfbakPath;
        private string _loadedTempFfbakPath;
        private CancellationTokenSource _operationCts;
        private bool _isBusy;
        private bool _settingThemeSelector;
        private bool _settingPageSelector;
        private bool _showingRestorePage;
        private string _defaultBackupFileName;
        private string _restoreSnapshotPath;

        public MainWindow()
        {
            InitializeComponent();
            _settingThemeSelector = true;
            SetThemeRadioButtons(SettingsStore.Current.Settings.Theme);
            _settingThemeSelector = false;
            ThemeManager.ThemeChanged += ThemeManager_ThemeChanged;
            BackupCharactersList.ItemsSource = _backupItems;
            RestoreMappingsList.ItemsSource = _restoreMappings;
            Closed += (s, e) => CleanupTempBackup();
            Closed += (s, e) => ThemeManager.ThemeChanged -= ThemeManager_ThemeChanged;
            SetPage(false);
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                await InitializeStartupAsync();
            }
            catch (Exception ex)
            {
                ShowError("初始化失败", ex);
            }
        }

        private void ThemeRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (_settingThemeSelector || sender == null) return;
            var radio = sender as RadioButton;
            if (radio == null || radio.Tag == null) return;
            ThemeManager.SetThemePreference(radio.Tag.ToString());
        }

        private void ThemeManager_ThemeChanged(object sender, EventArgs e)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => ThemeManager_ThemeChanged(sender, e)));
                return;
            }
            _settingThemeSelector = true;
            SetThemeRadioButtons(ThemeManager.ActivePreference);
            _settingThemeSelector = false;
        }

        private void SetThemeRadioButtons(string preference)
        {
            int index = ThemeManager.ToIndex(preference);
            ThemeSystemRadio.IsChecked = index == 0;
            ThemeLightRadio.IsChecked = index == 1;
            ThemeDarkRadio.IsChecked = index == 2;
        }

        private void PageRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (_settingPageSelector || sender == null) return;
            var radio = sender as RadioButton;
            if (radio == null) return;
            SetPage(string.Equals(radio.Tag as string, "restore", StringComparison.OrdinalIgnoreCase));
        }

        private void BackupFileNameBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (BackupFileNameHint == null || BackupFileNameBox == null) return;
            BackupFileNameHint.Visibility = string.IsNullOrWhiteSpace(BackupFileNameBox.Text)
                ? Visibility.Visible : Visibility.Collapsed;
        }

        private async Task InitializeStartupAsync()
        {
            var settings = SettingsStore.Current.Settings;
            BackupSaveDirectoryBox.Text = DefaultBackupDirectory();
            _defaultBackupFileName = CreateDefaultBackupFileName(BackupSaveDirectoryBox.Text);
            BackupFileNameHint.Text = _defaultBackupFileName;

            string archiveArg = GetArgument("--restore");
            string targetArg = GetArgument("--target");

            if (!string.IsNullOrWhiteSpace(targetArg))
            {
                SetPage(true);
                RestoreGamePathBox.Text = targetArg;
                await ScanTargetAsync();
            }

            if (!string.IsNullOrWhiteSpace(archiveArg))
            {
                SetPage(true);
                RestoreArchivePathBox.Text = archiveArg;
                await LoadArchiveAsync();
                return;
            }

            string initialInput = settings.LastGamePath;
            var location = GamePathResolver.Resolve(initialInput, string.IsNullOrWhiteSpace(initialInput));
            if (!location.Exists && !string.IsNullOrWhiteSpace(initialInput))
            {
                var fallback = GamePathResolver.Resolve(null, true);
                if (fallback.Exists)
                {
                    location = fallback;
                    initialInput = fallback.ConfigRoot;
                }
            }
            if (location.Exists)
            {
                BackupGamePathBox.Text = string.IsNullOrWhiteSpace(initialInput)
                    ? location.ConfigRoot
                    : initialInput;
                await ScanBackupAsync();
            }
            else
            {
                BackupConfigPathText.Text = "尚未找到配置目录，请选择游戏目录或直接选择配置文件夹。";
            }

            if (string.IsNullOrWhiteSpace(RestoreGamePathBox.Text))
            {
                RestoreGamePathBox.Text = !string.IsNullOrWhiteSpace(settings.LastTargetGamePath)
                    ? settings.LastTargetGamePath
                    : (location.Exists ? location.ConfigRoot : string.Empty);
            }

            TryFindAdjacentBackup();
        }

        private void TryFindAdjacentBackup()
        {
            try
            {
                string exe = Process.GetCurrentProcess().MainModule.FileName;
                string dir = Path.GetDirectoryName(exe);
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;
                var backups = Directory.GetFiles(dir, "*.ffbak", SearchOption.TopDirectoryOnly);
                if (backups.Length == 1)
                {
                    RestoreArchivePathBox.Text = backups[0];
                    ArchiveSummaryText.Text = "发现同目录备份文件，点击“读取备份”即可导入。";
                }
            }
            catch
            {
                // Optional convenience only.
            }
        }

        private static string GetArgument(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            }
            return null;
        }

        private string DefaultBackupDirectory()
        {
            string dir = SettingsStore.Current.Settings.LastBackupDirectory;
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                dir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                dir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            return dir;
        }

        private static string CreateDefaultBackupFileName(string directory)
        {
            string name = "FF14_" + DateTime.Now.ToString("yyyyMMdd_HHmm");
            if (!string.IsNullOrWhiteSpace(directory) &&
                (File.Exists(Path.Combine(directory, name + ".ffbak")) ||
                 File.Exists(Path.Combine(directory, name + ".zip"))))
                name = "FF14_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
            return name;
        }

        private void SetBusy(bool busy, bool backupTab, bool allowCancel)
        {
            AppLog.Info("SetBusy " + busy + " tab=" + (backupTab ? "backup" : "restore"));
            _isBusy = busy;
            StartBackupButton.IsEnabled = !busy;
            StartRestoreButton.IsEnabled = !busy;
            BackupGamePathBox.IsEnabled = !busy;
            BackupSaveDirectoryBox.IsEnabled = !busy;
            BackupFileNameBox.IsEnabled = !busy;
            PortablePackageCheckBox.IsEnabled = !busy;
            IncludeSystemSettingsCheckBox.IsEnabled = !busy;
            IncludeSharedMacroCheckBox.IsEnabled = !busy;
            RestoreArchivePathBox.IsEnabled = !busy;
            RestoreGamePathBox.IsEnabled = !busy;
            CancelBackupButton.Visibility = busy && backupTab && allowCancel ? Visibility.Visible : Visibility.Collapsed;
            RestoreChangesButton.IsEnabled = !busy && !string.IsNullOrWhiteSpace(_restoreSnapshotPath)
                && File.Exists(_restoreSnapshotPath);
        }

        private void SetBackupScanning(bool scanning)
        {
            BackupProgressBar.IsIndeterminate = scanning;
            SetProgressState(BackupProgressBar, "AccentBrush");
            if (scanning) BackupProgressBar.Value = 0;
        }

        private void SetRestoreScanning(bool scanning)
        {
            RestoreProgressBar.IsIndeterminate = scanning;
            SetProgressState(RestoreProgressBar, "AccentBrush");
            if (scanning) RestoreProgressBar.Value = 0;
        }

        private static void SetProgressState(ProgressBar progressBar, string brushKey)
        {
            if (progressBar == null) return;
            progressBar.SetResourceReference(ProgressBar.ForegroundProperty, brushKey);
        }

        private void UpdateBackupEmpty()
        {
            BackupEmptyText.Visibility = _backupItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateRestoreEmpty()
        {
            RestoreEmptyText.Visibility = _restoreMappings.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private static string BrowseFolder(Window owner, string initialPath, string description)
        {
            using (var dialog = new WinForms.FolderBrowserDialog())
            {
                dialog.Description = description;
                dialog.ShowNewFolderButton = true;
                if (!string.IsNullOrWhiteSpace(initialPath) && Directory.Exists(initialPath))
                    dialog.SelectedPath = initialPath;
                return dialog.ShowDialog() == WinForms.DialogResult.OK ? dialog.SelectedPath : null;
            }
        }

        private void SetPage(bool showRestore)
        {
            _showingRestorePage = showRestore;
            BackupPage.Visibility = showRestore ? Visibility.Collapsed : Visibility.Visible;
            RestorePage.Visibility = showRestore ? Visibility.Visible : Visibility.Collapsed;
            _settingPageSelector = true;
            BackupPageRadio.IsChecked = !showRestore;
            RestorePageRadio.IsChecked = showRestore;
            _settingPageSelector = false;
        }

        private void ShowError(string title, Exception ex)
        {
            string message = ex == null ? "未知错误。" : ex.Message;
            MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private void ShowError(string message)
        {
            MessageBox.Show(this, message, "操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private void CleanupTempBackup()
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(_loadedTempFfbakPath) && File.Exists(_loadedTempFfbakPath))
                    File.Delete(_loadedTempFfbakPath);
            }
            catch
            {
                // Temp cleanup is best effort.
            }
            _loadedTempFfbakPath = null;
        }

        private static string ClientLabel(string client)
        {
            if (string.Equals(client, "cn", StringComparison.OrdinalIgnoreCase)) return "国服";
            if (string.Equals(client, "global", StringComparison.OrdinalIgnoreCase)) return "国际服";
            return "未知客户端";
        }

        private static string FormatBytes(long bytes)
        {
            return BackupItem.FormatSize(bytes);
        }

        private void EditAlias_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var item = button == null ? null : button.Tag as BackupItem;
            if (item == null) return;
            string current = string.IsNullOrWhiteSpace(item.Character.Alias)
                ? item.Character.DetectedName
                : item.Character.Alias;
            string value = SimpleDialogs.Prompt(this, "设置角色备注",
                "备注名（仅保存在本机）：", current);
            if (value == null) return;
            SettingsStore.Current.SetAlias(item.Character.FolderId, value);
            item.RefreshAlias();
        }
    }
}

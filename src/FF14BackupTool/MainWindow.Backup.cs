using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using FF14BackupTool.Core;
using FF14BackupTool.Dialogs;
using FF14BackupTool.ViewModels;

namespace FF14BackupTool
{
    public partial class MainWindow
    {
        private async void BrowseBackupGamePath_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;
            string selected = BrowseFolder(this, BackupGamePathBox.Text,
                "选择最终幻想14安装目录（包含 game 文件夹），或直接选择配置目录");
            if (string.IsNullOrWhiteSpace(selected)) return;
            BackupGamePathBox.Text = selected;
            await ScanBackupAsync();
        }

        private async void ScanBackup_Click(object sender, RoutedEventArgs e)
        {
            await ScanBackupAsync();
        }

        private async Task ScanBackupAsync()
        {
            if (_isBusy) return;
            string input = BackupGamePathBox.Text == null ? string.Empty : BackupGamePathBox.Text.Trim();
            ConfigLocation location = GamePathResolver.Resolve(input, string.IsNullOrWhiteSpace(input));
            if (!location.Exists)
            {
                BackupConfigPathText.Text = "未找到角色配置目录。请确认选择的是包含 game 文件夹的游戏目录。";
                if (!string.IsNullOrWhiteSpace(location.ExpectedPath))
                    BackupConfigPathText.Text += " 预期位置：" + location.ExpectedPath;
                MessageBox.Show(this,
                    "没有找到 FINAL FANTASY XIV - A Realm Reborn 配置目录。\n\n国服通常在：\n游戏目录\\game\\My Games\\FINAL FANTASY XIV - A Realm Reborn\n\n请选择游戏安装目录，或直接选择配置目录。",
                    "未找到配置目录", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _backupLocation = location;
            if (string.IsNullOrWhiteSpace(input))
                BackupGamePathBox.Text = location.ConfigRoot;
            BackupConfigPathText.Text = "配置目录：" + location.ConfigRoot +
                "  ·  " + ClientLabel(location.Client) +
                "  ·  " + (location.IsWritable ? "可写" : "只读（不影响备份）");
            SetBusy(true, true, false);
            SetBackupScanning(true);
            BackupStatusText.Text = "正在扫描角色...";
            BackupResultText.Text = string.Empty;
            try
            {
                var progress = new Progress<OperationProgress>(p =>
                {
                    BackupStatusText.Text = p.Message;
                });
                var characters = await Task.Run(() => CharacterScanner.Scan(
                    location.ConfigRoot, true, progress, CancellationToken.None));

                _backupItems.Clear();
                foreach (var character in characters)
                    _backupItems.Add(new BackupItem(character));
                UpdateBackupEmpty();

                if (string.IsNullOrWhiteSpace(BackupSaveDirectoryBox.Text))
                    BackupSaveDirectoryBox.Text = DefaultBackupDirectory();

                long total = _backupItems.Sum(i => i.Character.TotalSize);
                BackupStatusText.Text = _backupItems.Count == 0
                    ? "未找到角色文件夹。请确认该目录下存在 FFXIV_CHR... 文件夹。"
                    : "已找到 " + _backupItems.Count + " 个角色，共 " + FormatBytes(total) + "。";
                IncludeSystemSettingsCheckBox.IsEnabled = File.Exists(Path.Combine(location.ConfigRoot, "FFXIV.cfg"));
                IncludeSharedMacroCheckBox.IsEnabled = File.Exists(Path.Combine(location.ConfigRoot, "MACROSYS.DAT"));

                SettingsStore.Current.Settings.LastGamePath = BackupGamePathBox.Text;
                SettingsStore.Current.Settings.LastBackupDirectory = BackupSaveDirectoryBox.Text.Trim();
                SettingsStore.Current.Save();
            }
            catch (Exception ex)
            {
                ShowError("扫描角色失败", ex);
            }
            finally
            {
                SetBusy(false, true, false);
                SetBackupScanning(false);
            }
        }

        private void SelectAllCharacters_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _backupItems) item.IsSelected = true;
        }

        private void SelectNoCharacters_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _backupItems) item.IsSelected = false;
        }

        private void ResetDefaultGroups_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _backupItems) item.SetDefaults();
        }

        private void BrowseBackupDirectory_Click(object sender, RoutedEventArgs e)
        {
            string selected = BrowseFolder(this, BackupSaveDirectoryBox.Text, "选择备份文件保存文件夹");
            if (!string.IsNullOrWhiteSpace(selected))
                BackupSaveDirectoryBox.Text = selected;
        }

        private async void StartBackup_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;
            if (_backupLocation == null || !_backupLocation.Exists)
            {
                MessageBox.Show(this, "请先选择游戏目录并扫描角色。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var selection = BuildBackupSelection();
            if (selection == null) return;

            string saveDirectory = BackupSaveDirectoryBox.Text == null
                ? string.Empty : BackupSaveDirectoryBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(saveDirectory))
            {
                BrowseBackupDirectory_Click(sender, e);
                saveDirectory = BackupSaveDirectoryBox.Text == null
                    ? string.Empty : BackupSaveDirectoryBox.Text.Trim();
                if (string.IsNullOrWhiteSpace(saveDirectory)) return;
            }

            string fileName = BackupFileNameBox.Text == null ? string.Empty : BackupFileNameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(fileName))
                fileName = CreateDefaultBackupFileName(saveDirectory);
            if (fileName.EndsWith(".ffbak", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                fileName = Path.GetFileNameWithoutExtension(fileName);
            if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                fileName.IndexOf(Path.DirectorySeparatorChar) >= 0 ||
                fileName.IndexOf(Path.AltDirectorySeparatorChar) >= 0)
            {
                MessageBox.Show(this, "备份文件名包含无效字符，请只填写文件名，不要包含文件夹路径。", "文件名无效",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            bool makePortable = PortablePackageCheckBox.IsChecked == true;
            string outputPath = Path.Combine(saveDirectory, fileName + (makePortable ? ".zip" : ".ffbak"));

            if (File.Exists(outputPath))
            {
                var overwrite = MessageBox.Show(this, "目标备份已存在，是否覆盖？\n\n" + outputPath,
                    "覆盖备份", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (overwrite != MessageBoxResult.Yes) return;
            }

            if (GameProcessProbe.IsGameRunning())
            {
                var proceed = MessageBox.Show(this,
                    "检测到游戏正在运行。当前备份可能不包含最新设置，是否仍要继续？",
                    "游戏正在运行", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (proceed != MessageBoxResult.Yes) return;
            }

            string stagingPath = null;
            try
            {
                if (makePortable)
                {
                    string stagingDirectory = Path.Combine(Path.GetTempPath(), "FF14BackupTool", "backup-staging");
                    Directory.CreateDirectory(stagingDirectory);
                    stagingPath = Path.Combine(stagingDirectory, Guid.NewGuid().ToString("N") + ".ffbak");
                }

                string dirToCreate = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrWhiteSpace(dirToCreate)) Directory.CreateDirectory(dirToCreate);
            }
            catch (Exception ex)
            {
                ShowError("保存路径无效或无法创建目录。", ex);
                return;
            }

            _operationCts = new CancellationTokenSource();
            SetBusy(true, true, true);
            SetProgressState(BackupProgressBar, "AccentBrush");
            BackupProgressBar.IsIndeterminate = false;
            BackupProgressBar.Value = 0;
            BackupStatusText.Text = "准备备份...";
            BackupResultText.Text = string.Empty;

            try
            {
                var progress = new Progress<OperationProgress>(p =>
                {
                    BackupProgressBar.Value = p.Percent;
                    BackupStatusText.Text = p.Message;
                });
                var token = _operationCts.Token;
                string ffbakPath = makePortable ? stagingPath : outputPath;
                var result = await Task.Run(() => FfbakWriter.Write(
                    _backupLocation.ConfigRoot, selection, ffbakPath,
                    CompressionLevel.Optimal, progress, token), token);

                SettingsStore.Current.Settings.LastBackupDirectory = saveDirectory;
                SettingsStore.Current.Save();

                string finalOutputPath = result.Path;
                if (makePortable)
                {
                    BackupStatusText.Text = "备份完成，正在生成便携包...";
                    BackupProgressBar.Value = 0;
                    var packageProgress = new Progress<OperationProgress>(p =>
                    {
                        BackupProgressBar.Value = p.Percent;
                        BackupStatusText.Text = p.Message;
                    });
                    string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                    var package = await Task.Run(() => PortablePackageWriter.Create(
                        result.Path, exePath, outputPath, packageProgress, token), token);
                    finalOutputPath = package.Path;
                }

                BackupProgressBar.Value = 100;
                SetProgressState(BackupProgressBar, "SuccessBrush");
                BackupStatusText.Text = "备份完成。";
                BackupResultText.Text = "备份文件：" + finalOutputPath + "\n" +
                                        "角色：" + result.CharacterCount + " 个，文件：" + result.FileCount + " 个" +
                                        "\n已打开备份所在文件夹。";
                OpenBackupFolder(finalOutputPath);
            }
            catch (OperationCanceledException)
            {
                SetProgressState(BackupProgressBar, "AccentBrush");
                BackupProgressBar.Value = 0;
                BackupStatusText.Text = "备份已取消。";
            }
            catch (Exception ex)
            {
                SetProgressState(BackupProgressBar, "DangerBrush");
                BackupStatusText.Text = "备份失败。";
                ShowError("备份失败", ex);
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(stagingPath))
                {
                    try { if (File.Exists(stagingPath)) File.Delete(stagingPath); } catch { }
                }
                SetBusy(false, true, false);
                if (_operationCts != null)
                {
                    _operationCts.Dispose();
                    _operationCts = null;
                }
            }
        }

        private BackupSelection BuildBackupSelection()
        {
            var selection = new BackupSelection
            {
                IncludeSystemSettings = IncludeSystemSettingsCheckBox.IsChecked == true,
                IncludeSharedMacro = IncludeSharedMacroCheckBox.IsChecked == true
            };

            foreach (var item in _backupItems.Where(i => i.IsSelected))
            {
                var groups = item.SelectedGroups();
                if (groups.Count == 0) continue;
                selection.Characters.Add(new CharacterSelection
                {
                    Character = item.Character,
                    Groups = groups
                });
            }

            bool hasGlobalSettings = IncludeSystemSettingsCheckBox.IsChecked == true &&
                File.Exists(Path.Combine(_backupLocation.ConfigRoot, "FFXIV.cfg"));
            bool hasSharedMacro = IncludeSharedMacroCheckBox.IsChecked == true &&
                File.Exists(Path.Combine(_backupLocation.ConfigRoot, "MACROSYS.DAT"));
            if (selection.Characters.Count == 0 && !hasGlobalSettings && !hasSharedMacro)
            {
                MessageBox.Show(this, "请至少选择一个要备份的角色或全局设置。", "没有可备份的内容",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return null;
            }

            return selection;
        }

        private static void OpenBackupFolder(string outputPath)
        {
            try
            {
                string directory = Path.GetDirectoryName(outputPath);
                if (string.IsNullOrWhiteSpace(directory)) return;
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "\"" + directory + "\"",
                    UseShellExecute = true
                });
            }
            catch
            {
                // Opening Explorer is a convenience and must not make a successful backup fail.
            }
        }

        private void CancelBackup_Click(object sender, RoutedEventArgs e)
        {
            if (_operationCts != null)
            {
                BackupStatusText.Text = "正在取消...";
                _operationCts.Cancel();
            }
        }

    }
}

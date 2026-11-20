using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using FF14BackupTool.Core;
using FF14BackupTool.Dialogs;
using FF14BackupTool.ViewModels;
using Microsoft.Win32;

namespace FF14BackupTool
{
    public partial class MainWindow
    {
        private async void BrowseRestoreGamePath_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;
            string selected = BrowseFolder(this, RestoreGamePathBox.Text,
                "选择要释放到的最终幻想14安装目录（包含 game 文件夹），或直接选择配置目录");
            if (string.IsNullOrWhiteSpace(selected)) return;
            RestoreGamePathBox.Text = selected;
            await ScanTargetAsync();
        }

        private async void ScanTarget_Click(object sender, RoutedEventArgs e)
        {
            await ScanTargetAsync();
        }

        private async Task ScanTargetAsync()
        {
            if (_isBusy) return;
            string input = RestoreGamePathBox.Text == null ? string.Empty : RestoreGamePathBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(input))
            {
                MessageBox.Show(this, "请先选择目标游戏目录。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            ConfigLocation location = GamePathResolver.Resolve(input, false);
            if (!location.Exists)
            {
                if (location.CanCreate)
                {
                    var create = MessageBox.Show(this,
                        "没有找到目标配置目录，是否创建？\n\n" + location.ExpectedPath +
                        "\n\n建议先启动一次游戏，让客户端自动生成配置目录。",
                        "创建配置目录", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (create == MessageBoxResult.Yes)
                    {
                        try
                        {
                            location = GamePathResolver.CreateConfigRoot(location);
                        }
                        catch (Exception ex)
                        {
                            ShowError("创建配置目录失败", ex);
                            return;
                        }
                    }
                }

                if (!location.Exists)
                {
                    RestoreConfigPathText.Text = "未找到目标配置目录。预期位置：" + (location.ExpectedPath ?? "-");
                    RestoreConfigPathText.Visibility = Visibility.Visible;
                    MessageBox.Show(this,
                        "没有找到目标配置目录。\n\n国服通常在：\n游戏目录\\game\\My Games\\FINAL FANTASY XIV - A Realm Reborn\n\n请选择游戏安装目录，或先启动一次游戏。",
                        "未找到目标目录", MessageBoxButton.OK, MessageBoxImage.Warning);
                    ElevationPanel.Visibility = Visibility.Collapsed;
                    return;
                }
            }

            _restoreLocation = location;
            RestoreConfigPathText.Visibility = Visibility.Collapsed;
            RestoreConfigPathText.Text = "目标配置目录：" + location.ConfigRoot +
                "  ·  " + ClientLabel(location.Client) +
                "  ·  " + (location.IsWritable ? "可写" : "不可写");
            UpdateElevationPanel(location);

            SetBusy(true, false, false);
            SetProgressState(RestoreProgressBar, "AccentBrush");
            SetRestoreScanning(true);
            RestoreStatusText.Text = "正在扫描目标角色...";
            try
            {
                var progress = new Progress<OperationProgress>(p => RestoreStatusText.Text = p.Message);
                var characters = await Task.Run(() => CharacterScanner.Scan(
                    location.ConfigRoot, true, progress, CancellationToken.None));
                _targetCharacters = characters;
                RefreshMappings();
                RestoreStatusText.Text = "目标目录找到 " + characters.Count + " 个角色。" +
                    (GameProcessProbe.IsGameRunning()
                        ? " 检测到游戏正在运行，恢复前请先退出游戏。"
                        : string.Empty);

                if (_loadedManifest != null && _loadedManifest.Characters.Count > 0 &&
                    characters.Count > 0 &&
                    !characters.Any(t => _loadedManifest.Characters.Any(s =>
                        string.Equals(s.Folder, t.FolderId, StringComparison.OrdinalIgnoreCase))))
                {
                    RestoreStatusText.Text += " 没有找到相同角色 ID，默认会新建角色文件夹；如果这是另一个账号，请使用“添加目标角色”手动映射。";
                }

                SettingsStore.Current.Settings.LastTargetGamePath = RestoreGamePathBox.Text;
                SettingsStore.Current.Save();
            }
            catch (Exception ex)
            {
                ShowError("扫描目标角色失败", ex);
            }
            finally
            {
                SetBusy(false, false, false);
                SetRestoreScanning(false);
            }
        }

        private void UpdateElevationPanel(ConfigLocation location)
        {
            if (location == null || !location.Exists || location.IsWritable)
            {
                ElevationPanel.Visibility = Visibility.Collapsed;
                return;
            }

            ElevationPanel.Visibility = Visibility.Visible;
            bool admin = ElevationHelper.IsAdministrator();
            ElevationText.Text = admin
                ? "当前已经以管理员身份运行，但该目录仍不可写。请检查文件夹权限。"
                : "目标目录不可写。国服默认安装在 Program Files 下时，恢复需要管理员权限。";
            var button = ElevationPanel.Child as System.Windows.Controls.Grid;
            if (button != null && button.Children.Count > 1)
            {
                var adminButton = button.Children[1] as System.Windows.Controls.Button;
                if (adminButton != null)
                {
                    adminButton.IsEnabled = !admin;
                    adminButton.Content = admin ? "已以管理员运行" : "以管理员身份重启";
                }
            }
        }

        private async void BrowseArchive_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;
            var dialog = new OpenFileDialog
            {
                Title = "选择 FF14 备份或便携转移包",
                Filter = "FF14 备份/便携包 (*.ffbak;*.zip)|*.ffbak;*.zip|所有文件 (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };
            if (dialog.ShowDialog(this) == true)
            {
                RestoreArchivePathBox.Text = dialog.FileName;
                await LoadArchiveAsync();
            }
        }

        private async void LoadArchive_Click(object sender, RoutedEventArgs e)
        {
            await LoadArchiveAsync();
        }

        private async Task LoadArchiveAsync()
        {
            if (_isBusy) return;
            string path = RestoreArchivePathBox.Text == null ? string.Empty : RestoreArchivePathBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                MessageBox.Show(this, "请选择存在的 .ffbak 或 .zip 文件。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            SetBusy(true, false, false);
            SetRestoreScanning(true);
            RestoreStatusText.Text = "正在读取备份...";
            string pendingTempPath = null;
            try
            {
                string ffbakPath = path;
                var inspection = await Task.Run(() => FfbakReader.Inspect(path));

                if (!inspection.IsDirectBackup)
                {
                    NestedBackupCandidate candidate;
                    if (inspection.NestedCandidates.Count == 1)
                    {
                        candidate = inspection.NestedCandidates[0];
                    }
                    else
                    {
                        candidate = SimpleDialogs.Choose<NestedBackupCandidate>(this,
                            "选择备份", "这个 ZIP 里包含多个 .ffbak，请选择要导入的备份：",
                            inspection.NestedCandidates);
                        if (candidate == null) return;
                    }

                    string tempRoot = Path.Combine(Path.GetTempPath(), "FF14BackupTool", "archives");
                    var progress = new Progress<OperationProgress>(p =>
                    {
                        RestoreStatusText.Text = p.Message;
                        RestoreProgressBar.Value = p.Percent;
                    });
                    pendingTempPath = await Task.Run(() => FfbakReader.ExtractNestedBackup(
                        path, candidate.EntryName, tempRoot, progress, CancellationToken.None));
                    ffbakPath = pendingTempPath;
                    inspection = await Task.Run(() => FfbakReader.Inspect(ffbakPath));
                }

                var manifest = inspection.Manifest;
                if (manifest == null) throw new InvalidDataException("无法读取备份清单。");

                CleanupTempBackup();
                _loadedManifest = manifest;
                _loadedFfbakPath = ffbakPath;
                _loadedTempFfbakPath = pendingTempPath;
                pendingTempPath = null;
                RestoreArchivePathBox.Text = path;

                int fileCount = manifest.Characters.Sum(c => c.Files.Count);
                string created = manifest.CreatedAt;
                if (DateTimeOffset.TryParse(manifest.CreatedAt, out var createdTime))
                    created = createdTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

                int systemSections = manifest.SystemSettings == null || manifest.SystemSettings.Sections == null
                    ? 0
                    : manifest.SystemSettings.Sections.Count;
                ArchiveSummaryText.Text = "客户端：" + ClientLabel(manifest.Client) +
                    "  ·  创建时间：" + created +
                    "  ·  角色：" + manifest.Characters.Count +
                    "  ·  文件：" + fileCount +
                    "  ·  系统设置：" + (systemSections > 0 ? systemSections + " 个安全段" : "无");
                ArchiveDetailText.Text = manifest.Characters.Count == 0
                    ? "这个备份没有角色文件。"
                    : string.Join("；", manifest.Characters.Take(6).Select(c =>
                        c.DisplayName + "（" + c.Folder + "）")) +
                      (manifest.Characters.Count > 6 ? " 等 " + manifest.Characters.Count + " 个角色" : string.Empty);

                RefreshMappings();
                RestoreStatusText.Text = "备份读取完成，请确认目标目录和角色映射。";
                RestoreProgressBar.Value = 100;
                SetProgressState(RestoreProgressBar, "SuccessBrush");
                ArchiveSummaryText.Visibility = Visibility.Collapsed;
                ArchiveDetailText.Visibility = Visibility.Collapsed;
                SettingsStore.Current.Settings.LastArchivePath = path;
                SettingsStore.Current.Settings.LastTargetGamePath = RestoreGamePathBox.Text;
                SettingsStore.Current.Save();
            }
            catch (Exception ex)
            {
                if (!string.IsNullOrWhiteSpace(pendingTempPath))
                {
                    try { if (File.Exists(pendingTempPath)) File.Delete(pendingTempPath); }
                    catch { }
                    pendingTempPath = null;
                }
                ArchiveSummaryText.Text = "读取备份失败。";
                ArchiveDetailText.Text = string.Empty;
                ArchiveSummaryText.Visibility = Visibility.Visible;
                SetProgressState(RestoreProgressBar, "DangerBrush");
                _loadedManifest = null;
                _loadedFfbakPath = null;
                CleanupTempBackup();
                _restoreMappings.Clear();
                UpdateRestoreEmpty();
                UpdateCrossWarning();
                ShowError("读取备份失败", ex);
            }
            finally
            {
                SetBusy(false, false, false);
                SetRestoreScanning(false);
            }
        }

        private void RefreshMappings()
        {
            _restoreMappings.Clear();
            if (_loadedManifest == null)
            {
                UpdateRestoreEmpty();
                UpdateCrossWarning();
                return;
            }

            var baseOptions = new List<TargetOption>();
            foreach (var target in _targetCharacters)
            {
                baseOptions.Add(new TargetOption
                {
                    FolderId = target.FolderId,
                    DisplayName = target.DisplayName,
                    Detail = target.DetailLine,
                    IsNew = false
                });
            }
            foreach (var manual in _manualTargets)
            {
                if (baseOptions.Any(o => string.Equals(o.FolderId, manual.FolderId, StringComparison.OrdinalIgnoreCase)))
                    continue;
                baseOptions.Add(manual);
            }

            foreach (var source in _loadedManifest.Characters)
            {
                var options = new List<TargetOption>(baseOptions);
                if (!options.Any(o => string.Equals(o.FolderId, source.Folder, StringComparison.OrdinalIgnoreCase)))
                {
                    options.Add(new TargetOption
                    {
                        FolderId = source.Folder,
                        DisplayName = source.DisplayName,
                        Detail = "目标中尚未创建，将新建文件夹",
                        IsNew = true
                    });
                }

                var item = new RestoreMappingItem(source);
                item.SetTargets(options, null);
                item.SelectionChanged += (s, e) => UpdateCrossWarning();
                _restoreMappings.Add(item);
            }

            UpdateRestoreEmpty();
            UpdateCrossWarning();
        }

        private void Mapping_SelectionChanged(object sender, EventArgs e)
        {
            UpdateCrossWarning();
        }

        private void UpdateCrossWarning()
        {
            int cross = _restoreMappings.Count(m => m.IsSelected && m.IsCrossCharacter);
            if (cross <= 0)
            {
                CrossWarningBorder.Visibility = Visibility.Collapsed;
                return;
            }

            CrossWarningBorder.Visibility = Visibility.Visible;
            CrossWarningText.Text = "检测到 " + cross +
                " 个跨角色覆盖。恢复前会完整备份目标角色；UISAVE.DAT 与聊天记录将自动跳过。";
        }

        private void AddTargetCharacter_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;
            string id;
            string alias;
            if (!SimpleDialogs.PromptTwo(this, "添加目标角色",
                    "FFXIV_CHR 角色 ID（16 位十六进制，可省略 FFXIV_CHR 前缀）", "FFXIV_CHR",
                    "备注名（可选）", string.Empty, out id, out alias))
                return;

            id = (id ?? string.Empty).Trim();
            if (Regex.IsMatch(id, "^[0-9A-Fa-f]{16}$"))
                id = "FFXIV_CHR" + id.ToUpperInvariant();
            if (!PathGuard.IsValidCharacterId(id))
            {
                MessageBox.Show(this, "角色 ID 必须是 FFXIV_CHR 加 16 位十六进制。", "ID 不合法",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (_targetCharacters.Any(c => string.Equals(c.FolderId, id, StringComparison.OrdinalIgnoreCase)) ||
                _manualTargets.Any(o => string.Equals(o.FolderId, id, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show(this, "这个目标角色已经在列表里。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _manualTargets.Add(new TargetOption
            {
                FolderId = id,
                DisplayName = string.IsNullOrWhiteSpace(alias) ? "手动添加角色" : alias.Trim(),
                Detail = "手动添加；恢复时如文件夹不存在会自动创建",
                IsNew = true,
                IsManual = true
            });
            RefreshMappings();
            MessageBox.Show(this,
                "已添加目标角色。请在映射行的下拉框中选择它。\n\n" +
                "如果该角色从未在此电脑登录，游戏可能不会读取新建的文件夹，建议先登录一次。",
                "已添加", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void ResetMappings_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _restoreMappings)
            {
                var same = item.TargetOptions.FirstOrDefault(o =>
                    string.Equals(o.FolderId, item.Source.Folder, StringComparison.OrdinalIgnoreCase));
                if (same != null) item.SelectedTarget = same;
            }
            UpdateCrossWarning();
        }

        private async void StartRestore_Click(object sender, RoutedEventArgs e)
        {
            AppLog.Info("StartRestore clicked busy=" + _isBusy + " manifest=" + (_loadedManifest != null) +
                        " archive=" + _loadedFfbakPath + " target=" + (_restoreLocation == null ? "null" : _restoreLocation.ConfigRoot));
            if (_isBusy) { AppLog.Info("StartRestore ignored: busy"); return; }
            if (_loadedManifest == null || string.IsNullOrWhiteSpace(_loadedFfbakPath))
            {
                MessageBox.Show(this, "请先读取备份文件。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (_restoreLocation == null || !_restoreLocation.Exists)
            {
                MessageBox.Show(this, "请先选择并扫描目标游戏目录。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (GameProcessProbe.IsGameRunning())
            {
                MessageBox.Show(this, "检测到游戏正在运行。请完全退出游戏后再执行恢复。", "游戏正在运行",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!_restoreLocation.IsWritable)
            {
                if (!ElevationHelper.IsAdministrator())
                {
                    var elevate = MessageBox.Show(this,
                        "目标目录不可写。是否以管理员身份重启工具并继续？\n\n" +
                        "重启前会保留当前备份文件路径和目标路径。",
                        "需要管理员权限", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (elevate == MessageBoxResult.Yes)
                    {
                        RelaunchAsAdministrator();
                        return;
                    }
                }
                else
                {
                    MessageBox.Show(this, "当前已是管理员身份，但目标目录仍不可写。请检查文件夹权限。", "无法写入",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                return;
            }

            if (!string.Equals(_loadedManifest.Client, "unknown", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(_restoreLocation.Client, "unknown", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(_loadedManifest.Client, _restoreLocation.Client, StringComparison.OrdinalIgnoreCase))
            {
                var proceed = MessageBox.Show(this,
                    "备份客户端：" + ClientLabel(_loadedManifest.Client) +
                    "\n目标客户端：" + ClientLabel(_restoreLocation.Client) +
                    "\n\n不同客户端的角色 ID 通常不匹配，恢复后游戏可能不会读取。是否仍要继续？",
                    "客户端不一致", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (proceed != MessageBoxResult.Yes) return;
            }

            var selectedMappings = _restoreMappings.Where(m => m.IsSelected && m.SelectedTarget != null).ToList();
            AppLog.Info("StartRestore selected mappings=" + selectedMappings.Count + " writable=" + _restoreLocation.IsWritable);
            if (selectedMappings.Count == 0)
            {
                MessageBox.Show(this, "请至少选择一个要恢复的角色。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var duplicateTargets = selectedMappings
                .GroupBy(m => m.SelectedTarget.FolderId, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(g => g.Count() > 1);
            if (duplicateTargets != null)
            {
                MessageBox.Show(this, "同一个目标角色只能映射一个来源，请调整重复项：\n\n" + duplicateTargets.Key,
                    "映射重复", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int crossCount = selectedMappings.Count(m => m.IsCrossCharacter);
            int totalFiles = selectedMappings.Sum(m => m.Source.Files.Count);
            bool hasSystemSettings = _loadedManifest.SystemSettings != null && _loadedManifest.SystemSettings.Included;
            bool hasSharedMacro = _loadedManifest.SharedMacro != null && _loadedManifest.SharedMacro.Included;
            string confirmText =
                "目标配置目录：\n" + _restoreLocation.ConfigRoot + "\n\n" +
                "将恢复 " + selectedMappings.Count + " 个角色，备份中共 " + totalFiles + " 个文件。\n" +
                "跨角色覆盖：" + crossCount + " 个\n" +
                "全局设置：" + ((hasSystemSettings || hasSharedMacro) ? "恢复" : "无") + "\n\n" +
                "恢复前会自动备份目标角色原文件。是否继续？";
            AppLog.Info("StartRestore showing confirm dialog");
            if (MessageBox.Show(this, confirmText, "确认恢复",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                AppLog.Info("StartRestore cancelled at confirm dialog");
                return;
            }
            AppLog.Info("StartRestore confirmed; starting service");

            var options = new RestoreOptions
            {
                IncludeSystemSettings = true,
                IncludeCrossCharacterUiSave = false,
                IncludeCrossCharacterChatHistory = false,
                CreateMissingTargetCharacter = true,
                VerifyHashes = true
            };
            var mappings = selectedMappings.Select(m => new RestoreMapping
            {
                SourceFolderId = m.Source.Folder,
                TargetFolderId = m.SelectedTarget.FolderId
            }).ToList();

            string newSnapshotPath;
            try
            {
                RestoreStatusText.Text = "正在保存恢复前设置...";
                newSnapshotPath = await Task.Run(() => RestoreSnapshotStore.Create(
                    _restoreLocation.ConfigRoot,
                    mappings.Select(m => m.TargetFolderId).ToList()));
            }
            catch (Exception ex)
            {
                RestoreStatusText.Text = "恢复前快照创建失败。";
                ShowError("无法创建恢复前快照，已取消恢复", ex);
                return;
            }

            _operationCts = new CancellationTokenSource();
            SetBusy(true, false, false);
            SetProgressState(RestoreProgressBar, "AccentBrush");
            RestoreProgressBar.IsIndeterminate = false;
            RestoreProgressBar.Value = 0;
            RestoreStatusText.Text = "准备恢复...";
            RestoreResultText.Text = string.Empty;

            try
            {
                var progress = new Progress<OperationProgress>(p =>
                {
                    RestoreProgressBar.Value = p.Percent;
                    RestoreStatusText.Text = p.Message;
                });
                var token = _operationCts.Token;
                var result = await Task.Run(() => RestoreService.Restore(
                    _loadedFfbakPath, _restoreLocation.ConfigRoot, mappings, options, progress, token), token);

                string previousSnapshot = _restoreSnapshotPath;
                _restoreSnapshotPath = newSnapshotPath;
                if (!string.IsNullOrWhiteSpace(previousSnapshot) &&
                    !string.Equals(previousSnapshot, newSnapshotPath, StringComparison.OrdinalIgnoreCase))
                {
                    try { if (File.Exists(previousSnapshot)) File.Delete(previousSnapshot); }
                    catch { }
                }

                RestoreProgressBar.Value = 100;
                SetProgressState(RestoreProgressBar, "SuccessBrush");
                RestoreStatusText.Text = "恢复完成。";
                RestoreResultText.Text = result.Message +
                    "\n恢复文件：" + result.RestoredFiles +
                    "，跳过：" + result.SkippedFiles +
                    "，备份目标文件：" + result.BackedUpFiles +
                    (result.SystemSettingsRestored ? "\n系统设置：已合并" : "\n系统设置：未恢复") +
                    (hasSharedMacro ? "\n公用宏：已恢复" : string.Empty) +
                    "\n原文件备份目录：" + result.PreRestoreBackupPath +
                    "\n恢复前快照：已保存，可使用“还原更改”。";
                if (result.Warnings.Count > 0)
                {
                    RestoreResultText.Text += "\n\n注意：\n" + string.Join("\n", result.Warnings.Distinct());
                    MessageBox.Show(this, string.Join("\n", result.Warnings.Distinct()), "恢复完成，但有提醒",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }

                SettingsStore.Current.Settings.LastTargetGamePath = RestoreGamePathBox.Text;
                SettingsStore.Current.Save();
            }
            catch (OperationCanceledException)
            {
                SetProgressState(RestoreProgressBar, "AccentBrush");
                RestoreProgressBar.Value = 0;
                _restoreSnapshotPath = newSnapshotPath;
                RestoreStatusText.Text = "恢复已取消。";
            }
            catch (RestoreFailedException ex)
            {
                SetProgressState(RestoreProgressBar, "DangerBrush");
                _restoreSnapshotPath = newSnapshotPath;
                RestoreStatusText.Text = "恢复失败，已尝试回滚。";
                RestoreResultText.Text = ex.Result == null ? ex.Message : ex.Result.Message;
                if (ex.Result != null && ex.Result.Warnings.Count > 0)
                    RestoreResultText.Text += "\n\n" + string.Join("\n", ex.Result.Warnings.Distinct());
                MessageBox.Show(this, RestoreResultText.Text, "恢复失败",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception ex)
            {
                SetProgressState(RestoreProgressBar, "DangerBrush");
                _restoreSnapshotPath = newSnapshotPath;
                RestoreStatusText.Text = "恢复失败。";
                ShowError("恢复失败", ex);
            }
            finally
            {
                SetBusy(false, false, false);
                if (_operationCts != null)
                {
                    _operationCts.Dispose();
                    _operationCts = null;
                }
            }
        }

        private async void RestoreChanges_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy || string.IsNullOrWhiteSpace(_restoreSnapshotPath) || !File.Exists(_restoreSnapshotPath))
                return;
            if (MessageBox.Show(this,
                    "将还原本次恢复前的设置，当前恢复结果会被覆盖。是否继续？",
                    "还原更改", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            string snapshot = _restoreSnapshotPath;
            SetBusy(true, false, false);
            SetProgressState(RestoreProgressBar, "AccentBrush");
            RestoreStatusText.Text = "正在还原恢复前的设置...";
            RestoreResultText.Text = string.Empty;
            try
            {
                await Task.Run(() => RestoreSnapshotStore.Restore(snapshot, _restoreLocation.ConfigRoot));
                SetProgressState(RestoreProgressBar, "SuccessBrush");
                RestoreProgressBar.Value = 100;
                RestoreStatusText.Text = "已还原恢复前的设置。";
                RestoreResultText.Text = "目标角色文件、FFXIV.cfg 和 MACROSYS.DAT 已恢复到本次恢复前的状态。";
                _restoreSnapshotPath = null;
                try { if (File.Exists(snapshot)) File.Delete(snapshot); } catch { }
            }
            catch (Exception ex)
            {
                SetProgressState(RestoreProgressBar, "DangerBrush");
                RestoreStatusText.Text = "还原失败。";
                ShowError("还原更改失败", ex);
            }
            finally
            {
                SetBusy(false, false, false);
            }
        }

        private void RelaunchAsAdmin_Click(object sender, RoutedEventArgs e)
        {
            RelaunchAsAdministrator();
        }

        private void RelaunchAsAdministrator()
        {
            string archive = string.IsNullOrWhiteSpace(RestoreArchivePathBox.Text)
                ? _loadedFfbakPath
                : RestoreArchivePathBox.Text.Trim();
            string target = RestoreGamePathBox.Text == null ? string.Empty : RestoreGamePathBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(archive) || string.IsNullOrWhiteSpace(target))
            {
                MessageBox.Show(this, "需要先选择备份文件和目标目录。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string args = "--restore \"" + archive.Replace("\"", "\\\"") +
                          "\" --target \"" + target.Replace("\"", "\\\"") + "\"";
            if (ElevationHelper.RelaunchAsAdministrator(args))
            {
                Application.Current.Shutdown();
            }
            else
            {
                MessageBox.Show(this, "没有获得管理员权限，已取消。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }
}

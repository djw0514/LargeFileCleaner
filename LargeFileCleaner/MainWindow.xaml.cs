using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LargeFileCleaner.Models;
using LargeFileCleaner.Services;
using LargeFileCleaner.Utilities;
using Forms = System.Windows.Forms;
using WpfMessageBox = System.Windows.MessageBox;

namespace LargeFileCleaner;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<LargeFileItem> _files = [];
    private readonly LargeFileScanner _scanner = new();
    private readonly FileDeletionService _deletionService = new();
    private readonly DeleteLogService _logService = new();
    private readonly DuplicateFileAnalyzer _duplicateAnalyzer = new();
    private readonly FileMoveService _moveService = new();
    private readonly object _watcherSync = new();
    private readonly HashSet<string> _pendingRemovedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _watcherDebounceTimer;

    private ICollectionView _filesView = null!;
    private CancellationTokenSource? _scanCancellation;
    private CancellationTokenSource? _duplicateCancellation;
    private CancellationTokenSource? _reconcileCancellation;
    private CancellationTokenSource? _watcherCheckCancellation;
    private FileSystemWatcher? _fileWatcher;
    private LargeFileItem? _contextClickedFile;
    private IReadOnlyList<LargeFileItem> _contextActionFiles = [];
    private string? _activeScanRoot;
    private bool _isScanning;
    private bool _isDeleting;
    private bool _isMoving;
    private bool _isFindingDuplicates;
    private bool _isPreparingFileOperation;
    private bool _isPreparingMutation;
    private bool _duplicateAnalysisCompleted;
    private bool _suppressSummaryUpdates;
    private volatile bool _isClosed;
    private int _duplicateGroupCount;
    private int _watcherGeneration;
    private long _filesVersion;
    private long _lastVisitedFiles;
    private long _lastVisitedDirectories;
    private long _lastSkippedDirectories;
    private long _lastInaccessibleDirectories;

    public MainWindow()
    {
        InitializeComponent();
        _watcherDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _watcherDebounceTimer.Tick += WatcherDebounceTimer_Tick;

        InitializeGrid();
        InitializeStatus();
        UpdateSummary();
        UpdateBusyState();
    }

    private void InitializeGrid()
    {
        _filesView = CollectionViewSource.GetDefaultView(_files);
        _filesView.Filter = FilterFile;
        _filesView.SortDescriptions.Add(new SortDescription(nameof(LargeFileItem.SizeBytes), ListSortDirection.Descending));
        FilesGrid.ItemsSource = _filesView;
    }

    private void InitializeStatus()
    {
        StatusTextBlock.Text = "请选择扫描目录。";
        ScanStatsTextBlock.Text = "已扫描 0 个文件，0 个目录。";
        LogPathTextBlock.Text = "日志: " + ShortenPath(_logService.LogPath, 56);
    }

    private void SelectFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isScanning || _isPreparingFileOperation || _isDeleting || _isMoving || _isFindingDuplicates)
        {
            return;
        }

        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "选择扫描目录",
            ShowNewFolderButton = false,
            UseDescriptionForTitle = true
        };

        if (Directory.Exists(FolderPathTextBox.Text))
        {
            dialog.SelectedPath = FolderPathTextBox.Text;
        }

        if (dialog.ShowDialog() == Forms.DialogResult.OK)
        {
            FolderPathTextBox.Text = dialog.SelectedPath;
            StatusTextBlock.Text = "已选择目录: " + dialog.SelectedPath;
        }
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isScanning || _isPreparingFileOperation || _isDeleting || _isMoving || _isFindingDuplicates)
        {
            return;
        }

        if (!Directory.Exists(FolderPathTextBox.Text))
        {
            WpfMessageBox.Show(this, "请先选择有效的扫描目录。", "无法开始扫描", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!TryGetThresholdBytes(out var thresholdBytes))
        {
            WpfMessageBox.Show(this, "请输入有效的阈值，数值必须大于 0。", "阈值无效", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ClearFiles();
        ResetScanCounters();

        var rootPath = NormalizePath(FolderPathTextBox.Text);
        _activeScanRoot = rootPath;
        StartFileWatcher(rootPath);

        _scanCancellation = new CancellationTokenSource();
        SetScanningState(true);

        var excludeSystemDirectories = ExcludeSystemDirsCheckBox.IsChecked == true;
        var progress = new Progress<ScanProgress>(HandleScanProgress);

        try
        {
            StatusTextBlock.Text = "正在扫描: " + rootPath;

            var summary = await Task.Run(
                () => _scanner.Scan(rootPath, thresholdBytes, excludeSystemDirectories, progress, _scanCancellation.Token),
                _scanCancellation.Token);

            ApplyScanSummary(summary);
            if (!_isDeleting)
            {
                StatusTextBlock.Text = BuildScanCompletedMessage(summary);
            }
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "扫描已取消。";
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show(this, ex.Message, "扫描失败", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusTextBlock.Text = "扫描失败。";
        }
        finally
        {
            await ReconcileMissingFilesAsync(showStatus: false);
            _scanCancellation?.Dispose();
            _scanCancellation = null;
            SetScanningState(false);
            UpdateSummary();
        }
    }

    private void CancelScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isFindingDuplicates)
        {
            _duplicateCancellation?.Cancel();
            CancelScanButton.IsEnabled = false;
            StatusTextBlock.Text = "正在取消重复文件分析...";
            return;
        }

        _scanCancellation?.Cancel();
        CancelScanButton.IsEnabled = false;
        StatusTextBlock.Text = "正在取消扫描...";
    }

    private void SearchTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _filesView?.Refresh();
        UpdateSummary();
    }

    private void SelectVisibleButton_Click(object sender, RoutedEventArgs e)
    {
        SetFilesSelected(GetVisibleFiles().Where(file => !file.IsDuplicateKeeper), true);
    }

    private void ClearSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        SetFilesSelected(_files, false);
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        var operationLease = TryAcquireFileOperation(allowDuringScan: true, isMutation: true);
        if (operationLease is null)
        {
            return;
        }

        using var operationScope = operationLease;

        await ReconcileMissingFilesAsync(showStatus: false);

        var selectedFiles = _files.Where(file => file.IsSelected).ToList();
        if (selectedFiles.Count == 0)
        {
            WpfMessageBox.Show(this, "请先勾选要删除的文件。", "没有选择文件", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!TryBuildDuplicateRetentionGuards(selectedFiles, out var duplicateRetentionGuards, out var guardError))
        {
            WpfMessageBox.Show(this, guardError, "需要保留重复文件", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var deleteMode = GetSelectedDeleteMode();
        var selectedBytes = selectedFiles.Sum(file => file.SizeBytes);
        var readOnlyCount = CountReadOnlyFiles(selectedFiles);
        var modeText = deleteMode == DeleteMode.RecycleBin ? "移动到回收站" : "永久删除";
        var confirmIcon = deleteMode == DeleteMode.RecycleBin ? MessageBoxImage.Question : MessageBoxImage.Warning;
        var confirmMessage = $"将删除 {selectedFiles.Count:N0} 个文件，预计释放 {SizeFormatter.Format(selectedBytes)}。\n删除方式: {modeText}";

        if (_isScanning)
        {
            confirmMessage += "\n\n扫描仍在进行，当前只会删除已经扫描出来并勾选的文件，扫描会继续运行。";
        }

        if (readOnlyCount > 0)
        {
            confirmMessage += $"\n\n检测到 {readOnlyCount:N0} 个只读文件，继续后会先解除只读属性再删除。";
        }

        if (duplicateRetentionGuards.Count > 0)
        {
            var guardedGroupCount = selectedFiles
                .Where(file => duplicateRetentionGuards.ContainsKey(file.FullPath))
                .Select(file => file.DuplicateGroupId)
                .Distinct(StringComparer.Ordinal)
                .Count();

            confirmMessage +=
                $"\n\n其中 {duplicateRetentionGuards.Count:N0} 个文件来自 {guardedGroupCount:N0} 个重复组；删除前会再次逐字节核对保留项。";
        }

        confirmMessage += "\n\n是否继续？";

        var confirmResult = WpfMessageBox.Show(
            this,
            confirmMessage,
            "确认删除",
            MessageBoxButton.YesNo,
            confirmIcon);

        if (confirmResult != MessageBoxResult.Yes)
        {
            return;
        }

        SetDeletingState(true);
        var progress = new Progress<DeletionProgress>(HandleDeletionProgress);

        try
        {
            var result = await Task.Run(
                () => _deletionService.DeleteFiles(
                    selectedFiles,
                    deleteMode,
                    progress,
                    CancellationToken.None,
                    duplicateRetentionGuards));

            RemoveFilesByPaths(result.DeletedFiles.Concat(result.MissingFiles));

            var logEntry = new DeleteLogEntry(
                DateTimeOffset.Now,
                deleteMode.ToString(),
                selectedFiles.Count,
                result.DeletedFiles.Count,
                result.DeletedBytes,
                result.DeletedFiles,
                result.Failures);

            await _logService.AppendAsync(logEntry);

            StatusTextBlock.Text = BuildDeletionCompletedMessage(result);
            if (result.Failures.Count > 0)
            {
                WpfMessageBox.Show(
                    this,
                    $"删除完成，但有 {result.Failures.Count:N0} 个文件失败。详情已写入日志。\n{_logService.LogPath}",
                    "部分文件删除失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show(this, ex.Message, "删除失败", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusTextBlock.Text = "删除失败。";
        }
        finally
        {
            SetDeletingState(false);
            UpdateSummary();
        }
    }

    private void DuplicatesOnlyCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        _filesView?.Refresh();
        UpdateSummary();
    }

    private async void FindDuplicatesButton_Click(object sender, RoutedEventArgs e)
    {
        var operationLease = TryAcquireFileOperation(allowDuringScan: false, isMutation: false);
        if (operationLease is null)
        {
            return;
        }

        using var operationScope = operationLease;

        await ReconcileMissingFilesAsync(showStatus: false);
        if (_files.Count < 2)
        {
            WpfMessageBox.Show(this, "当前扫描结果不足 2 个文件，无法查找重复项。", "没有可分析的文件", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ClearDuplicateMetadata();

        var candidates = _files
            .Select(file => new DuplicateCandidate(file.FullPath, file.SizeBytes, file.ModifiedTimeUtc))
            .ToArray();
        var startingFilesVersion = _filesVersion;
        var progress = new Progress<DuplicateAnalysisProgress>(HandleDuplicateAnalysisProgress);

        var duplicateCancellation = new CancellationTokenSource();
        _duplicateCancellation = duplicateCancellation;
        SetFindingDuplicatesState(true);

        try
        {
            StatusTextBlock.Text = $"正在分析当前扫描结果中的 {candidates.Length:N0} 个文件...";
            var result = await Task.Run(
                () => _duplicateAnalyzer.Analyze(candidates, progress, duplicateCancellation.Token),
                duplicateCancellation.Token);

            if (_filesVersion != startingFilesVersion)
            {
                ClearDuplicateMetadata();
                StatusTextBlock.Text = "分析期间文件列表发生变化，结果未应用；请重新查找重复文件。";
                return;
            }

            ApplyDuplicateAnalysisResult(result);
        }
        catch (OperationCanceledException)
        {
            ClearDuplicateMetadata();
            StatusTextBlock.Text = "重复文件分析已取消。";
        }
        catch (Exception ex)
        {
            ClearDuplicateMetadata();
            WpfMessageBox.Show(this, ex.Message, "重复文件分析失败", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusTextBlock.Text = "重复文件分析失败。";
        }
        finally
        {
            if (ReferenceEquals(_duplicateCancellation, duplicateCancellation))
            {
                _duplicateCancellation = null;
            }

            duplicateCancellation.Dispose();
            SetFindingDuplicatesState(false);
        }
    }

    private void SelectDuplicateCopiesButton_Click(object sender, RoutedEventArgs e)
    {
        var groups = GetDuplicateGroups();
        if (groups.Count == 0)
        {
            return;
        }

        _suppressSummaryUpdates = true;
        try
        {
            foreach (var group in groups)
            {
                var keeper = group.FirstOrDefault(file => file.IsDuplicateKeeper) ??
                             group.OrderBy(file => file.FullPath, StringComparer.OrdinalIgnoreCase).First();

                foreach (var file in group)
                {
                    file.IsSelected = !ReferenceEquals(file, keeper);
                }
            }
        }
        finally
        {
            _suppressSummaryUpdates = false;
        }

        UpdateSummary();
        var selectedDuplicateCount = _files.Count(file => file.IsSelected && file.IsDuplicate);
        StatusTextBlock.Text =
            $"已在 {_duplicateGroupCount:N0} 个重复组中各保留一份，并勾选其余 {selectedDuplicateCount:N0} 个副本；可右键某个文件更换保留项。";
    }

    private void HandleDuplicateAnalysisProgress(DuplicateAnalysisProgress progress)
    {
        var byteProgress = progress.TotalBytes > 0
            ? $"，已读取 {SizeFormatter.Format(progress.ProcessedBytes)} / {SizeFormatter.Format(progress.TotalBytes)}"
            : string.Empty;

        StatusTextBlock.Text =
            $"正在分析重复文件 {progress.ProcessedFiles:N0}/{progress.TotalFiles:N0}{byteProgress}: {ShortenPath(progress.CurrentPath, 78)}";
    }

    private void ApplyDuplicateAnalysisResult(DuplicateAnalysisResult result)
    {
        var filesByPath = _files.ToDictionary(file => file.FullPath, StringComparer.OrdinalIgnoreCase);
        var applicableGroups = result.Groups
            .Select(group => new
            {
                Group = group,
                Files = group.FilePaths
                    .Where(filesByPath.ContainsKey)
                    .Select(path => filesByPath[path])
                    .OrderBy(file => file.FullPath, StringComparer.OrdinalIgnoreCase)
                    .ToList()
            })
            .Where(item => item.Files.Count > 1)
            .OrderByDescending(item => item.Group.SizeBytes)
            .ThenBy(item => item.Files[0].FullPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _suppressSummaryUpdates = true;
        using (_filesView.DeferRefresh())
        {
            try
            {
                foreach (var file in _files)
                {
                    if (file.IsDuplicate)
                    {
                        file.IsSelected = false;
                    }

                    file.SetDuplicateInfo(null);
                }

                for (var index = 0; index < applicableGroups.Count; index++)
                {
                    var group = applicableGroups[index];
                    var keeper = group.Files[0];

                    foreach (var file in group.Files)
                    {
                        file.SetDuplicateInfo(
                            group.Group.Id,
                            index + 1,
                            group.Files.Count,
                            ReferenceEquals(file, keeper));

                        if (ReferenceEquals(file, keeper))
                        {
                            file.IsSelected = false;
                        }
                    }
                }
            }
            finally
            {
                _suppressSummaryUpdates = false;
            }
        }

        _duplicateAnalysisCompleted = true;
        _duplicateGroupCount = applicableGroups.Count;
        var duplicateFileCount = applicableGroups.Sum(group => group.Files.Count);
        var reclaimableBytes = applicableGroups.Sum(group => group.Group.SizeBytes * (group.Files.Count - 1L));

        if (_duplicateGroupCount == 0)
        {
            DuplicatesOnlyCheckBox.IsChecked = false;
        }

        UpdateSummary();

        var failureText = result.Failures.Count > 0
            ? $"；另有 {result.Failures.Count:N0} 个文件因不存在、不可访问或已变化而跳过"
            : string.Empty;
        StatusTextBlock.Text =
            $"重复分析完成：找到 {_duplicateGroupCount:N0} 组、{duplicateFileCount:N0} 个重复文件，可清理副本的逻辑大小约 {SizeFormatter.Format(reclaimableBytes)}{failureText}。";
    }

    private void ClearDuplicateMetadata()
    {
        _suppressSummaryUpdates = true;
        using (_filesView.DeferRefresh())
        {
            try
            {
                foreach (var file in _files)
                {
                    if (file.IsDuplicate)
                    {
                        file.IsSelected = false;
                    }

                    file.SetDuplicateInfo(null);
                }
            }
            finally
            {
                _suppressSummaryUpdates = false;
            }
        }

        _duplicateAnalysisCompleted = false;
        _duplicateGroupCount = 0;
        DuplicatesOnlyCheckBox.IsChecked = false;
        UpdateSummary();
    }

    private void NormalizeDuplicateGroups()
    {
        if (!_duplicateAnalysisCompleted)
        {
            return;
        }

        var activeGroups = _files
            .Where(file => file.DuplicateGroupId is not null)
            .GroupBy(file => file.DuplicateGroupId!, StringComparer.Ordinal)
            .Select(group => group.OrderBy(file => file.FullPath, StringComparer.OrdinalIgnoreCase).ToList())
            .Where(group => group.Count > 1)
            .OrderByDescending(group => group[0].SizeBytes)
            .ThenBy(group => group[0].FullPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var activePaths = activeGroups
            .SelectMany(group => group)
            .Select(file => file.FullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        _suppressSummaryUpdates = true;
        using (_filesView.DeferRefresh())
        {
            try
            {
                foreach (var file in _files.Where(file => file.IsDuplicate && !activePaths.Contains(file.FullPath)))
                {
                    file.IsSelected = false;
                    file.SetDuplicateInfo(null);
                }

                for (var index = 0; index < activeGroups.Count; index++)
                {
                    var group = activeGroups[index];
                    var keeper = group.FirstOrDefault(file => file.IsDuplicateKeeper) ?? group[0];
                    var groupId = group[0].DuplicateGroupId!;

                    foreach (var file in group)
                    {
                        file.SetDuplicateInfo(groupId, index + 1, group.Count, ReferenceEquals(file, keeper));
                        if (ReferenceEquals(file, keeper))
                        {
                            file.IsSelected = false;
                        }
                    }
                }
            }
            finally
            {
                _suppressSummaryUpdates = false;
            }
        }

        _duplicateGroupCount = activeGroups.Count;
        if (_duplicateGroupCount == 0)
        {
            DuplicatesOnlyCheckBox.IsChecked = false;
        }
    }

    private List<List<LargeFileItem>> GetDuplicateGroups()
    {
        return _files
            .Where(file => file.DuplicateGroupId is not null)
            .GroupBy(file => file.DuplicateGroupId!, StringComparer.Ordinal)
            .Select(group => group.ToList())
            .Where(group => group.Count > 1)
            .ToList();
    }

    private bool TryBuildDuplicateRetentionGuards(
        IReadOnlyCollection<LargeFileItem> selectedFiles,
        out IReadOnlyDictionary<string, string> guards,
        out string error)
    {
        var selectedPaths = selectedFiles
            .Select(file => file.FullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in GetDuplicateGroups())
        {
            var selectedGroupFiles = group.Where(file => selectedPaths.Contains(file.FullPath)).ToList();
            if (selectedGroupFiles.Count == 0)
            {
                continue;
            }

            var keeper = group.SingleOrDefault(file => file.IsDuplicateKeeper);
            if (keeper is null)
            {
                guards = result;
                error = "有重复组没有有效的保留文件，请重新执行重复分析。";
                return false;
            }

            if (selectedPaths.Contains(keeper.FullPath))
            {
                guards = result;
                error = $"“{keeper.Name}”是重复组的保留文件。请取消勾选，或右键组内另一个文件将其设为保留项。";
                return false;
            }

            foreach (var file in selectedGroupFiles)
            {
                result[file.FullPath] = keeper.FullPath;
            }
        }

        guards = result;
        error = string.Empty;
        return true;
    }

    private void FilesGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        _contextClickedFile = null;
        _contextActionFiles = [];

        var row = FindVisualParent<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row?.Item is not LargeFileItem clickedFile)
        {
            return;
        }

        _contextClickedFile = clickedFile;
        if (!clickedFile.IsSelected)
        {
            SetFilesSelected(_files, false);
            clickedFile.IsSelected = true;
        }

        FilesGrid.SelectedItem = clickedFile;
        row.Focus();
    }

    private void FilesGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (e.CursorLeft < 0 && FilesGrid.CurrentItem is LargeFileItem keyboardTarget)
        {
            _contextClickedFile = keyboardTarget;
        }

        if (_contextClickedFile is null || !_files.Contains(_contextClickedFile))
        {
            e.Handled = true;
            return;
        }

        _contextActionFiles = _files.Where(file => file.IsSelected).ToList();
        if (_contextActionFiles.Count == 0)
        {
            _contextActionFiles = [_contextClickedFile];
        }

        MoveFilesMenuItem.Header = _contextActionFiles.Count == 1
            ? "移动到其他目录..."
            : $"移动所选的 {_contextActionFiles.Count:N0} 个文件...";
        MoveFilesMenuItem.IsEnabled = !_isScanning &&
                                      !_isPreparingFileOperation &&
                                      !_isDeleting &&
                                      !_isMoving &&
                                      !_isFindingDuplicates;

        KeepDuplicateMenuItem.Visibility = _contextClickedFile.IsDuplicate
            ? Visibility.Visible
            : Visibility.Collapsed;
        KeepDuplicateMenuItem.IsEnabled = !_contextClickedFile.IsDuplicateKeeper &&
                                          !_isScanning &&
                                          !_isDeleting &&
                                          !_isMoving &&
                                          !_isFindingDuplicates;
        KeepDuplicateMenuItem.Header = _contextClickedFile.IsDuplicateKeeper
            ? "此文件已是重复组保留项"
            : "将此文件设为重复组保留项";

        OpenContainingFolderMenuItem.Visibility = _contextActionFiles.Count == 1
            ? Visibility.Visible
            : Visibility.Collapsed;
        FilesContextMenuSeparator.Visibility = OpenContainingFolderMenuItem.Visibility;
    }

    private async void MoveFilesMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var operationLease = TryAcquireFileOperation(allowDuringScan: false, isMutation: true);
        if (operationLease is null)
        {
            return;
        }

        using var operationScope = operationLease;

        await ReconcileMissingFilesAsync(showStatus: false);
        var filesToMove = _contextActionFiles
            .Where(file => _files.Contains(file))
            .ToList();
        if (filesToMove.Count == 0)
        {
            return;
        }

        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = $"选择 {filesToMove.Count:N0} 个文件要移动到的目录（同名文件不会被覆盖）",
            ShowNewFolderButton = true,
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(FolderPathTextBox.Text) ? FolderPathTextBox.Text : string.Empty
        };

        if (dialog.ShowDialog() != Forms.DialogResult.OK)
        {
            return;
        }

        var destinationDirectory = dialog.SelectedPath;
        var selectedBytes = filesToMove.Sum(file => file.SizeBytes);
        var confirmResult = WpfMessageBox.Show(
            this,
            $"将移动 {filesToMove.Count:N0} 个文件（{SizeFormatter.Format(selectedBytes)}）到：\n{destinationDirectory}\n\n目标中若有同名文件将跳过，不会覆盖。是否继续？",
            "确认移动文件",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirmResult != MessageBoxResult.Yes)
        {
            return;
        }

        SetMovingState(true);
        var progress = new Progress<FileMoveProgress>(moveProgress =>
        {
            StatusTextBlock.Text =
                $"正在移动 {moveProgress.ProcessedCount:N0}/{moveProgress.TotalCount:N0}: {ShortenPath(moveProgress.CurrentPath, 88)}";
        });

        try
        {
            var result = await Task.Run(
                () => _moveService.MoveFiles(filesToMove, destinationDirectory, progress, CancellationToken.None));

            if (result.MovedFiles.Count > 0)
            {
                ClearDuplicateMetadata();
            }

            RemoveFilesByPaths(
                result.MovedFiles.Select(file => file.SourcePath).Concat(result.MissingFiles));

            foreach (var movedFile in result.MovedFiles)
            {
                if (_activeScanRoot is null ||
                    !IsSameOrChildPath(movedFile.DestinationPath, _activeScanRoot) ||
                    IsDefinitelyMissing(movedFile.DestinationPath))
                {
                    continue;
                }

                try
                {
                    AddFile(LargeFileItem.FromFileInfo(new FileInfo(movedFile.DestinationPath)));
                }
                catch
                {
                    // A later rescan can recover a destination that changed again immediately after the move.
                }
            }

            _filesView.Refresh();
            UpdateSummary();
            StatusTextBlock.Text = BuildMoveCompletedMessage(result);

            if (result.Failures.Count > 0)
            {
                var details = string.Join(
                    "\n",
                    result.Failures.Take(5).Select(failure => $"• {Path.GetFileName(failure.SourcePath)}：{failure.Error}"));
                var more = result.Failures.Count > 5 ? $"\n另有 {result.Failures.Count - 5:N0} 个失败项。" : string.Empty;
                WpfMessageBox.Show(
                    this,
                    $"有 {result.Failures.Count:N0} 个文件未能移动：\n\n{details}{more}",
                    "部分文件移动失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show(this, ex.Message, "移动文件失败", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusTextBlock.Text = "移动文件失败。";
        }
        finally
        {
            SetMovingState(false);
        }
    }

    private void KeepDuplicateMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var keeper = _contextClickedFile;
        if (keeper?.DuplicateGroupId is null || keeper.IsDuplicateKeeper)
        {
            return;
        }

        var group = _files
            .Where(file => string.Equals(file.DuplicateGroupId, keeper.DuplicateGroupId, StringComparison.Ordinal))
            .OrderBy(file => file.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (group.Count < 2)
        {
            NormalizeDuplicateGroups();
            return;
        }

        _suppressSummaryUpdates = true;
        try
        {
            foreach (var file in group)
            {
                file.SetDuplicateInfo(keeper.DuplicateGroupId, 0, group.Count, ReferenceEquals(file, keeper));
                file.IsSelected = !ReferenceEquals(file, keeper);
            }
        }
        finally
        {
            _suppressSummaryUpdates = false;
        }

        NormalizeDuplicateGroups();
        _filesView.Refresh();
        UpdateSummary();
        StatusTextBlock.Text = $"已将“{keeper.Name}”设为该重复组的保留文件，并勾选组内其余副本。";
    }

    private void OpenContainingFolderMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_contextActionFiles.Count != 1)
        {
            return;
        }

        var file = _contextActionFiles[0];
        var directoryPath = file.DirectoryPath;
        if (IsDefinitelyMissing(directoryPath))
        {
            RemoveFilesByPaths([file.FullPath]);
            WpfMessageBox.Show(this, "文件及其所在目录已不存在，列表已刷新。", "无法打开目录", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "explorer.exe",
                UseShellExecute = true
            };
            startInfo.ArgumentList.Add(directoryPath);
            Process.Start(startInfo);

            if (IsDefinitelyMissing(file.FullPath))
            {
                RemoveFilesByPaths([file.FullPath]);
                StatusTextBlock.Text = "文件已不存在，已打开原所在目录并刷新列表。";
            }
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show(this, ex.Message, "无法打开目录", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void HandleScanProgress(ScanProgress progress)
    {
        _lastVisitedFiles = progress.VisitedFiles;
        _lastVisitedDirectories = progress.VisitedDirectories;
        _lastSkippedDirectories = progress.SkippedDirectories;
        _lastInaccessibleDirectories = progress.InaccessibleDirectories;

        if (progress.FoundFile is not null)
        {
            AddFile(progress.FoundFile);
        }

        if (!_isDeleting)
        {
            StatusTextBlock.Text = "正在扫描: " + ShortenPath(progress.CurrentPath, 96);
        }

        UpdateScanStats();
        UpdateSummary();
    }

    private void HandleDeletionProgress(DeletionProgress progress)
    {
        StatusTextBlock.Text = $"正在删除 {progress.ProcessedCount:N0}/{progress.TotalCount:N0}: {ShortenPath(progress.CurrentPath, 92)}";
    }

    private bool FilterFile(object candidate)
    {
        if (candidate is not LargeFileItem item)
        {
            return false;
        }

        if (DuplicatesOnlyCheckBox?.IsChecked == true && !item.IsDuplicate)
        {
            return false;
        }

        var query = SearchTextBox?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        return item.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               item.FullPath.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               item.Extension.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void AddFile(LargeFileItem file)
    {
        if (IsDefinitelyMissing(file.FullPath) ||
            _files.Any(existing => string.Equals(existing.FullPath, file.FullPath, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        file.PropertyChanged += File_PropertyChanged;
        _files.Add(file);
        _filesVersion++;
    }

    private void ClearFiles()
    {
        foreach (var file in _files)
        {
            file.PropertyChanged -= File_PropertyChanged;
        }

        _files.Clear();
        _filesVersion++;
        _duplicateAnalysisCompleted = false;
        _duplicateGroupCount = 0;
        DuplicatesOnlyCheckBox.IsChecked = false;
    }

    private void File_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LargeFileItem.IsSelected))
        {
            if (!_suppressSummaryUpdates)
            {
                UpdateSummary();
            }
        }
    }

    private int RemoveFilesByPaths(IEnumerable<string> removedFiles)
    {
        var removedPathSet = removedFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (removedPathSet.Count == 0)
        {
            return 0;
        }

        var filesToRemove = _files.Where(file => removedPathSet.Contains(file.FullPath)).ToList();
        foreach (var file in filesToRemove)
        {
            file.PropertyChanged -= File_PropertyChanged;
            _files.Remove(file);
        }

        if (filesToRemove.Count > 0)
        {
            _filesVersion++;
            NormalizeDuplicateGroups();
            _filesView.Refresh();
            UpdateSummary();
        }

        return filesToRemove.Count;
    }

    private void UpdateSummary()
    {
        var selectedFiles = _files.Where(file => file.IsSelected).ToList();
        var visibleCount = GetVisibleFiles().Count();
        var fileOperationBusy = _isPreparingFileOperation || _isDeleting || _isMoving || _isFindingDuplicates;

        MatchedCountTextBlock.Text = _files.Count.ToString("N0", CultureInfo.CurrentCulture);
        VisibleCountTextBlock.Text = visibleCount.ToString("N0", CultureInfo.CurrentCulture);
        SelectedCountTextBlock.Text = selectedFiles.Count.ToString("N0", CultureInfo.CurrentCulture);
        SelectedSizeTextBlock.Text = SizeFormatter.Format(selectedFiles.Sum(file => file.SizeBytes));

        DeleteButton.IsEnabled = !fileOperationBusy && selectedFiles.Count > 0;
        SelectVisibleButton.IsEnabled = !fileOperationBusy && visibleCount > 0;
        ClearSelectionButton.IsEnabled = !fileOperationBusy && selectedFiles.Count > 0;
        FindDuplicatesButton.IsEnabled = !_isScanning &&
                                         !fileOperationBusy &&
                                         _files.Count > 1;
        DuplicatesOnlyCheckBox.IsEnabled = !fileOperationBusy && _duplicateGroupCount > 0;
        SelectDuplicateCopiesButton.IsEnabled = !_isScanning &&
                                                !fileOperationBusy &&
                                                _duplicateGroupCount > 0;
    }

    private IEnumerable<LargeFileItem> GetVisibleFiles()
    {
        return _filesView?.OfType<LargeFileItem>() ?? [];
    }

    private void SetFilesSelected(IEnumerable<LargeFileItem> files, bool isSelected)
    {
        var snapshot = files.Distinct().ToList();
        _suppressSummaryUpdates = true;

        try
        {
            foreach (var file in snapshot)
            {
                file.IsSelected = isSelected;
            }
        }
        finally
        {
            _suppressSummaryUpdates = false;
        }

        UpdateSummary();
    }

    private async void Window_Activated(object? sender, EventArgs e)
    {
        if (_isClosed || _files.Count == 0)
        {
            return;
        }

        await ReconcileMissingFilesAsync(showStatus: true);
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_isDeleting && !_isMoving && !_isPreparingMutation)
        {
            return;
        }

        e.Cancel = true;
        WpfMessageBox.Show(
            this,
            "文件删除或移动操作尚未结束。为避免只处理一部分文件，请等待操作完成后再关闭窗口。",
            "文件操作进行中",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _isClosed = true;
        _scanCancellation?.Cancel();
        _duplicateCancellation?.Cancel();
        _reconcileCancellation?.Cancel();
        _watcherDebounceTimer.Stop();
        StopFileWatcher();
    }

    private void StartFileWatcher(string rootPath)
    {
        StopFileWatcher();
        if (!Directory.Exists(rootPath) || _isClosed)
        {
            return;
        }

        var generation = Volatile.Read(ref _watcherGeneration);
        FileSystemWatcher? watcher = null;

        try
        {
            _watcherCheckCancellation = new CancellationTokenSource();
            watcher = new FileSystemWatcher(rootPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                InternalBufferSize = 64 * 1024
            };
            watcher.Deleted += (_, args) => QueueRemovedPath(args.FullPath, generation);
            watcher.Renamed += (_, args) => QueueRemovedPath(args.OldFullPath, generation);
            watcher.Error += (_, _) => QueueWatcherRecovery(generation);

            _fileWatcher = watcher;
            watcher.EnableRaisingEvents = true;
        }
        catch
        {
            _watcherCheckCancellation?.Dispose();
            _watcherCheckCancellation = null;
            watcher?.Dispose();
            _fileWatcher = null;
            // Window activation and scan-completion reconciliation remain available as a fallback.
        }
    }

    private void StopFileWatcher()
    {
        Interlocked.Increment(ref _watcherGeneration);
        _watcherCheckCancellation?.Cancel();
        _watcherCheckCancellation?.Dispose();
        _watcherCheckCancellation = null;
        _watcherDebounceTimer.Stop();

        lock (_watcherSync)
        {
            _pendingRemovedPaths.Clear();
        }

        if (_fileWatcher is null)
        {
            return;
        }

        try
        {
            _fileWatcher.EnableRaisingEvents = false;
        }
        catch
        {
            // The watched drive may already have been disconnected.
        }

        _fileWatcher.Dispose();
        _fileWatcher = null;
    }

    private void QueueRemovedPath(string path, int generation)
    {
        lock (_watcherSync)
        {
            if (_isClosed || generation != Volatile.Read(ref _watcherGeneration))
            {
                return;
            }

            _pendingRemovedPaths.Add(path);
        }

        try
        {
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() =>
                {
                    if (_isClosed || generation != Volatile.Read(ref _watcherGeneration))
                    {
                        return;
                    }

                    _watcherDebounceTimer.Stop();
                    _watcherDebounceTimer.Start();
                }));
        }
        catch (TaskCanceledException)
        {
            // The application is shutting down.
        }
    }

    private void QueueWatcherRecovery(int generation)
    {
        try
        {
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(async () =>
                {
                    if (_isClosed || generation != Volatile.Read(ref _watcherGeneration))
                    {
                        return;
                    }

                    if (_activeScanRoot is null)
                    {
                        return;
                    }

                    var rootPath = _activeScanRoot;
                    StartFileWatcher(rootPath);
                    await ReconcileMissingFilesAsync(showStatus: false);
                }));
        }
        catch (TaskCanceledException)
        {
            // The application is shutting down.
        }
    }

    private async void WatcherDebounceTimer_Tick(object? sender, EventArgs e)
    {
        _watcherDebounceTimer.Stop();
        string[] removedPaths;
        var generation = Volatile.Read(ref _watcherGeneration);
        var cancellationToken = _watcherCheckCancellation?.Token ?? CancellationToken.None;

        lock (_watcherSync)
        {
            removedPaths = _pendingRemovedPaths.ToArray();
            _pendingRemovedPaths.Clear();
        }

        if (removedPaths.Length == 0 || generation != Volatile.Read(ref _watcherGeneration))
        {
            return;
        }

        var candidatePaths = _files
            .Where(file => removedPaths.Any(path => IsSameOrChildPath(file.FullPath, path)))
            .Select(file => file.FullPath)
            .ToArray();

        int removedCount;
        try
        {
            removedCount = await RemoveDefinitelyMissingFilesAsync(
                candidatePaths,
                generation,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (removedCount > 0 && !_isScanning && !_isDeleting && !_isMoving && !_isFindingDuplicates)
        {
            StatusTextBlock.Text = $"检测到 {removedCount:N0} 个文件已在程序外删除或移动，列表已刷新。";
        }
    }

    private async Task<int> ReconcileMissingFilesAsync(bool showStatus)
    {
        if (_isClosed || _files.Count == 0)
        {
            return 0;
        }

        _reconcileCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _reconcileCancellation = cancellation;
        var generation = Volatile.Read(ref _watcherGeneration);
        var paths = _files.Select(file => file.FullPath).ToArray();

        try
        {
            var removedCount = await RemoveDefinitelyMissingFilesAsync(paths, generation, cancellation.Token);
            if (showStatus &&
                removedCount > 0 &&
                !_isScanning &&
                !_isDeleting &&
                !_isMoving &&
                !_isFindingDuplicates)
            {
                StatusTextBlock.Text = $"检测到 {removedCount:N0} 个文件已不存在，列表已刷新。";
            }

            return removedCount;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        finally
        {
            if (ReferenceEquals(_reconcileCancellation, cancellation))
            {
                _reconcileCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private async Task<int> RemoveDefinitelyMissingFilesAsync(
        IReadOnlyCollection<string> paths,
        int generation,
        CancellationToken cancellationToken)
    {
        if (paths.Count == 0)
        {
            return 0;
        }

        var missingPaths = await Task.Run(
            () => paths
                .Where(path =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return IsDefinitelyMissing(path);
                })
                .ToArray(),
            cancellationToken);

        if (_isClosed || generation != Volatile.Read(ref _watcherGeneration))
        {
            return 0;
        }

        var stillMissingPaths = missingPaths.Where(IsDefinitelyMissing).ToArray();
        return RemoveFilesByPaths(stillMissingPaths);
    }

    private static bool IsDefinitelyMissing(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return false;
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static T? FindVisualParent<T>(DependencyObject? source)
        where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match)
            {
                return match;
            }

            try
            {
                source = VisualTreeHelper.GetParent(source);
            }
            catch (InvalidOperationException)
            {
                source = LogicalTreeHelper.GetParent(source);
            }
        }

        return null;
    }

    private void UpdateScanStats()
    {
        ScanStatsTextBlock.Text =
            $"已扫描 {_lastVisitedFiles:N0} 个文件，{_lastVisitedDirectories:N0} 个目录；跳过 {_lastSkippedDirectories:N0} 个目录，不可访问 {_lastInaccessibleDirectories:N0} 项。";
    }

    private void ApplyScanSummary(ScanSummary summary)
    {
        _lastVisitedFiles = summary.VisitedFiles;
        _lastVisitedDirectories = summary.VisitedDirectories;
        _lastSkippedDirectories = summary.SkippedDirectories;
        _lastInaccessibleDirectories = summary.InaccessibleDirectories;
        UpdateScanStats();
    }

    private void ResetScanCounters()
    {
        _lastVisitedFiles = 0;
        _lastVisitedDirectories = 0;
        _lastSkippedDirectories = 0;
        _lastInaccessibleDirectories = 0;
        UpdateScanStats();
    }

    private void SetScanningState(bool isScanning)
    {
        _isScanning = isScanning;
        UpdateBusyState();
    }

    private void SetDeletingState(bool isDeleting)
    {
        _isDeleting = isDeleting;
        UpdateBusyState();
    }

    private void SetMovingState(bool isMoving)
    {
        _isMoving = isMoving;
        UpdateBusyState();
    }

    private void SetFindingDuplicatesState(bool isFindingDuplicates)
    {
        _isFindingDuplicates = isFindingDuplicates;
        UpdateBusyState();
    }

    private IDisposable? TryAcquireFileOperation(bool allowDuringScan, bool isMutation)
    {
        if (_isPreparingFileOperation ||
            _isDeleting ||
            _isMoving ||
            _isFindingDuplicates ||
            (!allowDuringScan && _isScanning))
        {
            return null;
        }

        _isPreparingFileOperation = true;
        _isPreparingMutation = isMutation;
        UpdateBusyState();
        return new ActionOnDispose(() =>
        {
            _isPreparingFileOperation = false;
            _isPreparingMutation = false;
            UpdateBusyState();
        });
    }

    private void UpdateBusyState()
    {
        var busy = _isScanning || _isPreparingFileOperation || _isDeleting || _isMoving || _isFindingDuplicates;
        var listBusy = _isPreparingFileOperation || _isDeleting || _isMoving || _isFindingDuplicates;

        SelectFolderButton.IsEnabled = !busy;
        FolderPathTextBox.IsEnabled = !busy;
        ThresholdTextBox.IsEnabled = !busy;
        ThresholdUnitComboBox.IsEnabled = !busy;
        DeleteModeComboBox.IsEnabled = !listBusy;
        ExcludeSystemDirsCheckBox.IsEnabled = !busy;
        SearchTextBox.IsEnabled = !listBusy;
        ScanButton.IsEnabled = !busy;
        CancelScanButton.IsEnabled = _isScanning || _isFindingDuplicates;
        FilesGrid.IsEnabled = !listBusy;

        ScanButton.Content = _isScanning ? "扫描中..." : "开始扫描";
        DeleteButton.Content = _isDeleting ? "处理中..." : "删除所选";
        FindDuplicatesButton.Content = _isFindingDuplicates ? "分析中..." : "查找重复文件";
        CancelScanButton.Content = _isFindingDuplicates ? "取消查重" : "取消";

        UpdateSummary();
    }

    private bool TryGetThresholdBytes(out long thresholdBytes)
    {
        thresholdBytes = 0;

        if (!decimal.TryParse(ThresholdTextBox.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var value) ||
            value <= 0)
        {
            return false;
        }

        var unit = (ThresholdUnitComboBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString();
        var multiplier = unit == "GB" ? 1024m * 1024m * 1024m : 1024m * 1024m;
        var bytes = value * multiplier;

        if (bytes > long.MaxValue)
        {
            return false;
        }

        thresholdBytes = (long)Math.Ceiling(bytes);
        return true;
    }

    private DeleteMode GetSelectedDeleteMode()
    {
        var selectedTag = (DeleteModeComboBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString();
        return selectedTag == "Permanent" ? DeleteMode.Permanent : DeleteMode.RecycleBin;
    }

    private string BuildScanCompletedMessage(ScanSummary summary)
    {
        var currentCount = _files.Count;
        var currentBytes = _files.Sum(file => file.SizeBytes);

        if (currentCount == summary.MatchedFiles && currentBytes == summary.MatchedBytes)
        {
            return $"扫描完成，找到 {summary.MatchedFiles:N0} 个大文件，总大小 {SizeFormatter.Format(summary.MatchedBytes)}。";
        }

        return $"扫描完成，共发现 {summary.MatchedFiles:N0} 个大文件；当前列表保留 {currentCount:N0} 个，总大小 {SizeFormatter.Format(currentBytes)}。";
    }

    private string BuildDeletionCompletedMessage(DeletionResult result)
    {
        var message = $"删除完成，已处理 {result.DeletedFiles.Count:N0} 个文件，释放 {SizeFormatter.Format(result.DeletedBytes)}。";

        if (result.MissingFiles.Count > 0)
        {
            message += $" 另有 {result.MissingFiles.Count:N0} 个文件此前已不存在，已从列表移除。";
        }

        if (_isScanning)
        {
            message += " 扫描仍在继续。";
        }

        return message;
    }

    private static string BuildMoveCompletedMessage(FileMoveResult result)
    {
        var message =
            $"移动完成，成功移动 {result.MovedFiles.Count:N0} 个文件（{SizeFormatter.Format(result.MovedBytes)}）。";

        if (result.MissingFiles.Count > 0)
        {
            message += $" {result.MissingFiles.Count:N0} 个文件此前已不存在，已从列表移除。";
        }

        if (result.Failures.Count > 0)
        {
            message += $" {result.Failures.Count:N0} 个文件移动失败。";
        }

        return message;
    }

    private static int CountReadOnlyFiles(IEnumerable<LargeFileItem> files)
    {
        var count = 0;

        foreach (var file in files)
        {
            try
            {
                if (File.Exists(file.FullPath) &&
                    File.GetAttributes(file.FullPath).HasFlag(FileAttributes.ReadOnly))
                {
                    count++;
                }
            }
            catch
            {
                // Ignore files that cannot be inspected; deletion will report them normally if they fail.
            }
        }

        return count;
    }

    private static string ShortenPath(string path, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length <= maxLength)
        {
            return path;
        }

        var headLength = Math.Max(12, maxLength / 2 - 2);
        var tailLength = Math.Max(12, maxLength - headLength - 3);
        return path[..headLength] + "..." + path[^tailLength..];
    }

    private static bool IsSameOrChildPath(string path, string parentPath)
    {
        try
        {
            var normalizedPath = NormalizePath(path);
            var normalizedParent = NormalizePath(parentPath);
            if (string.Equals(normalizedPath, normalizedParent, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var parentPrefix = normalizedParent.EndsWith(Path.DirectorySeparatorChar)
                ? normalizedParent
                : normalizedParent + Path.DirectorySeparatorChar;
            return normalizedPath.StartsWith(parentPrefix, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);

        if (!string.IsNullOrEmpty(root) &&
            string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
        {
            return root;
        }

        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private sealed class ActionOnDispose(Action action) : IDisposable
    {
        private Action? _action = action;

        public void Dispose()
        {
            Interlocked.Exchange(ref _action, null)?.Invoke();
        }
    }
}

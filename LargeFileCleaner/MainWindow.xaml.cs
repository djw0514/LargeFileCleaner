using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
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

    private ICollectionView _filesView = null!;
    private CancellationTokenSource? _scanCancellation;
    private bool _isScanning;
    private bool _isDeleting;
    private long _lastVisitedFiles;
    private long _lastVisitedDirectories;
    private long _lastSkippedDirectories;
    private long _lastInaccessibleDirectories;

    public MainWindow()
    {
        InitializeComponent();
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
        if (_isScanning || _isDeleting)
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

        _scanCancellation = new CancellationTokenSource();
        SetScanningState(true);

        var rootPath = FolderPathTextBox.Text;
        var excludeSystemDirectories = ExcludeSystemDirsCheckBox.IsChecked == true;
        var progress = new Progress<ScanProgress>(HandleScanProgress);

        try
        {
            StatusTextBlock.Text = "正在扫描: " + rootPath;

            var summary = await Task.Run(
                () => _scanner.Scan(rootPath, thresholdBytes, excludeSystemDirectories, progress, _scanCancellation.Token),
                _scanCancellation.Token);

            ApplyScanSummary(summary);
            StatusTextBlock.Text = $"扫描完成，找到 {summary.MatchedFiles:N0} 个大文件，总大小 {SizeFormatter.Format(summary.MatchedBytes)}。";
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
            _scanCancellation?.Dispose();
            _scanCancellation = null;
            SetScanningState(false);
            UpdateSummary();
        }
    }

    private void CancelScanButton_Click(object sender, RoutedEventArgs e)
    {
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
        foreach (var item in _filesView.Cast<LargeFileItem>())
        {
            item.IsSelected = true;
        }

        UpdateSummary();
    }

    private void ClearSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _files)
        {
            item.IsSelected = false;
        }

        UpdateSummary();
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isScanning || _isDeleting)
        {
            return;
        }

        var selectedFiles = _files.Where(file => file.IsSelected).ToList();
        if (selectedFiles.Count == 0)
        {
            WpfMessageBox.Show(this, "请先勾选要删除的文件。", "没有选择文件", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var deleteMode = GetSelectedDeleteMode();
        var selectedBytes = selectedFiles.Sum(file => file.SizeBytes);
        var modeText = deleteMode == DeleteMode.RecycleBin ? "移动到回收站" : "永久删除";
        var confirmIcon = deleteMode == DeleteMode.RecycleBin ? MessageBoxImage.Question : MessageBoxImage.Warning;

        var confirmResult = WpfMessageBox.Show(
            this,
            $"将删除 {selectedFiles.Count:N0} 个文件，预计释放 {SizeFormatter.Format(selectedBytes)}。\n删除方式: {modeText}\n\n是否继续？",
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
                () => _deletionService.DeleteFiles(selectedFiles, deleteMode, progress, CancellationToken.None));

            RemoveDeletedFiles(result.DeletedFiles);

            var logEntry = new DeleteLogEntry(
                DateTimeOffset.Now,
                deleteMode.ToString(),
                selectedFiles.Count,
                result.DeletedFiles.Count,
                result.DeletedBytes,
                result.DeletedFiles,
                result.Failures);

            await _logService.AppendAsync(logEntry);

            StatusTextBlock.Text = $"删除完成，已处理 {result.DeletedFiles.Count:N0} 个文件，释放 {SizeFormatter.Format(result.DeletedBytes)}。";
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

        StatusTextBlock.Text = "正在扫描: " + ShortenPath(progress.CurrentPath, 96);
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
        file.PropertyChanged += File_PropertyChanged;
        _files.Add(file);
    }

    private void ClearFiles()
    {
        foreach (var file in _files)
        {
            file.PropertyChanged -= File_PropertyChanged;
        }

        _files.Clear();
    }

    private void File_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LargeFileItem.IsSelected))
        {
            UpdateSummary();
        }
    }

    private void RemoveDeletedFiles(IReadOnlyCollection<string> deletedFiles)
    {
        var deletedPathSet = deletedFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var file in _files.Where(file => deletedPathSet.Contains(file.FullPath)).ToList())
        {
            file.PropertyChanged -= File_PropertyChanged;
            _files.Remove(file);
        }
    }

    private void UpdateSummary()
    {
        var selectedFiles = _files.Where(file => file.IsSelected).ToList();
        var visibleCount = _filesView?.Cast<object>().Count() ?? 0;

        MatchedCountTextBlock.Text = _files.Count.ToString("N0", CultureInfo.CurrentCulture);
        VisibleCountTextBlock.Text = visibleCount.ToString("N0", CultureInfo.CurrentCulture);
        SelectedCountTextBlock.Text = selectedFiles.Count.ToString("N0", CultureInfo.CurrentCulture);
        SelectedSizeTextBlock.Text = SizeFormatter.Format(selectedFiles.Sum(file => file.SizeBytes));

        DeleteButton.IsEnabled = !_isScanning && !_isDeleting && selectedFiles.Count > 0;
        SelectVisibleButton.IsEnabled = !_isScanning && !_isDeleting && visibleCount > 0;
        ClearSelectionButton.IsEnabled = !_isScanning && !_isDeleting && selectedFiles.Count > 0;
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

    private void UpdateBusyState()
    {
        var busy = _isScanning || _isDeleting;

        SelectFolderButton.IsEnabled = !busy;
        FolderPathTextBox.IsEnabled = !busy;
        ThresholdTextBox.IsEnabled = !busy;
        ThresholdUnitComboBox.IsEnabled = !busy;
        DeleteModeComboBox.IsEnabled = !busy;
        ExcludeSystemDirsCheckBox.IsEnabled = !busy;
        SearchTextBox.IsEnabled = !busy || _files.Count > 0;
        ScanButton.IsEnabled = !busy;
        CancelScanButton.IsEnabled = _isScanning;
        FilesGrid.IsEnabled = !busy || _isScanning;

        ScanButton.Content = _isScanning ? "扫描中..." : "开始扫描";
        DeleteButton.Content = _isDeleting ? "处理中..." : "删除所选";

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
}

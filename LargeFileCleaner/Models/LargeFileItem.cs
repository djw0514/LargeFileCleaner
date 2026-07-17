using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using LargeFileCleaner.Utilities;

namespace LargeFileCleaner.Models;

public sealed class LargeFileItem : INotifyPropertyChanged
{
    private bool _isSelected;
    private string? _duplicateGroupId;
    private int _duplicateGroupIndex;
    private int _duplicateGroupCount;
    private bool _isDuplicateKeeper;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public required string Name { get; init; }

    public required string FullPath { get; init; }

    public required string DirectoryPath { get; init; }

    public required string Extension { get; init; }

    public long SizeBytes { get; init; }

    public DateTime ModifiedTime { get; init; }

    public DateTime ModifiedTimeUtc { get; init; }

    public string SizeText => SizeFormatter.Format(SizeBytes);

    public string? DuplicateGroupId => _duplicateGroupId;

    public bool IsDuplicate => _duplicateGroupId is not null;

    public bool IsDuplicateKeeper => _isDuplicateKeeper;

    public string DuplicateStatusText
    {
        get
        {
            if (!IsDuplicate)
            {
                return string.Empty;
            }

            var action = IsDuplicateKeeper ? "保留" : "可清理";
            return $"组 {_duplicateGroupIndex} · {action}（{_duplicateGroupCount} 个）";
        }
    }

    public static LargeFileItem FromFileInfo(FileInfo fileInfo)
    {
        return new LargeFileItem
        {
            Name = fileInfo.Name,
            FullPath = fileInfo.FullName,
            DirectoryPath = fileInfo.DirectoryName ?? string.Empty,
            Extension = string.IsNullOrWhiteSpace(fileInfo.Extension) ? "(none)" : fileInfo.Extension,
            SizeBytes = fileInfo.Length,
            ModifiedTime = fileInfo.LastWriteTime,
            ModifiedTimeUtc = fileInfo.LastWriteTimeUtc
        };
    }

    public void SetDuplicateInfo(string? groupId, int groupIndex = 0, int groupCount = 0, bool isKeeper = false)
    {
        if (_duplicateGroupId == groupId &&
            _duplicateGroupIndex == groupIndex &&
            _duplicateGroupCount == groupCount &&
            _isDuplicateKeeper == isKeeper)
        {
            return;
        }

        _duplicateGroupId = groupId;
        _duplicateGroupIndex = groupIndex;
        _duplicateGroupCount = groupCount;
        _isDuplicateKeeper = isKeeper;

        OnPropertyChanged(nameof(DuplicateGroupId));
        OnPropertyChanged(nameof(IsDuplicate));
        OnPropertyChanged(nameof(IsDuplicateKeeper));
        OnPropertyChanged(nameof(DuplicateStatusText));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

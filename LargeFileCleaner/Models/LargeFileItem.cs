using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using LargeFileCleaner.Utilities;

namespace LargeFileCleaner.Models;

public sealed class LargeFileItem : INotifyPropertyChanged
{
    private bool _isSelected;

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

    public string SizeText => SizeFormatter.Format(SizeBytes);

    public static LargeFileItem FromFileInfo(FileInfo fileInfo)
    {
        return new LargeFileItem
        {
            Name = fileInfo.Name,
            FullPath = fileInfo.FullName,
            DirectoryPath = fileInfo.DirectoryName ?? string.Empty,
            Extension = string.IsNullOrWhiteSpace(fileInfo.Extension) ? "(none)" : fileInfo.Extension,
            SizeBytes = fileInfo.Length,
            ModifiedTime = fileInfo.LastWriteTime
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

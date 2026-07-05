using System.IO;
using LargeFileCleaner.Models;
using Microsoft.VisualBasic.FileIO;

namespace LargeFileCleaner.Services;

public enum DeleteMode
{
    RecycleBin,
    Permanent
}

public sealed class FileDeletionService
{
    public DeletionResult DeleteFiles(
        IReadOnlyList<LargeFileItem> files,
        DeleteMode mode,
        IProgress<DeletionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var result = new DeletionResult();

        for (var index = 0; index < files.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var file = files[index];

            try
            {
                if (!File.Exists(file.FullPath))
                {
                    result.Failures.Add(new DeletionFailure(file.FullPath, "文件不存在"));
                }
                else
                {
                    DeleteSingleFile(file.FullPath, mode);
                    result.DeletedFiles.Add(file.FullPath);
                    result.DeletedBytes += file.SizeBytes;
                }
            }
            catch (Exception ex)
            {
                result.Failures.Add(new DeletionFailure(file.FullPath, ex.Message));
            }

            progress?.Report(new DeletionProgress(index + 1, files.Count, file.FullPath));
        }

        return result;
    }

    private static void DeleteSingleFile(string path, DeleteMode mode)
    {
        if (mode == DeleteMode.RecycleBin)
        {
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            return;
        }

        File.Delete(path);
    }
}

public sealed class DeletionResult
{
    public List<string> DeletedFiles { get; } = [];

    public long DeletedBytes { get; set; }

    public List<DeletionFailure> Failures { get; } = [];
}

public sealed record DeletionFailure(string Path, string Error);

public sealed record DeletionProgress(int ProcessedCount, int TotalCount, string CurrentPath);

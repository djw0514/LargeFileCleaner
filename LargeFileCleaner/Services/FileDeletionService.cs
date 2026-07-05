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
        var originalAttributes = File.GetAttributes(path);
        var wasReadOnly = originalAttributes.HasFlag(FileAttributes.ReadOnly);

        if (wasReadOnly)
        {
            File.SetAttributes(path, originalAttributes & ~FileAttributes.ReadOnly);
        }

        try
        {
            if (mode == DeleteMode.RecycleBin)
            {
                FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                return;
            }

            File.Delete(path);
        }
        catch
        {
            RestoreAttributes(path, originalAttributes, wasReadOnly);
            throw;
        }
    }

    private static void RestoreAttributes(string path, FileAttributes originalAttributes, bool shouldRestore)
    {
        if (!shouldRestore || !File.Exists(path))
        {
            return;
        }

        try
        {
            File.SetAttributes(path, originalAttributes);
        }
        catch
        {
            // Best effort only: preserve the original deletion failure as the reported error.
        }
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

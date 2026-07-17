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
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? retainedDuplicateCopies = null)
    {
        var result = new DeletionResult();
        var requestedPaths = files
            .Select(file => file.FullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < files.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var file = files[index];

            try
            {
                if (IsDefinitelyMissing(file.FullPath))
                {
                    result.MissingFiles.Add(file.FullPath);
                }
                else
                {
                    DeleteSingleFileWithDuplicateGuard(
                        file.FullPath,
                        mode,
                        requestedPaths,
                        retainedDuplicateCopies,
                        cancellationToken);
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

    private static void DeleteSingleFileWithDuplicateGuard(
        string candidatePath,
        DeleteMode mode,
        IReadOnlySet<string> requestedPaths,
        IReadOnlyDictionary<string, string>? retainedDuplicateCopies,
        CancellationToken cancellationToken)
    {
        if (retainedDuplicateCopies is null ||
            !retainedDuplicateCopies.TryGetValue(candidatePath, out var keeperPath))
        {
            DeleteSingleFile(candidatePath, mode);
            return;
        }

        if (string.Equals(candidatePath, keeperPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("重复组的保留文件不能作为待删除副本");
        }

        if (requestedPaths.Contains(keeperPath))
        {
            throw new IOException("重复组的保留文件也在删除列表中，已跳过");
        }

        using var keeperStream = FileContentComparer.OpenProtectedRead(keeperPath);
        bool contentsMatch;

        using (var candidateStream = FileContentComparer.OpenProtectedRead(candidatePath))
        {
            contentsMatch = FileContentComparer.AreEqual(candidateStream, keeperStream, cancellationToken);
        }

        if (!contentsMatch)
        {
            throw new IOException("文件内容已发生变化，与保留文件不再相同，已跳过删除");
        }

        // Keep the retained file handle open through deletion so another process cannot
        // replace, rename, or delete the last verified copy between comparison and deletion.
        DeleteSingleFile(candidatePath, mode);
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

    public List<string> MissingFiles { get; } = [];

    public long DeletedBytes { get; set; }

    public List<DeletionFailure> Failures { get; } = [];
}

public sealed record DeletionFailure(string Path, string Error);

public sealed record DeletionProgress(int ProcessedCount, int TotalCount, string CurrentPath);

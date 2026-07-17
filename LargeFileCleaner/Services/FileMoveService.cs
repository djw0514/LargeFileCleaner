using System.IO;
using LargeFileCleaner.Models;

namespace LargeFileCleaner.Services;

public sealed class FileMoveService
{
    public FileMoveResult MoveFiles(
        IReadOnlyList<LargeFileItem> files,
        string destinationDirectory,
        IProgress<FileMoveProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(destinationDirectory))
        {
            throw new DirectoryNotFoundException(destinationDirectory);
        }

        var result = new FileMoveResult();
        var normalizedDestination = NormalizePath(destinationDirectory);

        for (var index = 0; index < files.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = files[index];
            var destinationPath = Path.Combine(normalizedDestination, Path.GetFileName(file.FullPath));

            try
            {
                if (IsDefinitelyMissing(file.FullPath))
                {
                    result.MissingFiles.Add(file.FullPath);
                }
                else if (string.Equals(
                             Path.GetFullPath(file.FullPath),
                             Path.GetFullPath(destinationPath),
                             StringComparison.OrdinalIgnoreCase))
                {
                    result.Failures.Add(new FileMoveFailure(file.FullPath, destinationPath, "源文件已在目标目录中"));
                }
                else if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
                {
                    result.Failures.Add(new FileMoveFailure(file.FullPath, destinationPath, "目标位置已存在同名文件或目录，未覆盖"));
                }
                else
                {
                    File.Move(file.FullPath, destinationPath, false);
                    result.MovedFiles.Add(new MovedFile(file.FullPath, destinationPath, file.SizeBytes));
                    result.MovedBytes += file.SizeBytes;
                }
            }
            catch (Exception ex)
            {
                result.Failures.Add(new FileMoveFailure(file.FullPath, destinationPath, ex.Message));
            }

            progress?.Report(new FileMoveProgress(index + 1, files.Count, file.FullPath));
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

    private static string NormalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (!string.IsNullOrEmpty(root) && string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
        {
            return root;
        }

        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}

public sealed record FileMoveProgress(int ProcessedCount, int TotalCount, string CurrentPath);

public sealed record MovedFile(string SourcePath, string DestinationPath, long SizeBytes);

public sealed record FileMoveFailure(string SourcePath, string DestinationPath, string Error);

public sealed class FileMoveResult
{
    public List<MovedFile> MovedFiles { get; } = [];

    public List<string> MissingFiles { get; } = [];

    public List<FileMoveFailure> Failures { get; } = [];

    public long MovedBytes { get; set; }
}

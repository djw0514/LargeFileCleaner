using System.IO;
using LargeFileCleaner.Models;

namespace LargeFileCleaner.Services;

public sealed class LargeFileScanner
{
    public ScanSummary Scan(
        string rootPath,
        long minimumSizeBytes,
        bool excludeSystemDirectories,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var root = new DirectoryInfo(rootPath);
        if (!root.Exists)
        {
            throw new DirectoryNotFoundException(rootPath);
        }

        var protectedDirectories = BuildProtectedDirectories();
        var stack = new Stack<DirectoryInfo>();
        stack.Push(root);

        long visitedFiles = 0;
        long visitedDirectories = 0;
        long matchedFiles = 0;
        long matchedBytes = 0;
        long skippedDirectories = 0;
        long inaccessibleDirectories = 0;

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var currentDirectory = stack.Pop();
            if (ShouldSkipDirectory(currentDirectory, protectedDirectories, excludeSystemDirectories))
            {
                skippedDirectories++;
                continue;
            }

            visitedDirectories++;

            foreach (var childDirectory in EnumerateDirectories(currentDirectory, ref inaccessibleDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (ShouldSkipDirectory(childDirectory, protectedDirectories, excludeSystemDirectories))
                {
                    skippedDirectories++;
                    continue;
                }

                stack.Push(childDirectory);
            }

            foreach (var file in EnumerateFiles(currentDirectory, ref inaccessibleDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                visitedFiles++;

                if (IsReparsePoint(file))
                {
                    continue;
                }

                try
                {
                    file.Refresh();
                    if (file.Length < minimumSizeBytes)
                    {
                        if (visitedFiles % 250 == 0)
                        {
                            progress?.Report(new ScanProgress(
                                visitedFiles,
                                visitedDirectories,
                                matchedFiles,
                                matchedBytes,
                                skippedDirectories,
                                inaccessibleDirectories,
                                currentDirectory.FullName,
                                null));
                        }

                        continue;
                    }

                    matchedFiles++;
                    matchedBytes += file.Length;

                    progress?.Report(new ScanProgress(
                        visitedFiles,
                        visitedDirectories,
                        matchedFiles,
                        matchedBytes,
                        skippedDirectories,
                        inaccessibleDirectories,
                        currentDirectory.FullName,
                        LargeFileItem.FromFileInfo(file)));
                }
                catch
                {
                    inaccessibleDirectories++;
                }
            }
        }

        return new ScanSummary(
            visitedFiles,
            visitedDirectories,
            matchedFiles,
            matchedBytes,
            skippedDirectories,
            inaccessibleDirectories);
    }

    private static DirectoryInfo[] EnumerateDirectories(DirectoryInfo directory, ref long inaccessibleDirectories)
    {
        try
        {
            return directory.EnumerateDirectories().ToArray();
        }
        catch
        {
            inaccessibleDirectories++;
            return [];
        }
    }

    private static FileInfo[] EnumerateFiles(DirectoryInfo directory, ref long inaccessibleDirectories)
    {
        try
        {
            return directory.EnumerateFiles().ToArray();
        }
        catch
        {
            inaccessibleDirectories++;
            return [];
        }
    }

    private static IReadOnlyList<string> BuildProtectedDirectories()
    {
        var candidates = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        };

        return candidates
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool ShouldSkipDirectory(
        DirectoryInfo directory,
        IReadOnlyList<string> protectedDirectories,
        bool excludeSystemDirectories)
    {
        if (IsReparsePoint(directory))
        {
            return true;
        }

        if (directory.Name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase) ||
            directory.Name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!excludeSystemDirectories)
        {
            return false;
        }

        if (directory.Name.Equals("AppData", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var normalizedPath = NormalizePath(directory.FullName);
        return protectedDirectories.Any(protectedDirectory => IsSameOrChild(normalizedPath, protectedDirectory));
    }

    private static bool IsSameOrChild(string path, string parentPath)
    {
        return path.Equals(parentPath, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(parentPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsReparsePoint(FileSystemInfo fileSystemInfo)
    {
        try
        {
            return fileSystemInfo.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch
        {
            return true;
        }
    }
}

public sealed record ScanProgress(
    long VisitedFiles,
    long VisitedDirectories,
    long MatchedFiles,
    long MatchedBytes,
    long SkippedDirectories,
    long InaccessibleDirectories,
    string CurrentPath,
    LargeFileItem? FoundFile);

public sealed record ScanSummary(
    long VisitedFiles,
    long VisitedDirectories,
    long MatchedFiles,
    long MatchedBytes,
    long SkippedDirectories,
    long InaccessibleDirectories);

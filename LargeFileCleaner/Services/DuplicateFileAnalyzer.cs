using System.Buffers;
using System.IO;
using System.Security.Cryptography;

namespace LargeFileCleaner.Services;

public sealed class DuplicateFileAnalyzer
{
    private const int BufferSize = 1024 * 1024;

    public DuplicateAnalysisResult Analyze(
        IReadOnlyList<DuplicateCandidate> candidates,
        IProgress<DuplicateAnalysisProgress>? progress,
        CancellationToken cancellationToken)
    {
        var result = new DuplicateAnalysisResult();
        var failuresByPath = new Dictionary<string, DuplicateAnalysisFailure>(StringComparer.OrdinalIgnoreCase);
        var inspectedFiles = new List<InspectedFile>();

        foreach (var candidate in candidates
                     .DistinctBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var snapshot = ReadSnapshot(candidate.FullPath);
                if (snapshot.Length != candidate.ExpectedSizeBytes ||
                    snapshot.LastWriteTimeUtc != candidate.ExpectedLastWriteTimeUtc)
                {
                    throw new IOException("文件自扫描后已发生变化，请重新扫描后再查重");
                }

                inspectedFiles.Add(new InspectedFile(candidate.FullPath, snapshot));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AddFailure(failuresByPath, candidate.FullPath, ex.Message);
            }
        }

        var hashCandidates = inspectedFiles
            .GroupBy(file => file.Snapshot.Length)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group)
            .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var totalBytes = hashCandidates.Sum(file => file.Snapshot.Length);
        long processedBytes = 0;
        var processedFiles = 0;
        var hashedFiles = new List<HashedFile>();

        foreach (var file in hashCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytesSinceLastProgress = 0L;

            try
            {
                var hash = ComputeHash(
                    file,
                    bytesRead =>
                    {
                        processedBytes += bytesRead;
                        bytesSinceLastProgress += bytesRead;
                        if (bytesSinceLastProgress < 16L * 1024 * 1024)
                        {
                            return;
                        }

                        bytesSinceLastProgress = 0;
                        progress?.Report(new DuplicateAnalysisProgress(
                            processedFiles,
                            hashCandidates.Count,
                            processedBytes,
                            totalBytes,
                            file.Path));
                    },
                    cancellationToken);

                EnsureUnchanged(file);
                hashedFiles.Add(new HashedFile(file.Path, file.Snapshot, hash));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AddFailure(failuresByPath, file.Path, ex.Message);
            }
            finally
            {
                processedFiles++;
                progress?.Report(new DuplicateAnalysisProgress(
                    processedFiles,
                    hashCandidates.Count,
                    processedBytes,
                    totalBytes,
                    file.Path));
            }
        }

        foreach (var hashBucket in hashedFiles
                     .GroupBy(file => (file.Snapshot.Length, file.Hash), HashBucketComparer.Instance)
                     .Where(group => group.Count() > 1)
                     .OrderByDescending(group => group.Key.Length)
                     .ThenBy(group => group.Key.Hash, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var exactGroups = BuildExactGroups(hashBucket.ToList(), failuresByPath, cancellationToken);
            var subgroupIndex = 0;

            foreach (var exactGroup in exactGroups)
            {
                var stablePaths = exactGroup
                    .Where(file => IsStillUnchanged(file, failuresByPath))
                    .Select(file => file.Path)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                if (stablePaths.Length < 2)
                {
                    continue;
                }

                subgroupIndex++;
                var groupId = $"{hashBucket.Key.Length}:{hashBucket.Key.Hash}:{subgroupIndex}";
                result.Groups.Add(new DuplicateFileGroup(
                    groupId,
                    hashBucket.Key.Length,
                    hashBucket.Key.Hash,
                    stablePaths));
            }
        }

        result.Failures.AddRange(failuresByPath.Values.OrderBy(failure => failure.Path, StringComparer.OrdinalIgnoreCase));
        result.HashedFiles = hashedFiles.Count;
        result.HashedBytes = processedBytes;
        return result;
    }

    private static List<List<HashedFile>> BuildExactGroups(
        IReadOnlyList<HashedFile> files,
        IDictionary<string, DuplicateAnalysisFailure> failuresByPath,
        CancellationToken cancellationToken)
    {
        var groups = new List<List<HashedFile>>();

        foreach (var file in files.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var placed = false;

            for (var groupIndex = 0; groupIndex < groups.Count; groupIndex++)
            {
                var group = groups[groupIndex];
                try
                {
                    if (!FileContentComparer.AreEqual(group[0].Path, file.Path, cancellationToken))
                    {
                        continue;
                    }

                    group.Add(file);
                    placed = true;
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    var representative = group[0];
                    if (!CanStillBeRead(representative))
                    {
                        AddFailure(failuresByPath, representative.Path, ex.Message);
                        group.RemoveAt(0);
                        if (group.Count == 0)
                        {
                            groups.RemoveAt(groupIndex);
                        }

                        groupIndex--;
                        continue;
                    }

                    AddFailure(failuresByPath, file.Path, ex.Message);
                    placed = true;
                    break;
                }
            }

            if (!placed)
            {
                groups.Add([file]);
            }
        }

        return groups;
    }

    private static bool CanStillBeRead(HashedFile file)
    {
        try
        {
            using var stream = new FileStream(
                file.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete);
            return stream.Length == file.Snapshot.Length && ReadSnapshot(file.Path) == file.Snapshot;
        }
        catch
        {
            return false;
        }
    }

    private static string ComputeHash(
        InspectedFile file,
        Action<int> reportBytesRead,
        CancellationToken cancellationToken)
    {
        using var stream = new FileStream(
            file.Path,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read | FileShare.Delete,
                BufferSize = BufferSize,
                Options = FileOptions.SequentialScan
            });

        if (stream.Length != file.Snapshot.Length)
        {
            throw new IOException("文件大小在分析期间发生变化");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bytesRead = stream.Read(buffer, 0, buffer.Length);
                if (bytesRead == 0)
                {
                    break;
                }

                hash.AppendData(buffer, 0, bytesRead);
                reportBytesRead(bytesRead);
            }

            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static FileSnapshot ReadSnapshot(string path)
    {
        var fileInfo = new FileInfo(path);
        fileInfo.Refresh();

        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException("文件不存在", path);
        }

        return new FileSnapshot(fileInfo.Length, fileInfo.LastWriteTimeUtc);
    }

    private static void EnsureUnchanged(InspectedFile file)
    {
        var current = ReadSnapshot(file.Path);
        if (current != file.Snapshot)
        {
            throw new IOException("文件在分析期间发生变化");
        }
    }

    private static bool IsStillUnchanged(
        HashedFile file,
        IDictionary<string, DuplicateAnalysisFailure> failuresByPath)
    {
        try
        {
            var current = ReadSnapshot(file.Path);
            if (current == file.Snapshot)
            {
                return true;
            }

            AddFailure(failuresByPath, file.Path, "文件在分析期间发生变化");
        }
        catch (Exception ex)
        {
            AddFailure(failuresByPath, file.Path, ex.Message);
        }

        return false;
    }

    private static void AddFailure(
        IDictionary<string, DuplicateAnalysisFailure> failuresByPath,
        string path,
        string error)
    {
        failuresByPath[path] = new DuplicateAnalysisFailure(path, error);
    }

    private sealed record FileSnapshot(long Length, DateTime LastWriteTimeUtc);

    private sealed record InspectedFile(string Path, FileSnapshot Snapshot);

    private sealed record HashedFile(string Path, FileSnapshot Snapshot, string Hash);

    private sealed class HashBucketComparer : IEqualityComparer<(long Length, string Hash)>
    {
        public static HashBucketComparer Instance { get; } = new();

        public bool Equals((long Length, string Hash) x, (long Length, string Hash) y)
        {
            return x.Length == y.Length && string.Equals(x.Hash, y.Hash, StringComparison.Ordinal);
        }

        public int GetHashCode((long Length, string Hash) obj)
        {
            return HashCode.Combine(obj.Length, StringComparer.Ordinal.GetHashCode(obj.Hash));
        }
    }
}

public sealed record DuplicateCandidate(string FullPath, long ExpectedSizeBytes, DateTime ExpectedLastWriteTimeUtc);

public sealed record DuplicateAnalysisProgress(
    int ProcessedFiles,
    int TotalFiles,
    long ProcessedBytes,
    long TotalBytes,
    string CurrentPath);

public sealed record DuplicateFileGroup(
    string Id,
    long SizeBytes,
    string ContentHash,
    IReadOnlyList<string> FilePaths);

public sealed record DuplicateAnalysisFailure(string Path, string Error);

public sealed class DuplicateAnalysisResult
{
    public List<DuplicateFileGroup> Groups { get; } = [];

    public List<DuplicateAnalysisFailure> Failures { get; } = [];

    public int HashedFiles { get; set; }

    public long HashedBytes { get; set; }
}

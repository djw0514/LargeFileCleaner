using System.Buffers;
using System.IO;

namespace LargeFileCleaner.Services;

public static class FileContentComparer
{
    private const int BufferSize = 1024 * 1024;

    public static bool AreEqual(string firstPath, string secondPath, CancellationToken cancellationToken)
    {
        using var firstStream = OpenForSequentialRead(firstPath);
        using var secondStream = OpenForSequentialRead(secondPath);

        return AreEqual(firstStream, secondStream, cancellationToken);
    }

    internal static bool AreEqual(Stream firstStream, Stream secondStream, CancellationToken cancellationToken)
    {
        if (firstStream.Length != secondStream.Length)
        {
            return false;
        }

        var firstBuffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        var secondBuffer = ArrayPool<byte>.Shared.Rent(BufferSize);

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var firstRead = ReadChunk(firstStream, firstBuffer, cancellationToken);
                var secondRead = ReadChunk(secondStream, secondBuffer, cancellationToken);

                if (firstRead != secondRead)
                {
                    return false;
                }

                if (firstRead == 0)
                {
                    return true;
                }

                if (!firstBuffer.AsSpan(0, firstRead).SequenceEqual(secondBuffer.AsSpan(0, secondRead)))
                {
                    return false;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(firstBuffer);
            ArrayPool<byte>.Shared.Return(secondBuffer);
        }
    }

    private static int ReadChunk(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var totalRead = 0;

        while (totalRead < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytesRead = stream.Read(buffer, totalRead, buffer.Length - totalRead);
            if (bytesRead == 0)
            {
                break;
            }

            totalRead += bytesRead;
        }

        return totalRead;
    }

    internal static FileStream OpenProtectedRead(string path)
    {
        return OpenForSequentialRead(path, FileShare.Read);
    }

    private static FileStream OpenForSequentialRead(
        string path,
        FileShare share = FileShare.Read | FileShare.Delete)
    {
        return new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = share,
                BufferSize = BufferSize,
                Options = FileOptions.SequentialScan
            });
    }
}

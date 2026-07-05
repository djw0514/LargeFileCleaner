using System.IO;
using System.Text.Json;

namespace LargeFileCleaner.Services;

public sealed class DeleteLogService
{
    private readonly string _logPath;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };

    public DeleteLogService()
    {
        var logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LargeFileCleaner");

        Directory.CreateDirectory(logDirectory);
        _logPath = Path.Combine(logDirectory, "delete-history.json");
    }

    public string LogPath => _logPath;

    public async Task AppendAsync(DeleteLogEntry entry, CancellationToken cancellationToken = default)
    {
        var entries = await ReadExistingEntriesAsync(cancellationToken);
        entries.Add(entry);

        var json = JsonSerializer.Serialize(entries, _jsonOptions);
        await File.WriteAllTextAsync(_logPath, json, cancellationToken);
    }

    private async Task<List<DeleteLogEntry>> ReadExistingEntriesAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_logPath))
        {
            return [];
        }

        try
        {
            var json = await File.ReadAllTextAsync(_logPath, cancellationToken);
            return JsonSerializer.Deserialize<List<DeleteLogEntry>>(json, _jsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }
}

public sealed record DeleteLogEntry(
    DateTimeOffset DeletedAt,
    string DeleteMode,
    int RequestedCount,
    int DeletedCount,
    long DeletedBytes,
    IReadOnlyList<string> DeletedFiles,
    IReadOnlyList<DeletionFailure> Failures);

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace STS2_MCP.Recording;

/// <summary>Disk-only journal. It never holds game objects or invokes game APIs.</summary>
public sealed class RecordingJournal
{
    private sealed record Item(string? Event, string? Path, byte[]? Bytes);
    private readonly object _gate = new();
    private readonly Channel<Item> _queue;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Task _writer;
    private long _accepted;
    private long _written;
    private bool _accepting = true;
    private string? _failure;
    private string _reason = "not_stopped";
    private JsonElement _finishMetadata;
    public string DirectoryPath { get; }
    public string? Failure => Volatile.Read(ref _failure);
    public long AcceptedCount => Interlocked.Read(ref _accepted);
    public long WrittenCount => Interlocked.Read(ref _written);
    public bool Finished => _writer.IsCompleted;
    public double ElapsedSeconds => _clock.Elapsed.TotalSeconds;
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        IncludeFields = true,
    };

    public RecordingJournal(string directory, object header, int capacity = 2048)
    {
        DirectoryPath = Path.GetFullPath(directory);
        // The caller must create a fresh session name. Never append to prior evidence.
        if (Directory.Exists(DirectoryPath))
            throw new IOException("Recording directory already exists.");
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(Path.Combine(DirectoryPath, "header.json"), JsonSerializer.Serialize(header, Json));
        _queue = Channel.CreateBounded<Item>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _finishMetadata = JsonSerializer.SerializeToElement(new { }, Json);
        _writer = Task.Run(WriteLoop);
    }

    public bool Append(string kind, object? data)
    {
        lock (_gate)
        {
            if (!_accepting || Failure != null) return false;
            try
            {
                // Freeze before returning: mutable game state never reaches the IO worker.
                string record = JsonSerializer.Serialize(new
                {
                    schema_version = 1,
                    sequence = _accepted + 1,
                    utc = DateTimeOffset.UtcNow,
                    monotonic_ms = _clock.Elapsed.TotalMilliseconds,
                    kind,
                    data,
                }, Json);
                if (!_queue.Writer.TryWrite(new Item(record, null, null)))
                {
                    MarkFailed("Journal queue filled; recording is incomplete.");
                    return false;
                }
                Interlocked.Increment(ref _accepted);
                return true;
            }
            catch (Exception ex)
            {
                MarkFailed("Cannot serialize record: " + ex.Message);
                return false;
            }
        }
    }

    public string? SaveArtifact(string basename, byte[] bytes)
    {
        lock (_gate)
        {
            if (!_accepting || Failure != null) return null;
            string relative = "game-files/" + Hash(bytes) + "-" + Path.GetFileName(basename);
            if (!_queue.Writer.TryWrite(new Item(null, relative, (byte[])bytes.Clone())))
            {
                MarkFailed("Artifact queue filled; recording is incomplete.");
                return null;
            }
            return relative;
        }
    }

    public void MarkFailed(string message) => Interlocked.CompareExchange(ref _failure, message, null);

    public Task FinishAsync(string reason, object metadata)
    {
        lock (_gate)
        {
            if (_accepting)
            {
                _reason = reason;
                _finishMetadata = JsonSerializer.SerializeToElement(metadata, Json);
                _accepting = false;
                _queue.Writer.TryComplete();
            }
        }
        return _writer;
    }

    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private async Task WriteLoop()
    {
        string previousHash = new('0', 64);
        try
        {
            using (var stream = new FileStream(Path.Combine(DirectoryPath, "events.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true })
            {
                await foreach (var item in _queue.Reader.ReadAllAsync())
                {
                    if (item.Event != null)
                    {
                        // Chain exact line bytes; validator also checks sequence/count.
                        string line = "{\"previous_sha256\":\"" + previousHash + "\",\"record\":" + item.Event + "}";
                        await writer.WriteLineAsync(line);
                        previousHash = Hash(Encoding.UTF8.GetBytes(line));
                        Interlocked.Increment(ref _written);
                    }
                    else
                    {
                        string path = Path.Combine(DirectoryPath, item.Path!);
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        if (!File.Exists(path)) await File.WriteAllBytesAsync(path, item.Bytes!);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            MarkFailed("Recording IO failed: " + ex.Message);
        }
        try
        {
            var files = new List<object>();
            foreach (string path in Directory.EnumerateFiles(DirectoryPath, "*", SearchOption.AllDirectories))
            {
                byte[] bytes = await File.ReadAllBytesAsync(path);
                files.Add(new { path = Path.GetRelativePath(DirectoryPath, path), bytes = bytes.Length, sha256 = Hash(bytes) });
            }
            var manifest = new
            {
                schema_version = 1,
                closed_utc = DateTimeOffset.UtcNow,
                reason = _reason,
                accepted_records = AcceptedCount,
                written_records = WrittenCount,
                last_record_sha256 = previousHash,
                journal_complete = Failure == null && AcceptedCount == WrittenCount && _reason != "not_stopped",
                error = Failure,
                metadata = _finishMetadata,
                files,
            };
            string temp = Path.Combine(DirectoryPath, "manifest.json.tmp");
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(manifest, Json));
            File.Move(temp, Path.Combine(DirectoryPath, "manifest.json"));
        }
        catch (Exception ex)
        {
            MarkFailed("Could not finalize recording manifest: " + ex.Message);
        }
    }
}

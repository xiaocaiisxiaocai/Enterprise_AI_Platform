using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EnterpriseAI.Poc;

public sealed record LocalStateEvent(
    string EventType,
    string TargetId,
    string? SourceId = null,
    string[]? Groups = null,
    bool? Enabled = null,
    DateTimeOffset? ExpiresAtUtc = null,
    string? RelativePath = null,
    string? ContentSha256 = null,
    string? ReasonCode = null);

public sealed record LocalStateEnvelope(
    long Sequence,
    string PreviousHash,
    string EntryHash,
    DateTimeOffset OccurredAtUtc,
    LocalStateEvent Event);

public sealed class LocalStateStore
{
    public const string GenesisHash = "GENESIS";

    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly object _sync = new();
    private readonly string _directory;
    private readonly List<LocalStateEnvelope> _events;
    private long _sequence;
    private string _previousHash;

    public LocalStateStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
        if ((File.GetAttributes(_directory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("本地状态目录不得为符号链接或重解析点。");
        }

        _events = LoadAndValidate(_directory);
        _sequence = _events.Count;
        _previousHash = _events.Count == 0 ? GenesisHash : _events[^1].EntryHash;
    }

    public string FinalHash
    {
        get
        {
            lock (_sync)
            {
                return _previousHash;
            }
        }
    }

    public IReadOnlyList<LocalStateEnvelope> ReadEvents()
    {
        lock (_sync)
        {
            return _events.ToArray();
        }
    }

    public LocalStateEnvelope Append(LocalStateEvent stateEvent)
    {
        ValidateEvent(stateEvent);
        lock (_sync)
        {
            var sequence = checked(_sequence + 1);
            var occurredAtUtc = DateTimeOffset.UtcNow;
            var entryHash = ComputeHash(sequence, _previousHash, occurredAtUtc, stateEvent);
            var envelope = new LocalStateEnvelope(
                sequence,
                _previousHash,
                entryHash,
                occurredAtUtc,
                stateEvent);
            var finalName = $"{sequence:D20}-{entryHash}.json";
            var finalPath = Path.Combine(_directory, finalName);
            var pendingPath = Path.Combine(_directory, $".pending-{Guid.NewGuid():N}.tmp");
            WriteDurably(pendingPath, JsonSerializer.Serialize(envelope, JsonOptions));
            File.Move(pendingPath, finalPath);

            _events.Add(envelope);
            _sequence = sequence;
            _previousHash = entryHash;
            return envelope;
        }
    }

    private static List<LocalStateEnvelope> LoadAndValidate(string directory)
    {
        var events = new List<LocalStateEnvelope>();
        var expectedSequence = 1L;
        var previousHash = GenesisHash;
        foreach (var path in Directory.EnumerateFiles(directory, "*.json")
            .Order(StringComparer.Ordinal))
        {
            LocalStateEnvelope envelope;
            try
            {
                envelope = JsonSerializer.Deserialize<LocalStateEnvelope>(
                    File.ReadAllBytes(path),
                    JsonOptions)
                    ?? throw new InvalidDataException("本地状态事件不能为空。");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("本地状态事件 JSON 损坏。", exception);
            }

            ValidateEvent(envelope.Event);
            var expectedHash = ComputeHash(
                envelope.Sequence,
                envelope.PreviousHash,
                envelope.OccurredAtUtc,
                envelope.Event);
            var expectedName = $"{envelope.Sequence:D20}-{envelope.EntryHash}.json";
            if (envelope.Sequence != expectedSequence ||
                !string.Equals(envelope.PreviousHash, previousHash, StringComparison.Ordinal) ||
                !string.Equals(envelope.EntryHash, expectedHash, StringComparison.Ordinal) ||
                !string.Equals(Path.GetFileName(path), expectedName, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"本地状态事件链在序号 {expectedSequence} 处不一致。");
            }

            events.Add(envelope);
            expectedSequence++;
            previousHash = envelope.EntryHash;
        }
        return events;
    }

    private static void ValidateEvent(LocalStateEvent stateEvent)
    {
        ArgumentNullException.ThrowIfNull(stateEvent);
        if (string.IsNullOrWhiteSpace(stateEvent.EventType) ||
            string.IsNullOrWhiteSpace(stateEvent.TargetId))
        {
            throw new InvalidDataException("本地状态事件缺少类型或目标。");
        }
    }

    private static string ComputeHash(
        long sequence,
        string previousHash,
        DateTimeOffset occurredAtUtc,
        LocalStateEvent stateEvent)
    {
        var eventJson = JsonSerializer.Serialize(stateEvent, JsonOptions);
        var canonical = $"{sequence}\n{previousHash}\n{occurredAtUtc:O}\n{eventJson}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    private static void WriteDurably(string path, string content)
    {
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough);
        using var writer = new StreamWriter(stream, Utf8WithoutBom);
        writer.Write(content);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }
}

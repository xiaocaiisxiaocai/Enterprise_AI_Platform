using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EnterpriseAI.Poc;

public interface ISearchTraceSink
{
    void Record(SearchTraceRecord record);
}

public sealed record SearchTraceRecord(
    string SchemaVersion,
    string TraceId,
    DateTimeOffset OccurredAtUtc,
    string PrincipalId,
    string TenantId,
    string[] Groups,
    string QuestionSha256,
    string SnapshotSourceId,
    string SnapshotManifestSha256,
    string PolicyVersion,
    string Decision,
    string ReasonCode,
    string? SelectedDocumentId,
    int CitationCount);

public sealed record SearchTraceEnvelope(
    long Sequence,
    string PreviousHash,
    string EntryHash,
    SearchTraceRecord Record);

public sealed record TraceChainValidation(
    long EntryCount,
    string FinalHash);

public sealed class InMemorySearchTraceSink : ISearchTraceSink
{
    private readonly object _sync = new();
    private readonly List<SearchTraceRecord> _records = [];

    public IReadOnlyList<SearchTraceRecord> Records
    {
        get
        {
            lock (_sync)
            {
                return _records.ToArray();
            }
        }
    }

    public void Record(SearchTraceRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_sync)
        {
            _records.Add(record);
        }
    }
}

public sealed class HashChainedJsonLineTraceSink : ISearchTraceSink
{
    public const string GenesisHash = "GENESIS";

    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly object _sync = new();
    private readonly string _path;
    private long _sequence;
    private string _previousHash;

    public HashChainedJsonLineTraceSink(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        var parentPath = Path.GetDirectoryName(_path)
            ?? throw new InvalidDataException("Trace 文件缺少父目录。");
        Directory.CreateDirectory(parentPath);

        var validation = Validate(_path);
        _sequence = validation.EntryCount;
        _previousHash = validation.FinalHash;
    }

    public void Record(SearchTraceRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_sync)
        {
            var sequence = checked(_sequence + 1);
            var entryHash = ComputeEntryHash(sequence, _previousHash, record);
            var envelope = new SearchTraceEnvelope(
                sequence,
                _previousHash,
                entryHash,
                record);
            var line = JsonSerializer.Serialize(envelope, SerializerOptions);

            using var stream = new FileStream(
                _path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.WriteThrough);
            using var writer = new StreamWriter(stream, Utf8WithoutBom);
            writer.WriteLine(line);
            writer.Flush();
            stream.Flush(flushToDisk: true);

            _sequence = sequence;
            _previousHash = entryHash;
        }
    }

    public static TraceChainValidation Validate(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return new TraceChainValidation(0, GenesisHash);
        }

        long expectedSequence = 1;
        var previousHash = GenesisHash;
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                throw new InvalidDataException("Trace 哈希链包含空记录。");
            }

            var envelope = JsonSerializer.Deserialize<SearchTraceEnvelope>(line, SerializerOptions)
                ?? throw new InvalidDataException("Trace 记录无法反序列化。");
            if (envelope.Sequence != expectedSequence ||
                !string.Equals(envelope.PreviousHash, previousHash, StringComparison.Ordinal) ||
                !string.Equals(
                    envelope.EntryHash,
                    ComputeEntryHash(envelope.Sequence, envelope.PreviousHash, envelope.Record),
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Trace 哈希链在序号 {expectedSequence} 处不一致。");
            }

            previousHash = envelope.EntryHash;
            expectedSequence++;
        }

        return new TraceChainValidation(expectedSequence - 1, previousHash);
    }

    private static string ComputeEntryHash(
        long sequence,
        string previousHash,
        SearchTraceRecord record)
    {
        var recordJson = JsonSerializer.Serialize(record, SerializerOptions);
        var canonicalEntry = $"{sequence}\n{previousHash}\n{recordJson}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalEntry)))
            .ToLowerInvariant();
    }
}

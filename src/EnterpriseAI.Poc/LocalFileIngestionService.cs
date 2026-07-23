using System.Security.Cryptography;
using System.Text;

namespace EnterpriseAI.Poc;

public sealed class LocalFileIngestionService
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly HashSet<string> SupportedExtensions = new(
        [".md", ".txt"],
        StringComparer.OrdinalIgnoreCase);

    private readonly object _sync = new();
    private readonly DocumentRepository _repository;
    private readonly LocalFileIngestionOptions _options;
    private readonly LocalStateStore? _stateStore;
    private Dictionary<string, IngestedFileState> _published = new(StringComparer.OrdinalIgnoreCase);

    public LocalFileIngestionService(
        DocumentRepository repository,
        LocalFileIngestionOptions options,
        LocalStateStore? stateStore = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _options = ValidateOptions(options);
        _stateStore = stateStore;
        if (stateStore is not null)
        {
            ReplayCheckpoint(stateStore.ReadEvents());
        }
    }

    public LocalFileIngestionResult Synchronize()
    {
        lock (_sync)
        {
            var candidates = new Dictionary<string, IngestedFileState>(
                StringComparer.OrdinalIgnoreCase);
            var quarantine = new List<IngestionQuarantineItem>();
            var ignored = 0;
            foreach (var sourcePath in EnumerateSafeFiles(_options.RootPath, quarantine))
            {
                var extension = Path.GetExtension(sourcePath);
                if (!SupportedExtensions.Contains(extension))
                {
                    ignored++;
                    continue;
                }

                var relativePath = NormalizeRelativePath(_options.RootPath, sourcePath);
                try
                {
                    var bytes = ReadStableFile(sourcePath);
                    var content = StrictUtf8.GetString(bytes).TrimEnd('\r', '\n');
                    if (string.IsNullOrWhiteSpace(content))
                    {
                        quarantine.Add(new IngestionQuarantineItem(relativePath, "empty_content"));
                        continue;
                    }

                    var contentHash = ComputeSha256(bytes);
                    var documentId = CreateDocumentId(_options.SourceId, relativePath);
                    var title = ExtractTitle(relativePath, extension, content);
                    var record = new DocumentRecord(
                        documentId,
                        PocIdentityDirectory.EnterpriseTenantId,
                        $"sha256:{contentHash}",
                        title,
                        "全文",
                        $"local/{_options.SourceId}/{relativePath}",
                        content,
                        _options.AllowedGroups,
                        BuildSearchTerms(relativePath, title));
                    candidates[relativePath] = new IngestedFileState(
                        documentId,
                        contentHash,
                        record);
                }
                catch (FileTooLargeException)
                {
                    quarantine.Add(new IngestionQuarantineItem(relativePath, "file_too_large"));
                }
                catch (DecoderFallbackException)
                {
                    quarantine.Add(new IngestionQuarantineItem(relativePath, "invalid_utf8"));
                }
                catch (IOException)
                {
                    quarantine.Add(new IngestionQuarantineItem(relativePath, "unstable_or_unreadable"));
                }
                catch (UnauthorizedAccessException)
                {
                    quarantine.Add(new IngestionQuarantineItem(relativePath, "unreadable"));
                }
            }

            var added = candidates.Keys.Count(path => !_published.ContainsKey(path));
            var updated = candidates.Count(pair =>
                _published.TryGetValue(pair.Key, out var previous) &&
                !string.Equals(previous.ContentSha256, pair.Value.ContentSha256, StringComparison.Ordinal));
            var unchanged = candidates.Count(pair =>
                _published.TryGetValue(pair.Key, out var previous) &&
                string.Equals(previous.ContentSha256, pair.Value.ContentSha256, StringComparison.Ordinal));
            var removedIds = _published
                .Where(pair => !candidates.ContainsKey(pair.Key))
                .Select(pair => pair.Value.DocumentId)
                .ToArray();

            _repository.ApplyIngestionBatch(
                candidates.Values.Select(candidate => candidate.Document!).ToArray(),
                removedIds);
            PersistCheckpointChanges(candidates, quarantine);
            _published = candidates;

            return new LocalFileIngestionResult(
                added,
                updated,
                unchanged,
                removedIds.Length,
                ignored,
                quarantine,
                _repository.Revision,
                _stateStore?.FinalHash ?? LocalStateStore.GenesisHash);
        }
    }

    private static LocalFileIngestionOptions ValidateOptions(LocalFileIngestionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var rootPath = Path.GetFullPath(options.RootPath);
        if (!Directory.Exists(rootPath))
        {
            throw new DirectoryNotFoundException("本地摄取根目录不存在。");
        }
        if ((File.GetAttributes(rootPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("本地摄取根目录不得为符号链接或重解析点。");
        }

        var groups = options.AllowedGroups?
            .Select(group => group?.Trim())
            .Where(group => !string.IsNullOrWhiteSpace(group))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
        if (string.IsNullOrWhiteSpace(options.SourceId) ||
            string.IsNullOrWhiteSpace(options.Owner) ||
            string.IsNullOrWhiteSpace(options.Classification) ||
            groups.Length == 0 ||
            options.SourceId.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            throw new InvalidDataException("本地摄取配置缺少有效 Source、Owner、分类或 ACL。");
        }

        return options with
        {
            RootPath = rootPath,
            SourceId = options.SourceId.Trim(),
            Owner = options.Owner.Trim(),
            Classification = options.Classification.Trim(),
            AllowedGroups = groups
        };
    }

    private static IEnumerable<string> EnumerateSafeFiles(
        string rootPath,
        List<IngestionQuarantineItem> quarantine)
    {
        var pending = new Stack<string>();
        pending.Push(rootPath);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory)
                .Order(StringComparer.OrdinalIgnoreCase))
            {
                var attributes = File.GetAttributes(entry);
                var relativePath = NormalizeRelativePath(rootPath, entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    quarantine.Add(new IngestionQuarantineItem(relativePath, "reparse_point"));
                    continue;
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
                else
                {
                    yield return entry;
                }
            }
        }
    }

    private static byte[] ReadStableFile(string sourcePath)
    {
        using var stream = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        var length = stream.Length;
        if (length > DocumentRepository.MaxSourceFileBytes)
        {
            throw new FileTooLargeException();
        }

        var bytes = new byte[checked((int)length)];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = stream.Read(bytes, offset, bytes.Length - offset);
            if (read == 0)
            {
                throw new IOException("本地来源文件在读取期间发生变化。");
            }
            offset += read;
        }
        if (stream.ReadByte() != -1 || stream.Length != length)
        {
            throw new IOException("本地来源文件在读取期间发生变化。");
        }
        return bytes;
    }

    private static string NormalizeRelativePath(string rootPath, string path) =>
        Path.GetRelativePath(rootPath, path)
            .Replace('\\', '/')
            .ToLowerInvariant();

    private static string CreateDocumentId(string sourceId, string relativePath)
    {
        var hash = ComputeSha256(Encoding.UTF8.GetBytes($"{sourceId}\n{relativePath}"));
        return $"local-{hash[..24]}";
    }

    private static string ExtractTitle(string relativePath, string extension, string content)
    {
        if (extension.Equals(".md", StringComparison.OrdinalIgnoreCase))
        {
            var heading = content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .FirstOrDefault(line => line.StartsWith("# ", StringComparison.Ordinal));
            if (heading is not null && !string.IsNullOrWhiteSpace(heading[2..]))
            {
                return heading[2..].Trim();
            }
        }
        return Path.GetFileNameWithoutExtension(relativePath);
    }

    private static string[] BuildSearchTerms(string relativePath, string title) =>
        new[] { title, Path.GetFileNameWithoutExtension(relativePath) }
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string ComputeSha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private void ReplayCheckpoint(IEnumerable<LocalStateEnvelope> events)
    {
        foreach (var envelope in events)
        {
            var stateEvent = envelope.Event;
            if (!string.Equals(stateEvent.SourceId, _options.SourceId, StringComparison.Ordinal))
            {
                continue;
            }
            if (stateEvent.EventType == "ingestion_file_upserted")
            {
                var relativePath = stateEvent.RelativePath
                    ?? throw new InvalidDataException("摄取 Checkpoint 缺少相对路径。");
                var contentHash = stateEvent.ContentSha256
                    ?? throw new InvalidDataException("摄取 Checkpoint 缺少内容哈希。");
                _published[relativePath] = new IngestedFileState(
                    stateEvent.TargetId,
                    contentHash,
                    Document: null);
            }
            else if (stateEvent.EventType is "ingestion_file_removed" or "ingestion_file_quarantined")
            {
                if (stateEvent.RelativePath is null)
                {
                    throw new InvalidDataException("摄取删除/隔离事件缺少相对路径。");
                }
                _published.Remove(stateEvent.RelativePath);
            }
        }
    }

    private void PersistCheckpointChanges(
        IReadOnlyDictionary<string, IngestedFileState> candidates,
        IReadOnlyCollection<IngestionQuarantineItem> quarantine)
    {
        if (_stateStore is null)
        {
            return;
        }

        foreach (var pair in candidates.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (_published.TryGetValue(pair.Key, out var previous) &&
                string.Equals(
                    previous.ContentSha256,
                    pair.Value.ContentSha256,
                    StringComparison.Ordinal))
            {
                continue;
            }
            _stateStore.Append(new LocalStateEvent(
                "ingestion_file_upserted",
                pair.Value.DocumentId,
                _options.SourceId,
                RelativePath: pair.Key,
                ContentSha256: pair.Value.ContentSha256));
        }

        var quarantineByPath = quarantine.ToDictionary(
            item => item.RelativePath,
            StringComparer.OrdinalIgnoreCase);
        foreach (var previous in _published
            .Where(pair => !candidates.ContainsKey(pair.Key))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (quarantineByPath.TryGetValue(previous.Key, out var item))
            {
                _stateStore.Append(new LocalStateEvent(
                    "ingestion_file_quarantined",
                    previous.Value.DocumentId,
                    _options.SourceId,
                    RelativePath: previous.Key,
                    ReasonCode: item.ReasonCode));
            }
            else
            {
                _stateStore.Append(new LocalStateEvent(
                    "ingestion_file_removed",
                    previous.Value.DocumentId,
                    _options.SourceId,
                    RelativePath: previous.Key));
            }
        }
    }

    private sealed record IngestedFileState(
        string DocumentId,
        string ContentSha256,
        DocumentRecord? Document);

    private sealed class FileTooLargeException : Exception
    {
    }
}

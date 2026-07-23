using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EnterpriseAI.Poc;

public sealed class DocumentRepository
{
    public const int MaxSourceFileBytes = 256 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly object _sync = new();
    private readonly Dictionary<string, ManagedDocument> _documents;
    private readonly LocalStateStore? _stateStore;
    private long _revision;

    private DocumentRepository(
        IReadOnlyList<DocumentRecord> documents,
        string sourceId,
        string manifestSha256,
        LocalStateStore? stateStore)
    {
        _documents = documents.ToDictionary(
            document => document.Id,
            document => new ManagedDocument(
                document,
                KnowledgeLifecycleStatus.Published,
                ExpiresAtUtc: null),
            StringComparer.OrdinalIgnoreCase);
        SourceId = sourceId;
        ManifestSha256 = manifestSha256;
        _stateStore = stateStore;
        if (stateStore is not null)
        {
            Replay(stateStore.ReadEvents());
        }
    }

    public IReadOnlyList<DocumentRecord> Documents
    {
        get
        {
            lock (_sync)
            {
                return _documents.Values
                    .Where(document => document.Status != KnowledgeLifecycleStatus.Deleted)
                    .Select(document => document.Document)
                    .OrderBy(document => document.Id, StringComparer.Ordinal)
                    .ToArray();
            }
        }
    }

    public string SourceId { get; }

    public string ManifestSha256 { get; }

    public long Revision
    {
        get
        {
            lock (_sync)
            {
                return _revision;
            }
        }
    }

    public AuthorizedDocumentSnapshot GetAuthorizedSnapshot(
        PocIdentity identity,
        DateTimeOffset? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var effectiveNow = nowUtc ?? DateTimeOffset.UtcNow;
        lock (_sync)
        {
            var documents = _documents.Values
                .Where(entry =>
                    entry.Status == KnowledgeLifecycleStatus.Published &&
                    (entry.ExpiresAtUtc is null || entry.ExpiresAtUtc > effectiveNow) &&
                    string.Equals(
                        identity.TenantId,
                        entry.Document.TenantId,
                        StringComparison.Ordinal) &&
                    entry.Document.AllowedGroups.Any(identity.Groups.Contains))
                .Select(entry => entry.Document)
                .ToArray();
            return new AuthorizedDocumentSnapshot(
                _revision,
                GetRevisionSourceId(_revision),
                documents);
        }
    }

    public void ReplaceAllowedGroups(string documentId, IEnumerable<string> allowedGroups)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        ArgumentNullException.ThrowIfNull(allowedGroups);
        var normalizedGroups = allowedGroups
            .Select(group => group?.Trim())
            .Where(group => !string.IsNullOrWhiteSpace(group))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedGroups.Length == 0)
        {
            throw new ArgumentException("文档 ACL 至少包含一个有效 Group。", nameof(allowedGroups));
        }

        lock (_sync)
        {
            var entry = GetMutableDocument(documentId);
            EnsureNotDeleted(entry);
            _stateStore?.Append(new LocalStateEvent(
                "document_acl_replaced",
                entry.Document.Id,
                Groups: normalizedGroups));
            _documents[documentId] = entry with
            {
                Document = entry.Document with { AllowedGroups = normalizedGroups }
            };
            AdvanceRevision();
        }
    }

    public void Withdraw(string documentId)
    {
        lock (_sync)
        {
            var entry = GetMutableDocument(documentId);
            EnsureNotDeleted(entry);
            _stateStore?.Append(new LocalStateEvent("document_withdrawn", entry.Document.Id));
            _documents[documentId] = entry with
            {
                Status = KnowledgeLifecycleStatus.Withdrawn
            };
            AdvanceRevision();
        }
    }

    public void Publish(string documentId)
    {
        lock (_sync)
        {
            var entry = GetMutableDocument(documentId);
            EnsureNotDeleted(entry);
            _stateStore?.Append(new LocalStateEvent("document_published", entry.Document.Id));
            _documents[documentId] = entry with
            {
                Status = KnowledgeLifecycleStatus.Published,
                ExpiresAtUtc = null
            };
            AdvanceRevision();
        }
    }

    public void SetExpiration(string documentId, DateTimeOffset? expiresAtUtc)
    {
        lock (_sync)
        {
            var entry = GetMutableDocument(documentId);
            EnsureNotDeleted(entry);
            _stateStore?.Append(new LocalStateEvent(
                expiresAtUtc is null ? "document_expiration_cleared" : "document_expiration_set",
                entry.Document.Id,
                ExpiresAtUtc: expiresAtUtc));
            _documents[documentId] = entry with { ExpiresAtUtc = expiresAtUtc };
            AdvanceRevision();
        }
    }

    public void Delete(string documentId)
    {
        lock (_sync)
        {
            var entry = GetMutableDocument(documentId);
            EnsureNotDeleted(entry);
            _stateStore?.Append(new LocalStateEvent("document_deleted", entry.Document.Id));
            _documents[documentId] = entry with
            {
                Status = KnowledgeLifecycleStatus.Deleted,
                ExpiresAtUtc = null
            };
            AdvanceRevision();
        }
    }

    public void ApplyIngestionBatch(
        IReadOnlyCollection<DocumentRecord> documents,
        IReadOnlyCollection<string> removedDocumentIds)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(removedDocumentIds);
        if (documents.Select(document => document.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() != documents.Count)
        {
            throw new InvalidDataException("本地摄取批次包含重复文档标识。");
        }
        foreach (var document in documents)
        {
            ValidateIngestedDocument(document);
        }
        var incomingIds = documents
            .Select(document => document.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (removedDocumentIds.Any(incomingIds.Contains))
        {
            throw new InvalidDataException("本地摄取批次不能同时更新和删除同一文档。");
        }

        lock (_sync)
        {
            var changed = false;
            foreach (var document in documents)
            {
                if (_documents.TryGetValue(document.Id, out var current) &&
                    !current.Document.SourcePath.StartsWith("local/", StringComparison.Ordinal))
                {
                    throw new InvalidDataException("本地摄取文档标识与批准快照冲突。");
                }
                var replacement = new ManagedDocument(
                    document,
                    KnowledgeLifecycleStatus.Published,
                    ExpiresAtUtc: null);
                if (!_documents.TryGetValue(document.Id, out var existing) ||
                    !ManagedDocumentsEqual(existing, replacement))
                {
                    _documents[document.Id] = replacement;
                    changed = true;
                }
            }

            foreach (var documentId in removedDocumentIds.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (_documents.TryGetValue(documentId, out var existing) &&
                    existing.Document.SourcePath.StartsWith("local/", StringComparison.Ordinal))
                {
                    _documents.Remove(documentId);
                    changed = true;
                }
            }

            if (changed)
            {
                AdvanceRevision();
            }
        }
    }

    public static DocumentRepository LoadApprovedSnapshot(
        string manifestPath,
        LocalStateStore? stateStore = null)
    {
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException("批准数据源清单不存在。");
        }

        var manifestBytes = File.ReadAllBytes(manifestPath);
        ApprovedSourceManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ApprovedSourceManifest>(
                manifestBytes,
                SerializerOptions)
                ?? throw new InvalidDataException("批准数据源清单不能为空。");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("批准数据源清单 JSON 无效或包含未知字段。", exception);
        }

        if (manifest.Documents is null || manifest.Documents.Length == 0)
        {
            throw new InvalidDataException("批准数据源清单不能为空。");
        }

        ValidateManifest(manifest);
        var rootPath = Path.GetFullPath(Path.GetDirectoryName(manifestPath)
            ?? throw new InvalidDataException("批准数据源清单缺少父目录。"));
        RejectReparsePoint(rootPath);

        var documents = new List<DocumentRecord>(manifest.Documents.Length);
        var documentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolvedPaths = new HashSet<string>(PathComparer);
        foreach (var entry in manifest.Documents)
        {
            ValidateEntry(entry);
            if (!documentIds.Add(entry.Id))
            {
                throw new InvalidDataException($"批准数据源包含重复文档标识：{entry.Id}。");
            }

            var sourcePath = ResolveSourcePath(rootPath, entry.RelativePath);
            var canonicalRelative = NormalizeRelativePath(rootPath, sourcePath);
            if (!resolvedPaths.Add(canonicalRelative))
            {
                throw new InvalidDataException($"批准数据源包含冲突的来源路径：{entry.Id}。");
            }

            var sourceBytes = ReadApprovedSourceBytes(sourcePath, entry.Id);

            var actualHash = ComputeSha256(sourceBytes);
            if (!string.Equals(actualHash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"文档 {entry.Id} 的 SHA-256 与批准清单不一致。");
            }

            string content;
            try
            {
                content = StrictUtf8.GetString(sourceBytes).TrimEnd('\r', '\n');
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException($"文档 {entry.Id} 不是有效 UTF-8。", exception);
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                throw new InvalidDataException($"文档 {entry.Id} 的来源文件为空。");
            }

            documents.Add(new DocumentRecord(
                entry.Id,
                manifest.TenantId,
                entry.Version,
                entry.Title,
                entry.Section,
                entry.RelativePath.Replace('\\', '/'),
                content,
                entry.AllowedGroups,
                entry.SearchTerms));
        }

        return new DocumentRepository(
            documents,
            manifest.SourceId,
            ComputeSha256(manifestBytes),
            stateStore);
    }

    // 批准清单必须跨 Windows/Linux 保持同一语义，统一拒绝仅大小写不同的来源路径。
    private static StringComparer PathComparer => StringComparer.OrdinalIgnoreCase;

    private static void ValidateManifest(ApprovedSourceManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.SourceId) ||
            string.IsNullOrWhiteSpace(manifest.Owner) ||
            string.IsNullOrWhiteSpace(manifest.Classification) ||
            manifest.ApprovedFor is null ||
            !manifest.ApprovedFor.Contains("gate-f", StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("批准数据源清单缺少来源、负责人、分类或 Gate F 批准范围。");
        }

        if (!string.Equals(
                manifest.TenantId,
                PocIdentityDirectory.EnterpriseTenantId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("批准数据源清单不属于固定企业 Tenant。");
        }
    }

    private static void ValidateEntry(ApprovedSourceDocument entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Id) ||
            string.IsNullOrWhiteSpace(entry.RelativePath) ||
            string.IsNullOrWhiteSpace(entry.Version) ||
            string.IsNullOrWhiteSpace(entry.Title) ||
            string.IsNullOrWhiteSpace(entry.Section) ||
            entry.AllowedGroups is null ||
            entry.AllowedGroups.Length == 0 ||
            entry.AllowedGroups.Any(string.IsNullOrWhiteSpace) ||
            entry.AllowedGroups.Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
                entry.AllowedGroups.Length ||
            entry.SearchTerms is null ||
            entry.SearchTerms.Length == 0 ||
            entry.SearchTerms.Any(string.IsNullOrWhiteSpace) ||
            entry.Sha256 is null ||
            entry.Sha256.Length != 64 ||
            entry.Sha256.Any(character => !Uri.IsHexDigit(character)))
        {
            // 不回显路径或文件正文，仅使用文档 ID（可能为空）。
            var documentLabel = string.IsNullOrWhiteSpace(entry.Id) ? "(missing-id)" : entry.Id;
            throw new InvalidDataException($"文档 {documentLabel} 缺少有效的权限、检索或完整性元数据。");
        }
    }

    private static string ResolveSourcePath(string rootPath, string relativePath)
    {
        if (Path.IsPathRooted(relativePath) ||
            relativePath.Contains(':', StringComparison.Ordinal) ||
            relativePath.StartsWith("\\\\", StringComparison.Ordinal) ||
            relativePath.StartsWith("//", StringComparison.Ordinal))
        {
            throw new InvalidDataException("来源文件路径必须相对于清单目录。");
        }

        var fullPath = Path.GetFullPath(Path.Combine(rootPath, relativePath));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var rootPrefix = rootPath.EndsWith(Path.DirectorySeparatorChar)
            ? rootPath
            : rootPath + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, comparison))
        {
            throw new InvalidDataException("来源文件路径越过批准数据源目录。");
        }

        if (!File.Exists(fullPath))
        {
            throw new InvalidDataException("批准来源文件不存在或不可访问。");
        }

        var relativeSegments = Path.GetRelativePath(rootPath, fullPath)
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
        var currentPath = rootPath;
        foreach (var segment in relativeSegments)
        {
            currentPath = Path.Combine(currentPath, segment);
            RejectReparsePoint(currentPath);
        }

        return fullPath;
    }

    private static string NormalizeRelativePath(string rootPath, string fullPath)
    {
        var relative = Path.GetRelativePath(rootPath, fullPath)
            .Replace('\\', '/');
        return relative.ToLowerInvariant();
    }

    private static byte[] ReadApprovedSourceBytes(string sourcePath, string documentId)
    {
        using var stream = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        var sourceLength = stream.Length;
        if (sourceLength > MaxSourceFileBytes)
        {
            throw new InvalidDataException($"文档 {documentId} 超过批准快照大小上限。");
        }

        var sourceBytes = new byte[checked((int)sourceLength)];
        var offset = 0;
        while (offset < sourceBytes.Length)
        {
            var read = stream.Read(sourceBytes, offset, sourceBytes.Length - offset);
            if (read == 0)
            {
                throw new InvalidDataException($"文档 {documentId} 在读取期间发生变化。");
            }
            offset += read;
        }

        if (stream.ReadByte() != -1 || stream.Length != sourceLength)
        {
            throw new InvalidDataException($"文档 {documentId} 在读取期间发生变化。");
        }

        return sourceBytes;
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("批准数据源路径不得包含符号链接或重解析点。");
        }
    }

    private static string ComputeSha256(byte[] content)
    {
        return Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
    }

    private ManagedDocument GetMutableDocument(string documentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        return _documents.TryGetValue(documentId, out var entry)
            ? entry
            : throw new KeyNotFoundException("本地知识文档不存在。");
    }

    private static void EnsureNotDeleted(ManagedDocument entry)
    {
        if (entry.Status == KnowledgeLifecycleStatus.Deleted)
        {
            throw new InvalidOperationException("已删除文档不能重新发布或修改。");
        }
    }

    private void AdvanceRevision()
    {
        _revision = checked(_revision + 1);
    }

    private string GetRevisionSourceId(long revision) =>
        revision == 0 ? SourceId : $"{SourceId}:local-revision:{revision}";

    private void Replay(IEnumerable<LocalStateEnvelope> events)
    {
        foreach (var envelope in events)
        {
            var stateEvent = envelope.Event;
            if (!stateEvent.EventType.StartsWith("document_", StringComparison.Ordinal))
            {
                continue;
            }
            if (!_documents.TryGetValue(stateEvent.TargetId, out var entry))
            {
                throw new InvalidDataException("知识生命周期事件引用未知批准文档。");
            }

            switch (stateEvent.EventType)
            {
                case "document_acl_replaced":
                    entry = entry with
                    {
                        Document = entry.Document with
                        {
                            AllowedGroups = stateEvent.Groups
                                ?? throw new InvalidDataException("文档 ACL 事件缺少 Groups。")
                        }
                    };
                    break;
                case "document_withdrawn":
                    entry = entry with { Status = KnowledgeLifecycleStatus.Withdrawn };
                    break;
                case "document_published":
                    if (entry.Status == KnowledgeLifecycleStatus.Deleted)
                    {
                        throw new InvalidDataException("状态账本尝试重新发布已删除文档。");
                    }
                    entry = entry with
                    {
                        Status = KnowledgeLifecycleStatus.Published,
                        ExpiresAtUtc = null
                    };
                    break;
                case "document_expiration_set":
                    entry = entry with
                    {
                        ExpiresAtUtc = stateEvent.ExpiresAtUtc
                            ?? throw new InvalidDataException("文档过期事件缺少时间。")
                    };
                    break;
                case "document_expiration_cleared":
                    entry = entry with { ExpiresAtUtc = null };
                    break;
                case "document_deleted":
                    entry = entry with
                    {
                        Status = KnowledgeLifecycleStatus.Deleted,
                        ExpiresAtUtc = null
                    };
                    break;
                default:
                    throw new InvalidDataException("本地状态账本包含未知知识事件。");
            }

            _documents[stateEvent.TargetId] = entry;
            _revision = checked(_revision + 1);
        }
    }

    private static void ValidateIngestedDocument(DocumentRecord document)
    {
        if (string.IsNullOrWhiteSpace(document.Id) ||
            !string.Equals(
                document.TenantId,
                PocIdentityDirectory.EnterpriseTenantId,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(document.Version) ||
            string.IsNullOrWhiteSpace(document.Title) ||
            string.IsNullOrWhiteSpace(document.Section) ||
            !document.SourcePath.StartsWith("local/", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(document.Content) ||
            document.AllowedGroups.Length == 0 ||
            document.AllowedGroups.Any(string.IsNullOrWhiteSpace) ||
            document.SearchTerms.Length == 0)
        {
            throw new InvalidDataException("本地摄取文档缺少有效版本、权限或来源元数据。");
        }
    }

    private static bool ManagedDocumentsEqual(ManagedDocument left, ManagedDocument right) =>
        left.Status == right.Status &&
        left.ExpiresAtUtc == right.ExpiresAtUtc &&
        left.Document.Id == right.Document.Id &&
        left.Document.TenantId == right.Document.TenantId &&
        left.Document.Version == right.Document.Version &&
        left.Document.Title == right.Document.Title &&
        left.Document.Section == right.Document.Section &&
        left.Document.SourcePath == right.Document.SourcePath &&
        left.Document.Content == right.Document.Content &&
        left.Document.AllowedGroups.SequenceEqual(
            right.Document.AllowedGroups,
            StringComparer.OrdinalIgnoreCase) &&
        left.Document.SearchTerms.SequenceEqual(
            right.Document.SearchTerms,
            StringComparer.OrdinalIgnoreCase);

    private sealed record ManagedDocument(
        DocumentRecord Document,
        KnowledgeLifecycleStatus Status,
        DateTimeOffset? ExpiresAtUtc);
}

public sealed record AuthorizedDocumentSnapshot(
    long Revision,
    string SourceRevisionId,
    IReadOnlyList<DocumentRecord> Documents);

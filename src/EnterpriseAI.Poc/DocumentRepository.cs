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

    private DocumentRepository(
        IReadOnlyList<DocumentRecord> documents,
        string sourceId,
        string manifestSha256)
    {
        Documents = documents;
        SourceId = sourceId;
        ManifestSha256 = manifestSha256;
    }

    public IReadOnlyList<DocumentRecord> Documents { get; }

    public string SourceId { get; }

    public string ManifestSha256 { get; }

    public static DocumentRepository LoadApprovedSnapshot(string manifestPath)
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

            var sourceBytes = File.ReadAllBytes(sourcePath);
            if (sourceBytes.Length > MaxSourceFileBytes)
            {
                throw new InvalidDataException($"文档 {entry.Id} 超过批准快照大小上限。");
            }

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

        return new DocumentRepository(documents, manifest.SourceId, ComputeSha256(manifestBytes));
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

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
        return OperatingSystem.IsWindows() ? relative.ToLowerInvariant() : relative;
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
}

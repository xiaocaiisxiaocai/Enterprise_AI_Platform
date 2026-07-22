using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EnterpriseAI.Poc;

public sealed class DocumentRepository
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private DocumentRepository(IReadOnlyList<DocumentRecord> documents)
    {
        Documents = documents;
    }

    public IReadOnlyList<DocumentRecord> Documents { get; }

    public static DocumentRepository LoadApprovedSnapshot(string manifestPath)
    {
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException("批准数据源清单不存在。", manifestPath);
        }

        var manifest = JsonSerializer.Deserialize<ApprovedSourceManifest>(
            File.ReadAllText(manifestPath),
            SerializerOptions);

        if (manifest is null || manifest.Documents is null || manifest.Documents.Length == 0)
        {
            throw new InvalidDataException("批准数据源清单不能为空。");
        }

        ValidateManifest(manifest);
        var rootPath = Path.GetFullPath(Path.GetDirectoryName(manifestPath)
            ?? throw new InvalidDataException("批准数据源清单缺少父目录。"));
        RejectReparsePoint(rootPath);

        var documents = new List<DocumentRecord>(manifest.Documents.Length);
        var documentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.Documents)
        {
            ValidateEntry(entry);
            if (!documentIds.Add(entry.Id))
            {
                throw new InvalidDataException($"批准数据源包含重复文档标识：{entry.Id}。");
            }

            var sourcePath = ResolveSourcePath(rootPath, entry.RelativePath);
            var actualHash = ComputeSha256(sourcePath);
            if (!string.Equals(actualHash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"文档 {entry.Id} 的 SHA-256 与批准清单不一致。");
            }

            var content = File.ReadAllText(sourcePath).TrimEnd('\r', '\n');
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

        return new DocumentRepository(documents);
    }

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
            entry.SearchTerms is null ||
            entry.SearchTerms.Length == 0 ||
            entry.SearchTerms.Any(string.IsNullOrWhiteSpace) ||
            entry.Sha256 is null ||
            entry.Sha256.Length != 64 ||
            entry.Sha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException($"文档 {entry.Id} 缺少有效的权限、检索或完整性元数据。");
        }
    }

    private static string ResolveSourcePath(string rootPath, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
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
            throw new FileNotFoundException("批准来源文件不存在。", fullPath);
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

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("批准数据源路径不得包含符号链接或重解析点。");
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}

using System.Text.Json;

namespace EnterpriseAI.Poc;

public sealed class DocumentRepository
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private DocumentRepository(IReadOnlyList<DocumentRecord> documents)
    {
        Documents = documents;
    }

    public IReadOnlyList<DocumentRecord> Documents { get; }

    public static DocumentRepository Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("PoC 文档数据文件不存在。", path);
        }

        var documents = JsonSerializer.Deserialize<List<DocumentRecord>>(
            File.ReadAllText(path),
            SerializerOptions);

        if (documents is null || documents.Count == 0)
        {
            throw new InvalidDataException("PoC 文档数据不能为空。");
        }

        foreach (var document in documents)
        {
            if (string.IsNullOrWhiteSpace(document.Id) ||
                string.IsNullOrWhiteSpace(document.TenantId) ||
                document.AllowedGroups.Length == 0 ||
                document.SearchTerms.Length == 0)
            {
                throw new InvalidDataException($"文档 {document.Id} 缺少权限或检索元数据。");
            }
        }

        return new DocumentRepository(documents);
    }
}

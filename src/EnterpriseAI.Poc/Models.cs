namespace EnterpriseAI.Poc;

public sealed record QueryRequest(string Question);

public sealed record ErrorResponse(string Code, string Message);

public sealed record Citation(
    string DocumentId,
    string Version,
    string Title,
    string Section,
    string SourcePath);

public sealed record QueryResponse(
    string Status,
    string Answer,
    IReadOnlyList<Citation> Citations,
    string TraceId);

public sealed record DocumentRecord(
    string Id,
    string TenantId,
    string Version,
    string Title,
    string Section,
    string SourcePath,
    string Content,
    string[] AllowedGroups,
    string[] SearchTerms);

public sealed record ApprovedSourceManifest(
    string SourceId,
    string TenantId,
    string Owner,
    string Classification,
    string[] ApprovedFor,
    ApprovedSourceDocument[] Documents);

public sealed record ApprovedSourceDocument(
    string Id,
    string RelativePath,
    string Version,
    string Title,
    string Section,
    string[] AllowedGroups,
    string[] SearchTerms,
    string Sha256);

public enum KnowledgeLifecycleStatus
{
    Published,
    Withdrawn,
    Deleted
}

public sealed record LocalFileIngestionOptions(
    string RootPath,
    string SourceId,
    string Owner,
    string Classification,
    string[] AllowedGroups);

public sealed record IngestionQuarantineItem(
    string RelativePath,
    string ReasonCode);

public sealed record LocalFileIngestionResult(
    int Added,
    int Updated,
    int Unchanged,
    int Removed,
    int Ignored,
    IReadOnlyList<IngestionQuarantineItem> Quarantined,
    long RepositoryRevision);

public sealed class PocIdentity
{
    public PocIdentity(string principalId, string tenantId, IEnumerable<string> groups)
    {
        PrincipalId = principalId;
        TenantId = tenantId;
        Groups = new HashSet<string>(groups, StringComparer.OrdinalIgnoreCase);
    }

    public string PrincipalId { get; }

    public string TenantId { get; }

    public IReadOnlySet<string> Groups { get; }
}

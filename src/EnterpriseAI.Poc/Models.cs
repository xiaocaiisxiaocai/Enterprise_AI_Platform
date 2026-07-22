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

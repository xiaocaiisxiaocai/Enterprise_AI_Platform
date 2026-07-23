using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace EnterpriseAI.Poc;

public sealed class PermissionAwareSearchService
{
    public const string RefusalMessage = "未找到您有权访问且能够回答该问题的证据。";
    public const string TraceSchemaVersion = "1.0";
    public const string PolicyVersion = "gate-f-acl-v1";

    private readonly DocumentRepository _repository;
    private readonly ISearchTraceSink _traceSink;
    private readonly PocIdentityDirectory? _identityDirectory;

    public PermissionAwareSearchService(
        DocumentRepository repository,
        ISearchTraceSink traceSink,
        PocIdentityDirectory? identityDirectory = null)
    {
        _repository = repository;
        _traceSink = traceSink;
        _identityDirectory = identityDirectory;
    }

    public QueryResponse Query(PocIdentity identity, string question)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(question);

        var currentIdentity = ResolveCurrentIdentity(identity, out var identityActive);
        var snapshot = _repository.GetAuthorizedSnapshot(currentIdentity);

        // 权限过滤必须发生在评分与排序之前，禁止先召回再删除无权结果。
        var matches = snapshot.Documents
            .Select(document => new { Document = document, Score = Score(document, question) })
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Document.Id, StringComparer.Ordinal)
            .ToArray();

        var traceId = ActivityTraceId.CreateRandom().ToString();
        if (matches.Length == 0)
        {
            RecordTrace(
                currentIdentity,
                question,
                traceId,
                "refused",
                identityActive ? "no_authorized_evidence" : "identity_not_active",
                snapshot.SourceRevisionId,
                selectedDocumentId: null,
                citationCount: 0);
            return new QueryResponse("refused", RefusalMessage, [], traceId);
        }

        var evidence = matches[0].Document;
        var citation = new Citation(
            evidence.Id,
            evidence.Version,
            evidence.Title,
            evidence.Section,
            evidence.SourcePath);

        RecordTrace(
            currentIdentity,
            question,
            traceId,
            "answered",
            "authorized_evidence_found",
            snapshot.SourceRevisionId,
            evidence.Id,
            citationCount: 1);

        // Gate F 只返回抽取式证据，不调用模型生成答案。
        return new QueryResponse("answered", evidence.Content, [citation], traceId);
    }

    private void RecordTrace(
        PocIdentity identity,
        string question,
        string traceId,
        string decision,
        string reasonCode,
        string sourceRevisionId,
        string? selectedDocumentId,
        int citationCount)
    {
        var questionHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(question.Trim())))
            .ToLowerInvariant();
        _traceSink.Record(new SearchTraceRecord(
            TraceSchemaVersion,
            traceId,
            DateTimeOffset.UtcNow,
            identity.PrincipalId,
            identity.TenantId,
            identity.Groups.Order(StringComparer.Ordinal).ToArray(),
            questionHash,
            sourceRevisionId,
            _repository.ManifestSha256,
            PolicyVersion,
            decision,
            reasonCode,
            selectedDocumentId,
            citationCount));
    }

    private PocIdentity ResolveCurrentIdentity(PocIdentity requested, out bool active)
    {
        if (_identityDirectory is null)
        {
            active = true;
            return requested;
        }

        if (_identityDirectory.TryResolve(requested.PrincipalId, out var current) &&
            string.Equals(current.TenantId, requested.TenantId, StringComparison.Ordinal))
        {
            active = true;
            return current;
        }

        active = false;
        return new PocIdentity(requested.PrincipalId, requested.TenantId, []);
    }

    private static int Score(DocumentRecord document, string question)
    {
        var normalizedQuestion = question.Trim();
        var score = document.SearchTerms.Count(term =>
            normalizedQuestion.Contains(term, StringComparison.OrdinalIgnoreCase));

        if (document.Content.Contains(normalizedQuestion, StringComparison.OrdinalIgnoreCase))
        {
            score += 3;
        }

        return score;
    }
}

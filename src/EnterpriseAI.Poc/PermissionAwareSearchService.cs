using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace EnterpriseAI.Poc;

public sealed class PermissionAwareSearchService(
    DocumentRepository repository,
    ISearchTraceSink traceSink)
{
    public const string RefusalMessage = "未找到您有权访问且能够回答该问题的证据。";
    public const string TraceSchemaVersion = "1.0";
    public const string PolicyVersion = "gate-f-acl-v1";

    public QueryResponse Query(PocIdentity identity, string question)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);

        // 权限过滤必须发生在评分与排序之前，禁止先召回再删除无权结果。
        var authorizedDocuments = repository.Documents.Where(document => CanRead(identity, document));
        var matches = authorizedDocuments
            .Select(document => new { Document = document, Score = Score(document, question) })
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Document.Id, StringComparer.Ordinal)
            .ToArray();

        var traceId = ActivityTraceId.CreateRandom().ToString();
        if (matches.Length == 0)
        {
            RecordTrace(
                identity,
                question,
                traceId,
                "refused",
                "no_authorized_evidence",
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
            identity,
            question,
            traceId,
            "answered",
            "authorized_evidence_found",
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
        string? selectedDocumentId,
        int citationCount)
    {
        var questionHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(question.Trim())))
            .ToLowerInvariant();
        traceSink.Record(new SearchTraceRecord(
            TraceSchemaVersion,
            traceId,
            DateTimeOffset.UtcNow,
            identity.PrincipalId,
            identity.TenantId,
            identity.Groups.Order(StringComparer.Ordinal).ToArray(),
            questionHash,
            repository.SourceId,
            repository.ManifestSha256,
            PolicyVersion,
            decision,
            reasonCode,
            selectedDocumentId,
            citationCount));
    }

    private static bool CanRead(PocIdentity identity, DocumentRecord document) =>
        string.Equals(identity.TenantId, document.TenantId, StringComparison.Ordinal) &&
        document.AllowedGroups.Any(identity.Groups.Contains);

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

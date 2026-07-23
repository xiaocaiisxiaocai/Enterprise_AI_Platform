namespace EnterpriseAI.Poc.Evaluation;

public sealed record GoldenDataset(
    string SchemaVersion,
    string DatasetId,
    string Version,
    string TenantId,
    GoldenCase[] Cases);

public sealed record GoldenCase(
    string Id,
    string Category,
    string PrincipalId,
    string Question,
    string ExpectedStatus,
    string[] ExpectedDocumentIds,
    string[] ForbiddenDocumentIds);

public sealed record EvaluationCaseResult(
    string CaseId,
    string Category,
    string ActualStatus,
    string[] ActualDocumentIds,
    int ForbiddenCitationCount,
    bool Passed,
    string[] FailureReasons);

public sealed record EvaluationMetrics(
    int TotalCases,
    int PassedCases,
    int UnauthorizedCitationCount,
    decimal CasePassRate,
    decimal CitationExactMatchRate,
    decimal RefusalConsistencyRate);

public sealed record EvaluationReport(
    string SchemaVersion,
    string EvaluationType,
    string Status,
    DateTimeOffset GeneratedAtUtc,
    string DatasetId,
    string DatasetVersion,
    string DatasetSha256,
    string SnapshotSourceId,
    string SnapshotManifestSha256,
    string PolicyVersion,
    bool NegativeSelfTestPassed,
    EvaluationMetrics Metrics,
    long TraceEntryCount,
    string TraceFinalHash,
    string TraceArtifactFile,
    EvaluationCaseResult[] Cases,
    string[] Limitations);

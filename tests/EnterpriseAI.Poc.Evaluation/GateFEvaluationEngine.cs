using EnterpriseAI.Poc;

namespace EnterpriseAI.Poc.Evaluation;

public sealed class GateFEvaluationEngine(
    DocumentRepository repository,
    PocIdentityDirectory identities)
{
    public EvaluationReport Run(
        GoldenDataset dataset,
        string datasetSha256,
        string tracePath)
    {
        ValidateDataset(dataset);
        if (File.Exists(tracePath))
        {
            throw new InvalidOperationException("评测 Trace 路径必须是新的输出文件。");
        }

        var traceSink = new HashChainedJsonLineTraceSink(tracePath);
        var search = new PermissionAwareSearchService(repository, traceSink);
        var results = dataset.Cases
            .Select(testCase => EvaluateCase(search, testCase))
            .ToArray();
        var traceValidation = HashChainedJsonLineTraceSink.Validate(tracePath);
        if (traceValidation.EntryCount != dataset.Cases.Length)
        {
            throw new InvalidDataException("评测 Trace 条目数与数据集用例数不一致。");
        }

        var answeredCases = dataset.Cases.Count(testCase => testCase.ExpectedStatus == "answered");
        var refusedCases = dataset.Cases.Length - answeredCases;
        var exactCitationCases = results.Count(result =>
            result.FailureReasons.All(reason => reason != "citation_mismatch"));
        var consistentRefusals = Enumerable.Range(0, dataset.Cases.Length).Count(index =>
            dataset.Cases[index].ExpectedStatus == "refused" &&
            results[index].ActualStatus == "refused" &&
            results[index].FailureReasons.All(reason => reason != "refusal_message_mismatch"));
        var unauthorizedCitationCount = results.Sum(result => result.ForbiddenCitationCount);
        var passedCases = results.Count(result => result.Passed);
        var metrics = new EvaluationMetrics(
            dataset.Cases.Length,
            passedCases,
            unauthorizedCitationCount,
            Rate(passedCases, dataset.Cases.Length),
            Rate(exactCitationCases, dataset.Cases.Length),
            Rate(consistentRefusals, refusedCases));
        var negativeSelfTestPassed = RunNegativeSelfTest();
        var passed = passedCases == dataset.Cases.Length &&
            unauthorizedCitationCount == 0 &&
            metrics.CitationExactMatchRate == 1m &&
            metrics.RefusalConsistencyRate == 1m &&
            negativeSelfTestPassed;

        return new EvaluationReport(
            "1.0",
            "gate-f-local-deterministic",
            passed ? "PassedLocalDeterministicEvaluation" : "FailedLocalDeterministicEvaluation",
            DateTimeOffset.UtcNow,
            dataset.DatasetId,
            dataset.Version,
            datasetSha256,
            repository.SourceId,
            repository.ManifestSha256,
            PermissionAwareSearchService.PolicyVersion,
            negativeSelfTestPassed,
            metrics,
            traceValidation.EntryCount,
            traceValidation.FinalHash,
            Path.GetFileName(tracePath),
            results,
            [
                "No model or probabilistic AI evaluation",
                "No real identity provider, business data, or dynamic ACL propagation",
                "Metrics apply only to this synthetic, versioned dataset"
            ]);
    }

    private bool RunNegativeSelfTest()
    {
        var sink = new InMemorySearchTraceSink();
        var search = new PermissionAwareSearchService(repository, sink);
        var intentionallyWrong = new GoldenCase(
            "SELF-TEST-FAILURE",
            "evaluator_negative_self_test",
            "alice-finance",
            "预算报销规则是什么",
            "refused",
            [],
            ["doc-finance-001"]);
        var result = EvaluateCase(search, intentionallyWrong);
        return !result.Passed &&
            result.ForbiddenCitationCount == 1 &&
            result.FailureReasons.Contains("status_mismatch", StringComparer.Ordinal) &&
            result.FailureReasons.Contains("citation_mismatch", StringComparer.Ordinal);
    }

    private EvaluationCaseResult EvaluateCase(
        PermissionAwareSearchService search,
        GoldenCase testCase)
    {
        if (!identities.TryResolve(testCase.PrincipalId, out var identity))
        {
            throw new InvalidDataException($"评测用例 {testCase.Id} 引用了未知合成主体。");
        }

        var response = search.Query(identity, testCase.Question);
        var actualDocumentIds = response.Citations
            .Select(citation => citation.DocumentId)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var expectedDocumentIds = testCase.ExpectedDocumentIds
            .Order(StringComparer.Ordinal)
            .ToArray();
        var forbiddenCitationCount = actualDocumentIds.Count(documentId =>
            testCase.ForbiddenDocumentIds.Contains(documentId, StringComparer.Ordinal));
        var failures = new List<string>();

        if (!string.Equals(response.Status, testCase.ExpectedStatus, StringComparison.Ordinal))
        {
            failures.Add("status_mismatch");
        }
        if (!actualDocumentIds.SequenceEqual(expectedDocumentIds, StringComparer.Ordinal))
        {
            failures.Add("citation_mismatch");
        }
        if (forbiddenCitationCount > 0)
        {
            failures.Add("forbidden_citation");
        }
        if (testCase.ExpectedStatus == "refused" &&
            !string.Equals(
                response.Answer,
                PermissionAwareSearchService.RefusalMessage,
                StringComparison.Ordinal))
        {
            failures.Add("refusal_message_mismatch");
        }

        return new EvaluationCaseResult(
            testCase.Id,
            testCase.Category,
            response.Status,
            actualDocumentIds,
            forbiddenCitationCount,
            failures.Count == 0,
            failures.ToArray());
    }

    private void ValidateDataset(GoldenDataset dataset)
    {
        if (dataset.SchemaVersion != "1.0" ||
            string.IsNullOrWhiteSpace(dataset.DatasetId) ||
            string.IsNullOrWhiteSpace(dataset.Version) ||
            dataset.TenantId != PocIdentityDirectory.EnterpriseTenantId ||
            dataset.Cases is null ||
            dataset.Cases.Length == 0)
        {
            throw new InvalidDataException("Golden Dataset 缺少有效版本、Tenant 或用例。");
        }

        var caseIds = new HashSet<string>(StringComparer.Ordinal);
        var repositoryDocumentIds = repository.Documents
            .Select(document => document.Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var testCase in dataset.Cases)
        {
            if (string.IsNullOrWhiteSpace(testCase.Id) ||
                !caseIds.Add(testCase.Id) ||
                string.IsNullOrWhiteSpace(testCase.Category) ||
                string.IsNullOrWhiteSpace(testCase.PrincipalId) ||
                string.IsNullOrWhiteSpace(testCase.Question) ||
                testCase.Question.Length > 500 ||
                testCase.ExpectedStatus is not ("answered" or "refused") ||
                testCase.ExpectedDocumentIds is null ||
                testCase.ForbiddenDocumentIds is null ||
                testCase.ExpectedDocumentIds.Any(string.IsNullOrWhiteSpace) ||
                testCase.ForbiddenDocumentIds.Any(string.IsNullOrWhiteSpace) ||
                testCase.ExpectedDocumentIds.Distinct(StringComparer.Ordinal).Count() !=
                    testCase.ExpectedDocumentIds.Length ||
                testCase.ForbiddenDocumentIds.Distinct(StringComparer.Ordinal).Count() !=
                    testCase.ForbiddenDocumentIds.Length ||
                testCase.ExpectedDocumentIds.Intersect(
                    testCase.ForbiddenDocumentIds,
                    StringComparer.Ordinal).Any() ||
                testCase.ExpectedDocumentIds.Concat(testCase.ForbiddenDocumentIds)
                    .Any(documentId => !repositoryDocumentIds.Contains(documentId)) ||
                (testCase.ExpectedStatus == "answered" && testCase.ExpectedDocumentIds.Length == 0) ||
                (testCase.ExpectedStatus == "refused" && testCase.ExpectedDocumentIds.Length != 0))
            {
                throw new InvalidDataException($"Golden Dataset 用例 {testCase.Id} 契约无效。");
            }
        }
    }

    private static decimal Rate(int numerator, int denominator) =>
        denominator == 0 ? 1m : decimal.Divide(numerator, denominator);
}

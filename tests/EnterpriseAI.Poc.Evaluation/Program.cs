using EnterpriseAI.Poc;
using EnterpriseAI.Poc.Evaluation;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

if (args is ["--self-test"])
{
    return RunContractSelfTest();
}

if (args.Length != 4)
{
    Console.Error.WriteLine(
        "用法：EnterpriseAI.Poc.Evaluation <dataset> <approved-source> <report-output> <trace-output>");
    Console.Error.WriteLine("或：EnterpriseAI.Poc.Evaluation --self-test");
    return 2;
}

var serializerOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = false,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    WriteIndented = true
};

var reportPath = Path.GetFullPath(args[2]);
try
{
    var datasetPath = Path.GetFullPath(args[0]);
    var manifestPath = Path.GetFullPath(args[1]);
    var tracePath = Path.GetFullPath(args[3]);
    var datasetBytes = File.ReadAllBytes(datasetPath);
    var dataset = JsonSerializer.Deserialize<GoldenDataset>(datasetBytes, serializerOptions)
        ?? throw new InvalidDataException("Golden Dataset 无法反序列化。");
    var datasetSha256 = Convert.ToHexString(SHA256.HashData(datasetBytes)).ToLowerInvariant();
    var repository = DocumentRepository.LoadApprovedSnapshot(manifestPath);
    var engine = new GateFEvaluationEngine(repository, new PocIdentityDirectory());
    var report = engine.Run(dataset, datasetSha256, tracePath);

    // 仅在评测完整返回后写报告；异常路径不得留下伪造 Passed 报告。
    var reportDirectory = Path.GetDirectoryName(reportPath)
        ?? throw new InvalidDataException("评测报告缺少父目录。");
    Directory.CreateDirectory(reportDirectory);
    var reportJson = JsonSerializer.Serialize(report, serializerOptions);
    File.WriteAllText(reportPath, reportJson, new System.Text.UTF8Encoding(false));

    var passed = report.Status.StartsWith("Passed", StringComparison.Ordinal) &&
        report.NegativeSelfTestPassed;
    Console.WriteLine(
        $"GATE_F_EVALUATION={(passed ? "PASS" : "FAIL")} " +
        $"cases={report.Metrics.PassedCases}/{report.Metrics.TotalCases} " +
        $"unauthorized_citations={report.Metrics.UnauthorizedCitationCount} " +
        $"trace_final_hash={report.TraceFinalHash}");
    Console.WriteLine(
        "GATE_F_SUMMARY " +
        $"dataset_sha256={report.DatasetSha256} " +
        $"golden_cases={report.Metrics.PassedCases}/{report.Metrics.TotalCases} " +
        $"unauthorized_citations={report.Metrics.UnauthorizedCitationCount} " +
        $"trace_final_hash={report.TraceFinalHash} " +
        "limitations=local-deterministic-only;no-probabilistic-ai-eval");
    return passed ? 0 : 1;
}
catch (Exception exception)
{
    TryDeleteUntrustedReport(reportPath);
    Console.Error.WriteLine($"GATE_F_EVALUATION=ERROR {exception.GetType().Name}: {exception.Message}");
    return 1;
}

static int RunContractSelfTest()
{
    var manifestPath = Path.Combine(AppContext.BaseDirectory, "Data", "approved-source.json");
    var datasetPath = ResolveGoldenDatasetPath();
    var repository = DocumentRepository.LoadApprovedSnapshot(manifestPath);
    return GoldenDatasetContractRegression.RunSelfTest(repository, datasetPath);
}

static string ResolveGoldenDatasetPath()
{
    var candidates = new[]
    {
        Path.Combine(AppContext.BaseDirectory, "evaluation", "gate-f-golden-v1.json"),
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "evaluation", "gate-f-golden-v1.json"))
    };
    foreach (var candidate in candidates)
    {
        if (File.Exists(candidate))
        {
            return candidate;
        }
    }

    throw new FileNotFoundException("未找到 evaluation\\gate-f-golden-v1.json。");
}

static void TryDeleteUntrustedReport(string reportPath)
{
    try
    {
        if (File.Exists(reportPath))
        {
            var text = File.ReadAllText(reportPath);
            if (text.Contains("PassedLocalDeterministicEvaluation", StringComparison.Ordinal))
            {
                File.Delete(reportPath);
            }
        }
    }
    catch
    {
        // 清理失败不掩盖评测错误退出码。
    }
}

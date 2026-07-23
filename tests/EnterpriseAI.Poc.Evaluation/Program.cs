using EnterpriseAI.Poc;
using EnterpriseAI.Poc.Evaluation;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

if (args.Length != 4)
{
    Console.Error.WriteLine(
        "用法：EnterpriseAI.Poc.Evaluation <dataset> <approved-source> <report-output> <trace-output>");
    return 2;
}

var serializerOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = false,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    WriteIndented = true
};

try
{
    var datasetPath = Path.GetFullPath(args[0]);
    var manifestPath = Path.GetFullPath(args[1]);
    var reportPath = Path.GetFullPath(args[2]);
    var tracePath = Path.GetFullPath(args[3]);
    var datasetBytes = File.ReadAllBytes(datasetPath);
    var dataset = JsonSerializer.Deserialize<GoldenDataset>(datasetBytes, serializerOptions)
        ?? throw new InvalidDataException("Golden Dataset 无法反序列化。");
    var datasetSha256 = Convert.ToHexString(SHA256.HashData(datasetBytes)).ToLowerInvariant();
    var repository = DocumentRepository.LoadApprovedSnapshot(manifestPath);
    var engine = new GateFEvaluationEngine(repository, new PocIdentityDirectory());
    var report = engine.Run(dataset, datasetSha256, tracePath);

    var reportDirectory = Path.GetDirectoryName(reportPath)
        ?? throw new InvalidDataException("评测报告缺少父目录。");
    Directory.CreateDirectory(reportDirectory);
    var reportJson = JsonSerializer.Serialize(report, serializerOptions);
    File.WriteAllText(reportPath, reportJson, new System.Text.UTF8Encoding(false));

    Console.WriteLine(
        $"GATE_F_EVALUATION={(report.Status.StartsWith("Passed", StringComparison.Ordinal) ? "PASS" : "FAIL")} " +
        $"cases={report.Metrics.PassedCases}/{report.Metrics.TotalCases} " +
        $"unauthorized_citations={report.Metrics.UnauthorizedCitationCount} " +
        $"trace_final_hash={report.TraceFinalHash}");
    return report.Status.StartsWith("Passed", StringComparison.Ordinal) &&
        report.NegativeSelfTestPassed
        ? 0
        : 1;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"GATE_F_EVALUATION=ERROR {exception.GetType().Name}: {exception.Message}");
    return 1;
}

using EnterpriseAI.Poc;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EnterpriseAI.Poc.Evaluation;

/// <summary>
/// Golden Dataset 契约负向回归：损坏样例必须失败，进程非零退出，且不得产生伪造的 Passed 报告。
/// </summary>
public static class GoldenDatasetContractRegression
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    public static IReadOnlyList<(string Id, string Name, Action Test)> BuildCases(
        DocumentRepository repository,
        string validDatasetPath,
        string approvedManifestPath)
    {
        return
        [
            ("REG-EVAL-001", "重复 Case ID", () =>
                ExpectRejected(
                    repository,
                    MutateValidCase(validCase => validCase with { Id = "EVAL-GF-001" },
                        extraCase: CreateValidCase("EVAL-GF-001", "answered", ["doc-finance-001"], ["doc-hr-001"])))),
            ("REG-EVAL-002", "未知主体", () =>
                ExpectRejected(
                    repository,
                    MutateValidCase(validCase => validCase with
                    {
                        Id = "EVAL-BAD-UNKNOWN-PRINCIPAL",
                        PrincipalId = "not-a-synthetic-principal"
                    }))),
            ("REG-EVAL-003", "错误 Tenant", () =>
                ExpectRejected(
                    repository,
                    MutateValidCase(
                        validCase => validCase,
                        tenantId: "attacker-tenant"))),
            ("REG-EVAL-004", "非法状态", () =>
                ExpectRejected(
                    repository,
                    MutateValidCase(validCase => validCase with
                    {
                        Id = "EVAL-BAD-STATUS",
                        ExpectedStatus = "maybe"
                    }))),
            ("REG-EVAL-005", "空问题", () =>
                ExpectRejected(
                    repository,
                    MutateValidCase(validCase => validCase with
                    {
                        Id = "EVAL-BAD-EMPTY-QUESTION",
                        Question = "   "
                    }))),
            ("REG-EVAL-006", "重复文档 ID", () =>
                ExpectRejected(
                    repository,
                    MutateValidCase(validCase => validCase with
                    {
                        Id = "EVAL-BAD-DUP-DOC",
                        ExpectedDocumentIds = ["doc-finance-001", "doc-finance-001"],
                        ForbiddenDocumentIds = ["doc-hr-001"]
                    }))),
            ("REG-EVAL-007", "空白文档 ID", () =>
                ExpectRejected(
                    repository,
                    MutateValidCase(validCase => validCase with
                    {
                        Id = "EVAL-BAD-BLANK-DOC",
                        ExpectedDocumentIds = ["doc-finance-001"],
                        ForbiddenDocumentIds = ["  "]
                    }))),
            ("REG-EVAL-008", "不存在的文档 ID", () =>
                ExpectRejected(
                    repository,
                    MutateValidCase(validCase => validCase with
                    {
                        Id = "EVAL-BAD-MISSING-DOC",
                        ExpectedDocumentIds = ["doc-does-not-exist"],
                        ForbiddenDocumentIds = ["doc-hr-001"]
                    }))),
            ("REG-EVAL-009", "期望与禁止引用重叠", () =>
                ExpectRejected(
                    repository,
                    MutateValidCase(validCase => validCase with
                    {
                        Id = "EVAL-BAD-OVERLAP",
                        ExpectedDocumentIds = ["doc-finance-001"],
                        ForbiddenDocumentIds = ["doc-finance-001"]
                    }))),
            ("REG-EVAL-010", "回答用例无期望引用", () =>
                ExpectRejected(
                    repository,
                    MutateValidCase(validCase => validCase with
                    {
                        Id = "EVAL-BAD-ANSWER-NO-CITE",
                        ExpectedStatus = "answered",
                        ExpectedDocumentIds = [],
                        ForbiddenDocumentIds = ["doc-hr-001"]
                    }))),
            ("REG-EVAL-011", "拒答用例含期望引用", () =>
                ExpectRejected(
                    repository,
                    MutateValidCase(validCase => validCase with
                    {
                        Id = "EVAL-BAD-REFUSE-WITH-CITE",
                        ExpectedStatus = "refused",
                        ExpectedDocumentIds = ["doc-finance-001"],
                        ForbiddenDocumentIds = ["doc-hr-001"]
                    }))),
            ("REG-EVAL-012", "复用已有 Trace 路径", () =>
                ExpectExistingTraceRejected(repository, validDatasetPath)),
            ("REG-EVAL-013", "正常 Golden Dataset 12/12 通过", () =>
                ExpectValidDatasetPasses(repository, validDatasetPath)),
            ("REG-EVAL-014", "损坏样例进程级非零退出且不伪造 Passed 报告", () =>
                ExpectAllCorruptDatasetsFailViaProcess(approvedManifestPath, validDatasetPath)),
            ("REG-EVAL-015", "复用 Trace 路径进程级非零退出", () =>
                ExpectExistingTraceFailsViaProcess(approvedManifestPath, validDatasetPath))
        ];
    }

    public static int RunSelfTest(
        DocumentRepository repository,
        string validDatasetPath,
        string approvedManifestPath)
    {
        var failures = new List<string>();
        var cases = BuildCases(repository, validDatasetPath, approvedManifestPath);
        foreach (var (id, name, test) in cases)
        {
            try
            {
                test();
                Console.WriteLine($"PASS {id} {name}");
            }
            catch (Exception exception)
            {
                failures.Add($"FAIL {id} {name}: {exception.Message}");
                Console.Error.WriteLine($"FAIL {id} {name}: {exception.Message}");
            }
        }

        if (failures.Count > 0)
        {
            Console.Error.WriteLine($"GATE_F_EVAL_CONTRACT=FAILED count={failures.Count}");
            return 1;
        }

        Console.WriteLine($"GATE_F_EVAL_CONTRACT=PASS count={cases.Count}");
        return 0;
    }

    private static GoldenCase CreateValidCase(
        string id,
        string status,
        string[] expected,
        string[] forbidden) =>
        new(
            id,
            "contract_regression",
            "alice-finance",
            "预算报销规则是什么",
            status,
            expected,
            forbidden);

    private static GoldenDataset MutateValidCase(
        Func<GoldenCase, GoldenCase> mutate,
        string tenantId = PocIdentityDirectory.EnterpriseTenantId,
        GoldenCase? extraCase = null)
    {
        var baseCase = CreateValidCase(
            "EVAL-CONTRACT-BASE",
            "answered",
            ["doc-finance-001"],
            ["doc-hr-001"]);
        var cases = extraCase is null
            ? new[] { mutate(baseCase) }
            : new[] { mutate(baseCase), extraCase };
        return new GoldenDataset(
            "1.0",
            "gate-f-golden-contract-regression",
            "1",
            tenantId,
            cases);
    }

    private static IEnumerable<(string Name, GoldenDataset Dataset)> EnumerateCorruptDatasets()
    {
        yield return ("duplicate-case-id", MutateValidCase(
            validCase => validCase with { Id = "EVAL-GF-001" },
            extraCase: CreateValidCase("EVAL-GF-001", "answered", ["doc-finance-001"], ["doc-hr-001"])));
        yield return ("unknown-principal", MutateValidCase(validCase => validCase with
        {
            Id = "EVAL-BAD-UNKNOWN-PRINCIPAL",
            PrincipalId = "not-a-synthetic-principal"
        }));
        yield return ("wrong-tenant", MutateValidCase(validCase => validCase, tenantId: "attacker-tenant"));
        yield return ("illegal-status", MutateValidCase(validCase => validCase with
        {
            Id = "EVAL-BAD-STATUS",
            ExpectedStatus = "maybe"
        }));
        yield return ("empty-question", MutateValidCase(validCase => validCase with
        {
            Id = "EVAL-BAD-EMPTY-QUESTION",
            Question = "   "
        }));
        yield return ("duplicate-document-id", MutateValidCase(validCase => validCase with
        {
            Id = "EVAL-BAD-DUP-DOC",
            ExpectedDocumentIds = ["doc-finance-001", "doc-finance-001"],
            ForbiddenDocumentIds = ["doc-hr-001"]
        }));
        yield return ("blank-document-id", MutateValidCase(validCase => validCase with
        {
            Id = "EVAL-BAD-BLANK-DOC",
            ExpectedDocumentIds = ["doc-finance-001"],
            ForbiddenDocumentIds = ["  "]
        }));
        yield return ("missing-document-id", MutateValidCase(validCase => validCase with
        {
            Id = "EVAL-BAD-MISSING-DOC",
            ExpectedDocumentIds = ["doc-does-not-exist"],
            ForbiddenDocumentIds = ["doc-hr-001"]
        }));
        yield return ("expected-forbidden-overlap", MutateValidCase(validCase => validCase with
        {
            Id = "EVAL-BAD-OVERLAP",
            ExpectedDocumentIds = ["doc-finance-001"],
            ForbiddenDocumentIds = ["doc-finance-001"]
        }));
        yield return ("answered-without-expected-citation", MutateValidCase(validCase => validCase with
        {
            Id = "EVAL-BAD-ANSWER-NO-CITE",
            ExpectedStatus = "answered",
            ExpectedDocumentIds = [],
            ForbiddenDocumentIds = ["doc-hr-001"]
        }));
        yield return ("refused-with-expected-citation", MutateValidCase(validCase => validCase with
        {
            Id = "EVAL-BAD-REFUSE-WITH-CITE",
            ExpectedStatus = "refused",
            ExpectedDocumentIds = ["doc-finance-001"],
            ForbiddenDocumentIds = ["doc-hr-001"]
        }));
    }

    private static void ExpectRejected(DocumentRepository repository, GoldenDataset dataset)
    {
        WithTemporaryOutputs((reportPath, tracePath) =>
        {
            var engine = new GateFEvaluationEngine(repository, new PocIdentityDirectory());
            try
            {
                engine.Run(dataset, "deadbeef", tracePath);
                throw new InvalidOperationException("损坏 Golden Dataset 未被拒绝。");
            }
            catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException)
            {
                if (exception.Message.Contains("损坏 Golden Dataset 未被拒绝", StringComparison.Ordinal))
                {
                    throw;
                }
            }

            AssertNoPassedReport(reportPath);
            if (File.Exists(reportPath))
            {
                throw new InvalidOperationException("损坏样例不应由调用方写入报告；引擎不得产生报告文件。");
            }
        });
    }

    private static void ExpectExistingTraceRejected(
        DocumentRepository repository,
        string validDatasetPath)
    {
        WithTemporaryOutputs((reportPath, tracePath) =>
        {
            File.WriteAllText(tracePath, string.Empty, new UTF8Encoding(false));
            var dataset = LoadDataset(validDatasetPath);
            var engine = new GateFEvaluationEngine(repository, new PocIdentityDirectory());
            try
            {
                engine.Run(dataset, "deadbeef", tracePath);
                throw new InvalidOperationException("复用已有 Trace 路径未被拒绝。");
            }
            catch (InvalidOperationException exception)
            {
                if (exception.Message.Contains("复用已有 Trace 路径未被拒绝", StringComparison.Ordinal))
                {
                    throw;
                }
            }

            AssertNoPassedReport(reportPath);
        });
    }

    private static void ExpectValidDatasetPasses(
        DocumentRepository repository,
        string validDatasetPath)
    {
        WithTemporaryOutputs((reportPath, tracePath) =>
        {
            var datasetBytes = File.ReadAllBytes(validDatasetPath);
            var dataset = LoadDataset(validDatasetPath);
            var datasetSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(datasetBytes))
                .ToLowerInvariant();
            var engine = new GateFEvaluationEngine(repository, new PocIdentityDirectory());
            var report = engine.Run(dataset, datasetSha256, tracePath);

            if (report.Status != "PassedLocalDeterministicEvaluation" ||
                !report.NegativeSelfTestPassed ||
                report.Metrics.TotalCases != 12 ||
                report.Metrics.PassedCases != 12 ||
                report.Metrics.UnauthorizedCitationCount != 0 ||
                report.TraceEntryCount != 12)
            {
                throw new InvalidOperationException(
                    $"正常 Golden Dataset 未通过：status={report.Status}, " +
                    $"passed={report.Metrics.PassedCases}/{report.Metrics.TotalCases}");
            }

            var reportJson = JsonSerializer.Serialize(report, SerializerOptions);
            File.WriteAllText(reportPath, reportJson, new UTF8Encoding(false));
            if (!reportJson.Contains("PassedLocalDeterministicEvaluation", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("通过报告缺少 Passed 状态。");
            }
        });
    }

    private static void ExpectAllCorruptDatasetsFailViaProcess(
        string approvedManifestPath,
        string validDatasetPath)
    {
        _ = validDatasetPath;
        var evaluationAssembly = typeof(GoldenDatasetContractRegression).Assembly.Location;
        if (string.IsNullOrWhiteSpace(evaluationAssembly) || !File.Exists(evaluationAssembly))
        {
            throw new InvalidOperationException("无法定位 Evaluation 程序集用于进程级验收。");
        }

        foreach (var (name, dataset) in EnumerateCorruptDatasets())
        {
            WithTemporaryOutputs((reportPath, tracePath) =>
            {
                var datasetPath = Path.Combine(
                    Path.GetDirectoryName(reportPath)!,
                    $"corrupt-{name}.json");
                File.WriteAllText(
                    datasetPath,
                    JsonSerializer.Serialize(dataset, SerializerOptions),
                    new UTF8Encoding(false));

                var exitCode = RunEvaluationProcess(
                    evaluationAssembly,
                    datasetPath,
                    approvedManifestPath,
                    reportPath,
                    tracePath,
                    out var standardError);

                if (exitCode == 0)
                {
                    throw new InvalidOperationException(
                        $"损坏样例 {name} 进程退出码为 0（必须非零）。stderr={standardError}");
                }

                AssertNoPassedReport(reportPath);
                if (File.Exists(reportPath))
                {
                    var text = File.ReadAllText(reportPath);
                    if (text.Contains("PassedLocalDeterministicEvaluation", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException($"损坏样例 {name} 生成了伪造 Passed 报告。");
                    }
                }
            });
        }
    }

    private static void ExpectExistingTraceFailsViaProcess(
        string approvedManifestPath,
        string validDatasetPath)
    {
        var evaluationAssembly = typeof(GoldenDatasetContractRegression).Assembly.Location;
        WithTemporaryOutputs((reportPath, tracePath) =>
        {
            File.WriteAllText(tracePath, string.Empty, new UTF8Encoding(false));
            var exitCode = RunEvaluationProcess(
                evaluationAssembly,
                validDatasetPath,
                approvedManifestPath,
                reportPath,
                tracePath,
                out var standardError);
            if (exitCode == 0)
            {
                throw new InvalidOperationException(
                    $"复用 Trace 路径进程退出码为 0。stderr={standardError}");
            }

            AssertNoPassedReport(reportPath);
        });
    }

    private static int RunEvaluationProcess(
        string assemblyPath,
        string datasetPath,
        string manifestPath,
        string reportPath,
        string tracePath,
        out string standardError)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(assemblyPath);
        startInfo.ArgumentList.Add(datasetPath);
        startInfo.ArgumentList.Add(manifestPath);
        startInfo.ArgumentList.Add(reportPath);
        startInfo.ArgumentList.Add(tracePath);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 Evaluation 进程。");
        var stdout = process.StandardOutput.ReadToEnd();
        standardError = process.StandardError.ReadToEnd();
        process.WaitForExit(120_000);
        if (!process.HasExited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // ignore
            }

            throw new TimeoutException("Evaluation 进程超时。");
        }

        _ = stdout;
        return process.ExitCode;
    }

    private static GoldenDataset LoadDataset(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return JsonSerializer.Deserialize<GoldenDataset>(bytes, SerializerOptions)
            ?? throw new InvalidDataException("无法加载 Golden Dataset。");
    }

    private static void AssertNoPassedReport(string reportPath)
    {
        if (!File.Exists(reportPath))
        {
            return;
        }

        var text = File.ReadAllText(reportPath);
        if (text.Contains("PassedLocalDeterministicEvaluation", StringComparison.Ordinal) ||
            text.Contains("\"status\": \"Passed", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("损坏样例生成了伪造的 Passed 报告。");
        }
    }

    private static void WithTemporaryOutputs(Action<string, string> test)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "enterprise-ai-eval-contract",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var reportPath = Path.Combine(root, "report.json");
        var tracePath = Path.Combine(root, "traces.jsonl");

        try
        {
            test(reportPath, tracePath);
        }
        finally
        {
            var expectedParent = Path.GetFullPath(Path.Combine(
                Path.GetTempPath(),
                "enterprise-ai-eval-contract")) + Path.DirectorySeparatorChar;
            var resolvedRoot = Path.GetFullPath(root);
            if (resolvedRoot.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase))
            {
                Directory.Delete(resolvedRoot, recursive: true);
            }
        }
    }
}

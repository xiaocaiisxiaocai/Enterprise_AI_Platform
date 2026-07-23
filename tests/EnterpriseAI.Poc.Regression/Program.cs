using EnterpriseAI.Poc;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var dataPath = Path.Combine(AppContext.BaseDirectory, "Data", "approved-source.json");
var repository = DocumentRepository.LoadApprovedSnapshot(dataPath);
var identities = new PocIdentityDirectory();
var traceSink = new InMemorySearchTraceSink();
var search = new PermissionAwareSearchService(repository, traceSink);
var failures = new List<string>();

Run("REG-AUTH-001 未知身份不能解析", () =>
{
    AssertFalse(identities.TryResolve("unknown-user", out _), "未知身份被接受");
});

Run("REG-ACL-001 财务用户可以检索财务证据", () =>
{
    var identity = Resolve("alice-finance");
    var response = search.Query(identity, "预算报销规则");
    AssertEqual("answered", response.Status, "财务问题未返回答案");
    AssertEqual("doc-finance-001", SingleCitation(response).DocumentId, "未引用财务文档");
});

Run("REG-ACL-002 HR 用户不能探测财务文档", () =>
{
    var identity = Resolve("bob-hr");
    var forbiddenResponse = search.Query(identity, "预算报销规则");
    var unknownResponse = search.Query(identity, "食堂本周菜单");
    AssertEqual("refused", forbiddenResponse.Status, "HR 用户获得财务答案");
    AssertEqual(0, forbiddenResponse.Citations.Count, "拒答泄漏了财务引用");
    AssertEqual(unknownResponse.Answer, forbiddenResponse.Answer, "拒答文案泄漏了隐藏文档是否存在");
});

Run("REG-ACL-003 HR 用户可以检索 HR 证据", () =>
{
    var identity = Resolve("bob-hr");
    var response = search.Query(identity, "入职需要什么身份材料");
    AssertEqual("answered", response.Status, "HR 问题未返回答案");
    AssertEqual("doc-hr-001", SingleCitation(response).DocumentId, "未引用 HR 文档");
});

Run("REG-TEN-001 非配置 Tenant 默认拒绝", () =>
{
    var foreignIdentity = new PocIdentity("foreign-user", "unconfigured-tenant", ["employees", "finance"]);
    var response = search.Query(foreignIdentity, "预算报销规则");
    AssertEqual("refused", response.Status, "非配置 Tenant 获得答案");
    AssertEqual(0, response.Citations.Count, "非配置 Tenant 获得引用");
});

Run("REG-RAG-001 无证据时拒答", () =>
{
    var response = search.Query(Resolve("alice-finance"), "食堂本周菜单");
    AssertEqual("refused", response.Status, "无证据问题未拒答");
    AssertEqual(PermissionAwareSearchService.RefusalMessage, response.Answer, "拒答文案不一致");
});

Run("REG-CITE-001 引用包含固定版本和位置", () =>
{
    var response = search.Query(Resolve("alice-finance"), "预算规则");
    var citation = SingleCitation(response);
    AssertEqual("3", citation.Version, "引用缺少固定版本");
    AssertEqual("第 4 节 预算报销", citation.Section, "引用缺少位置");
    AssertFalse(string.IsNullOrWhiteSpace(citation.SourcePath), "引用缺少来源路径");
});

Run("REG-SRC-001 批准快照加载来源文件与 ACL", () =>
{
    AssertEqual(3, repository.Documents.Count, "批准快照文档数不正确");
    var financeDocument = repository.Documents.Single(document => document.Id == "doc-finance-001");
    AssertTrue(financeDocument.AllowedGroups.Contains("finance"), "财务文档缺少来源 ACL");
    AssertTrue(financeDocument.Content.Contains("成本中心", StringComparison.Ordinal), "未从来源文件加载内容");
});

Run("REG-SRC-002 来源文件被篡改时拒绝启动", () =>
{
    WithTemporarySnapshot(
        fileContent: "已被篡改的内容",
        relativePath: "fixtures/document.md",
        allowedGroups: ["employees"],
        approvedHash: ComputeSha256("批准内容"),
        manifestPath => ExpectThrows<InvalidDataException>(
            () => DocumentRepository.LoadApprovedSnapshot(manifestPath),
            "篡改内容通过了 SHA-256 校验"));
});

Run("REG-SRC-003 来源路径越界时拒绝启动", () =>
{
    WithTemporarySnapshot(
        fileContent: "批准内容",
        relativePath: "../outside.md",
        allowedGroups: ["employees"],
        approvedHash: ComputeSha256("批准内容"),
        manifestPath => ExpectThrows<InvalidDataException>(
            () => DocumentRepository.LoadApprovedSnapshot(manifestPath),
            "路径穿越未被拒绝"));
});

Run("REG-SRC-004 来源文档缺少 ACL 时拒绝启动", () =>
{
    WithTemporarySnapshot(
        fileContent: "批准内容",
        relativePath: "fixtures/document.md",
        allowedGroups: [],
        approvedHash: ComputeSha256("批准内容"),
        manifestPath => ExpectThrows<InvalidDataException>(
            () => DocumentRepository.LoadApprovedSnapshot(manifestPath),
            "缺失 ACL 的来源文档被接受"));
});

Run("REG-AUTH-002 Production 环境禁止启用测试身份", () =>
{
    ExpectThrows<InvalidOperationException>(
        () => PocApplication.Build(
            ["--GateF:PocIdentityEnabled=true"],
            repository,
            "Production",
            new InMemorySearchTraceSink()),
        "Production 环境启用了 X-Poc-User");
});

Run("REG-TRACE-001 回答 Trace 关联快照与授权引用", () =>
{
    var isolatedSink = new InMemorySearchTraceSink();
    var isolatedSearch = new PermissionAwareSearchService(repository, isolatedSink);
    var response = isolatedSearch.Query(Resolve("alice-finance"), "预算报销规则");
    var trace = isolatedSink.Records.Single();

    AssertEqual(response.TraceId, trace.TraceId, "响应与 Trace 标识不一致");
    AssertEqual(repository.SourceId, trace.SnapshotSourceId, "Trace 缺少快照来源");
    AssertEqual(repository.ManifestSha256, trace.SnapshotManifestSha256, "Trace 缺少清单哈希");
    AssertEqual("doc-finance-001", trace.SelectedDocumentId!, "Trace 未关联已授权引用");
    AssertEqual("answered", trace.Decision, "Trace 决策不正确");
});

Run("REG-TRACE-002 拒答 Trace 不泄漏问题原文或隐藏文档", () =>
{
    const string question = "预算报销规则";
    var isolatedSink = new InMemorySearchTraceSink();
    var isolatedSearch = new PermissionAwareSearchService(repository, isolatedSink);
    var response = isolatedSearch.Query(Resolve("bob-hr"), question);
    var trace = isolatedSink.Records.Single();
    var serializedTrace = JsonSerializer.Serialize(trace);

    AssertEqual("refused", response.Status, "跨部门请求未拒答");
    AssertEqual("refused", trace.Decision, "拒答 Trace 决策不正确");
    AssertTrue(trace.SelectedDocumentId is null, "拒答 Trace 包含文档标识");
    AssertFalse(serializedTrace.Contains(question, StringComparison.Ordinal), "Trace 保存了问题原文");
    AssertFalse(serializedTrace.Contains("doc-finance-001", StringComparison.Ordinal), "Trace 泄漏隐藏文档");
});

Run("REG-TRACE-003 并发写入保持连续哈希链", () =>
{
    WithTemporaryTraceFile(path =>
    {
        var fileSink = new HashChainedJsonLineTraceSink(path);
        Parallel.For(0, 32, index => fileSink.Record(CreateTraceRecord(index)));
        var validation = HashChainedJsonLineTraceSink.Validate(path);
        AssertEqual(32L, validation.EntryCount, "并发 Trace 条目数不正确");
        AssertFalse(
            string.Equals(validation.FinalHash, HashChainedJsonLineTraceSink.GenesisHash, StringComparison.Ordinal),
            "并发 Trace 未形成哈希链");
    });
});

Run("REG-SRC-005 来源文件不是有效 UTF-8 时拒绝启动", () =>
{
    byte[] invalidUtf8 = [0xc3, 0x28];
    WithTemporaryBinarySnapshot(
        fileContent: invalidUtf8,
        relativePath: "fixtures/document.md",
        allowedGroups: ["employees"],
        approvedHash: ComputeSha256Bytes(invalidUtf8),
        manifestPath => ExpectThrows<DecoderFallbackException>(
            () => DocumentRepository.LoadApprovedSnapshot(manifestPath),
            "无效 UTF-8 来源文件被接受"));
});

Run("REG-TRACE-004 Trace 被篡改时校验失败", () =>
{
    WithTemporaryTraceFile(path =>
    {
        var fileSink = new HashChainedJsonLineTraceSink(path);
        fileSink.Record(CreateTraceRecord(1));
        fileSink.Record(CreateTraceRecord(2));
        var content = File.ReadAllText(path);
        File.WriteAllText(
            path,
            content.Replace("authorized_evidence_found", "tampered_evidence", StringComparison.Ordinal),
            new UTF8Encoding(false));

        ExpectThrows<InvalidDataException>(
            () => HashChainedJsonLineTraceSink.Validate(path),
            "被篡改的 Trace 哈希链通过校验");
    });
});

Run("REG-TRACE-005 Trace 写入失败时检索失败关闭", () =>
{
    var failingSearch = new PermissionAwareSearchService(repository, new FailingSearchTraceSink());
    ExpectThrows<IOException>(
        () => failingSearch.Query(Resolve("alice-finance"), "预算规则"),
        "Trace 写入失败后仍返回检索结果");
});

await RunAsync("REG-API-001 HTTP 契约执行身份、ACL 与输入边界", async () =>
{
    var apiTraceSink = new InMemorySearchTraceSink();
    await using var application = PocApplication.Build(
        ["--GateF:PocIdentityEnabled=true"],
        repository,
        "Development",
        apiTraceSink);
    application.Urls.Add("http://127.0.0.1:0");
    await application.StartAsync();

    var addresses = application.Services
        .GetRequiredService<IServer>()
        .Features
        .Get<IServerAddressesFeature>()?
        .Addresses;
    var address = addresses?.SingleOrDefault()
        ?? throw new InvalidOperationException("测试 API 未获得监听地址");
    using var client = new HttpClient { BaseAddress = new Uri(address) };

    using var financeRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/query")
    {
        Content = JsonContent.Create(new QueryRequest("预算报销规则是什么"))
    };
    financeRequest.Headers.Add("X-Poc-User", "alice-finance");
    using var financeResponse = await client.SendAsync(financeRequest);
    var financePayload = await financeResponse.Content.ReadFromJsonAsync<QueryResponse>();
    AssertEqual(HttpStatusCode.OK, financeResponse.StatusCode, "财务 API 请求失败");
    AssertEqual("doc-finance-001", SingleCitation(financePayload!).DocumentId, "API 未返回财务引用");

    using var hrRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/query")
    {
        Content = JsonContent.Create(new QueryRequest("预算报销规则是什么"))
    };
    hrRequest.Headers.Add("X-Poc-User", "bob-hr");
    using var hrResponse = await client.SendAsync(hrRequest);
    var hrPayload = await hrResponse.Content.ReadFromJsonAsync<QueryResponse>();
    AssertEqual(HttpStatusCode.OK, hrResponse.StatusCode, "HR API 请求失败");
    AssertEqual("refused", hrPayload!.Status, "HTTP 层泄漏财务答案");
    AssertEqual(0, hrPayload.Citations.Count, "HTTP 层泄漏财务引用");

    using var missingIdentityResponse = await client.PostAsJsonAsync(
        "/api/v1/query",
        new QueryRequest("预算报销规则是什么"));
    AssertEqual(HttpStatusCode.Unauthorized, missingIdentityResponse.StatusCode, "缺失身份未返回 401");

    using var tenantInjection = new HttpRequestMessage(HttpMethod.Post, "/api/v1/query")
    {
        Content = new StringContent(
            "{\"question\":\"预算规则\",\"tenantId\":\"attacker-selected\"}",
            System.Text.Encoding.UTF8,
            "application/json")
    };
    tenantInjection.Headers.Add("X-Poc-User", "alice-finance");
    using var tenantInjectionResponse = await client.SendAsync(tenantInjection);
    AssertEqual(HttpStatusCode.BadRequest, tenantInjectionResponse.StatusCode, "客户端 Tenant 注入未被拒绝");

    await using var productionApplication = PocApplication.Build(
        [],
        repository,
        "Production",
        new InMemorySearchTraceSink());
    productionApplication.Urls.Add("http://127.0.0.1:0");
    await productionApplication.StartAsync();
    var productionAddresses = productionApplication.Services
        .GetRequiredService<IServer>()
        .Features
        .Get<IServerAddressesFeature>()?
        .Addresses;
    var productionAddress = productionAddresses?.SingleOrDefault()
        ?? throw new InvalidOperationException("Production 测试 API 未获得监听地址");
    using var productionClient = new HttpClient { BaseAddress = new Uri(productionAddress) };
    using var productionRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/query")
    {
        Content = JsonContent.Create(new QueryRequest("预算报销规则是什么"))
    };
    productionRequest.Headers.Add("X-Poc-User", "alice-finance");
    using var productionResponse = await productionClient.SendAsync(productionRequest);
    AssertEqual(
        HttpStatusCode.Unauthorized,
        productionResponse.StatusCode,
        "Production 默认配置接受了 X-Poc-User");
});

if (failures.Count > 0)
{
    Console.Error.WriteLine($"REGRESSION_TESTS=FAILED count={failures.Count}");
    foreach (var failure in failures)
    {
        Console.Error.WriteLine(failure);
    }

    return 1;
}

Console.WriteLine("REGRESSION_TESTS=PASS count=19");
return 0;

void Run(string name, Action test)
{
    try
    {
        test();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failures.Add($"FAIL {name}: {exception.Message}");
    }
}

async Task RunAsync(string name, Func<Task> test)
{
    try
    {
        await test();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failures.Add($"FAIL {name}: {exception.Message}");
    }
}

PocIdentity Resolve(string user)
{
    if (!identities.TryResolve(user, out var identity))
    {
        throw new InvalidOperationException($"测试身份 {user} 不存在");
    }

    return identity;
}

static Citation SingleCitation(QueryResponse response)
{
    if (response.Citations.Count != 1)
    {
        throw new InvalidOperationException($"期望一个引用，实际为 {response.Citations.Count}");
    }

    return response.Citations[0];
}

static void AssertEqual<T>(T expected, T actual, string message)
    where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{message}：expected={expected}, actual={actual}");
    }
}

static void AssertFalse(bool actual, string message)
{
    if (actual)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertTrue(bool actual, string message)
{
    if (!actual)
    {
        throw new InvalidOperationException(message);
    }
}

static void ExpectThrows<TException>(Action action, string message)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException(message);
}

static void WithTemporarySnapshot(
    string fileContent,
    string relativePath,
    string[] allowedGroups,
    string approvedHash,
    Action<string> test)
{
    WithTemporaryBinarySnapshot(
        Encoding.UTF8.GetBytes(fileContent),
        relativePath,
        allowedGroups,
        approvedHash,
        test);
}

static void WithTemporaryBinarySnapshot(
    byte[] fileContent,
    string relativePath,
    string[] allowedGroups,
    string approvedHash,
    Action<string> test)
{
    var regressionRoot = Path.Combine(
        Path.GetTempPath(),
        "enterprise-ai-poc-regression",
        Guid.NewGuid().ToString("N"));
    var fixtureDirectory = Path.Combine(regressionRoot, "fixtures");
    Directory.CreateDirectory(fixtureDirectory);
    File.WriteAllBytes(Path.Combine(fixtureDirectory, "document.md"), fileContent);

    var manifest = new ApprovedSourceManifest(
        "regression-snapshot",
        PocIdentityDirectory.EnterpriseTenantId,
        "regression-owner",
        "synthetic",
        ["gate-f"],
        [new ApprovedSourceDocument(
            "regression-document",
            relativePath,
            "1",
            "回归文档",
            "测试节",
            allowedGroups,
            ["批准内容"],
            approvedHash)]);
    var manifestPath = Path.Combine(regressionRoot, "approved-source.json");
    File.WriteAllText(
        manifestPath,
        JsonSerializer.Serialize(manifest),
        new UTF8Encoding(false));

    try
    {
        test(manifestPath);
    }
    finally
    {
        var expectedParent = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "enterprise-ai-poc-regression")) + Path.DirectorySeparatorChar;
        var resolvedRoot = Path.GetFullPath(regressionRoot);
        if (resolvedRoot.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase))
        {
            Directory.Delete(resolvedRoot, recursive: true);
        }
    }
}

static string ComputeSha256(string content)
{
    return ComputeSha256Bytes(Encoding.UTF8.GetBytes(content));
}

static string ComputeSha256Bytes(byte[] content)
{
    return Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
}

static void WithTemporaryTraceFile(Action<string> test)
{
    var regressionRoot = Path.Combine(
        Path.GetTempPath(),
        "enterprise-ai-trace-regression",
        Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(regressionRoot);
    var tracePath = Path.Combine(regressionRoot, "search-traces.jsonl");

    try
    {
        test(tracePath);
    }
    finally
    {
        var expectedParent = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "enterprise-ai-trace-regression")) + Path.DirectorySeparatorChar;
        var resolvedRoot = Path.GetFullPath(regressionRoot);
        if (resolvedRoot.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase))
        {
            Directory.Delete(resolvedRoot, recursive: true);
        }
    }
}

static SearchTraceRecord CreateTraceRecord(int index)
{
    return new SearchTraceRecord(
        PermissionAwareSearchService.TraceSchemaVersion,
        index.ToString("x32", CultureInfo.InvariantCulture),
        DateTimeOffset.UnixEpoch.AddSeconds(index),
        "regression-user",
        PocIdentityDirectory.EnterpriseTenantId,
        ["employees"],
        ComputeSha256($"question-{index}"),
        "regression-snapshot",
        new string('a', 64),
        PermissionAwareSearchService.PolicyVersion,
        "answered",
        "authorized_evidence_found",
        "regression-document",
        1);
}

file sealed class FailingSearchTraceSink : ISearchTraceSink
{
    public void Record(SearchTraceRecord record)
    {
        throw new IOException("模拟 Trace 写入失败。");
    }
}

using EnterpriseAI.Poc;
using EnterpriseAI.Poc.Evaluation;
using Microsoft.AspNetCore.Builder;
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
        manifestPath => ExpectThrows<InvalidDataException>(
            () => DocumentRepository.LoadApprovedSnapshot(manifestPath),
            "无效 UTF-8 来源文件被接受"));
});

Run("REG-SRC-006 绝对路径来源被拒绝", () =>
{
    var absolute = Path.Combine(Path.GetTempPath(), "outside-approved.md");
    File.WriteAllText(absolute, "批准内容", new UTF8Encoding(false));
    try
    {
        WithTemporarySnapshot(
            fileContent: "批准内容",
            relativePath: absolute,
            allowedGroups: ["employees"],
            approvedHash: ComputeSha256("批准内容"),
            manifestPath => ExpectThrows<InvalidDataException>(
                () => DocumentRepository.LoadApprovedSnapshot(manifestPath),
                "绝对路径来源被接受"));
    }
    finally
    {
        if (File.Exists(absolute))
        {
            File.Delete(absolute);
        }
    }
});

Run("REG-SRC-007 未知 JSON 字段被拒绝", () =>
{
    WithTemporaryRawManifest(
        """
        {
          "sourceId": "regression-snapshot",
          "tenantId": "enterprise-internal",
          "owner": "regression-owner",
          "classification": "synthetic",
          "approvedFor": ["gate-f"],
          "unexpectedField": true,
          "documents": []
        }
        """,
        manifestPath => ExpectThrows<InvalidDataException>(
            () => DocumentRepository.LoadApprovedSnapshot(manifestPath),
            "未知 JSON 字段被接受"));
});

Run("REG-SRC-008 空 Owner 被拒绝", () =>
{
    WithTemporarySnapshot(
        fileContent: "批准内容",
        relativePath: "fixtures/document.md",
        allowedGroups: ["employees"],
        approvedHash: ComputeSha256("批准内容"),
        manifestPath => ExpectThrows<InvalidDataException>(
            () => DocumentRepository.LoadApprovedSnapshot(manifestPath),
            "空 Owner 被接受"),
        owner: "  ");
});

Run("REG-SRC-009 空分类被拒绝", () =>
{
    WithTemporarySnapshot(
        fileContent: "批准内容",
        relativePath: "fixtures/document.md",
        allowedGroups: ["employees"],
        approvedHash: ComputeSha256("批准内容"),
        manifestPath => ExpectThrows<InvalidDataException>(
            () => DocumentRepository.LoadApprovedSnapshot(manifestPath),
            "空分类被接受"),
        classification: " ");
});

Run("REG-SRC-010 空版本被拒绝", () =>
{
    WithTemporarySnapshot(
        fileContent: "批准内容",
        relativePath: "fixtures/document.md",
        allowedGroups: ["employees"],
        approvedHash: ComputeSha256("批准内容"),
        manifestPath => ExpectThrows<InvalidDataException>(
            () => DocumentRepository.LoadApprovedSnapshot(manifestPath),
            "空版本被接受"),
        version: " ");
});

Run("REG-SRC-011 重复 ACL 被拒绝", () =>
{
    WithTemporarySnapshot(
        fileContent: "批准内容",
        relativePath: "fixtures/document.md",
        allowedGroups: ["employees", "Employees"],
        approvedHash: ComputeSha256("批准内容"),
        manifestPath => ExpectThrows<InvalidDataException>(
            () => DocumentRepository.LoadApprovedSnapshot(manifestPath),
            "重复 ACL 被接受"));
});

Run("REG-SRC-012 路径规范化冲突被拒绝", () =>
{
    WithTemporaryMultiPathSnapshot(
        fileContent: "批准内容",
        relativePaths: ["fixtures/document.md", "fixtures/./document.md"],
        allowedGroups: ["employees"],
        approvedHash: ComputeSha256("批准内容"),
        manifestPath => ExpectThrows<InvalidDataException>(
            () => DocumentRepository.LoadApprovedSnapshot(manifestPath),
            "路径规范化冲突被接受"));
});

Run("REG-SRC-013 超大文件被拒绝", () =>
{
    var oversized = new byte[DocumentRepository.MaxSourceFileBytes + 1];
    Array.Fill(oversized, (byte)'A');
    WithTemporaryBinarySnapshot(
        fileContent: oversized,
        relativePath: "fixtures/document.md",
        allowedGroups: ["employees"],
        approvedHash: ComputeSha256Bytes(oversized),
        manifestPath => ExpectThrows<InvalidDataException>(
            () => DocumentRepository.LoadApprovedSnapshot(manifestPath),
            "超大文件被接受"));
});

Run("REG-SRC-014 哈希格式错误被拒绝", () =>
{
    WithTemporarySnapshot(
        fileContent: "批准内容",
        relativePath: "fixtures/document.md",
        allowedGroups: ["employees"],
        approvedHash: "not-a-valid-sha256-hash-value!!!!!!!!!!!!!!",
        manifestPath => ExpectThrows<InvalidDataException>(
            () => DocumentRepository.LoadApprovedSnapshot(manifestPath),
            "非法哈希格式被接受"));
});

Run("REG-SRC-015 异常信息不泄漏正文或敏感路径", () =>
{
    WithTemporarySnapshot(
        fileContent: "SECRET_BODY_CONTENT_SHOULD_NOT_LEAK",
        relativePath: "fixtures/document.md",
        allowedGroups: ["employees"],
        approvedHash: ComputeSha256("不同哈希触发失败"),
        manifestPath =>
        {
            try
            {
                DocumentRepository.LoadApprovedSnapshot(manifestPath);
                throw new InvalidOperationException("哈希不一致未被拒绝");
            }
            catch (InvalidDataException exception)
            {
                AssertFalse(
                    exception.Message.Contains("SECRET_BODY_CONTENT_SHOULD_NOT_LEAK", StringComparison.Ordinal),
                    "异常消息泄漏了文档正文");
                AssertFalse(
                    exception.ToString().Contains("SECRET_BODY_CONTENT_SHOULD_NOT_LEAK", StringComparison.Ordinal),
                    "异常详情泄漏了文档正文");
                AssertFalse(
                    exception.Message.Contains(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase),
                    "异常消息泄漏了临时目录绝对路径");
            }
        });
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

Run("REG-TRACE-006 Trace 结构白名单与隐私字段", () =>
{
    WithTemporaryTraceFile(path =>
    {
        const string question = "预算报销规则隐私探针-UNIQUE";
        var sink = new HashChainedJsonLineTraceSink(path);
        var search = new PermissionAwareSearchService(repository, sink);
        search.Query(Resolve("alice-finance"), question);
        search.Query(Resolve("bob-hr"), question);

        var validation = HashChainedJsonLineTraceSink.Validate(path);
        AssertEqual(2L, validation.EntryCount, "白名单场景 Trace 条目数不正确");

        var raw = File.ReadAllText(path);
        AssertFalse(raw.Contains(question, StringComparison.Ordinal), "Trace 保存了问题原文");
        AssertFalse(raw.Contains("成本中心", StringComparison.Ordinal), "Trace 保存了文档正文");
        AssertFalse(raw.Contains("身份材料", StringComparison.Ordinal), "Trace 保存了隐藏文档正文");
        AssertFalse(raw.Contains("\"answer\"", StringComparison.OrdinalIgnoreCase), "Trace 包含答案字段");
        AssertFalse(raw.Contains("\"question\"", StringComparison.OrdinalIgnoreCase), "Trace 包含问题原文字段");

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };
        foreach (var line in File.ReadLines(path))
        {
            var envelope = JsonSerializer.Deserialize<SearchTraceEnvelope>(line, jsonOptions)
                ?? throw new InvalidOperationException("无法反序列化 Trace");
            HashChainedJsonLineTraceSink.ValidateRecordContract(envelope.Record);
            AssertEqual(repository.SourceId, envelope.Record.SnapshotSourceId, "缺少快照锚点");
            AssertEqual(repository.ManifestSha256, envelope.Record.SnapshotManifestSha256, "缺少清单哈希锚点");
            AssertEqual(PermissionAwareSearchService.PolicyVersion, envelope.Record.PolicyVersion, "缺少策略锚点");
        }

        var refusedLine = File.ReadLines(path).ElementAt(1);
        AssertFalse(refusedLine.Contains("doc-finance-001", StringComparison.Ordinal), "拒答 Trace 泄漏隐藏文档元数据");
    });
});

Run("REG-TRACE-007 Trace 截断篡改检测", () =>
{
    WithTemporaryTraceFile(path =>
    {
        var sink = new HashChainedJsonLineTraceSink(path);
        sink.Record(CreateTraceRecord(1));
        sink.Record(CreateTraceRecord(2));
        var lines = File.ReadAllLines(path);
        var half = Math.Max(12, lines[1].Length / 2);
        File.WriteAllText(
            path,
            lines[0] + Environment.NewLine + lines[1][..half],
            new UTF8Encoding(false));
        ExpectThrows<Exception>(
            () => HashChainedJsonLineTraceSink.Validate(path),
            "截断 Trace 通过校验");
    });
});

Run("REG-TRACE-008 Trace 重排篡改检测", () =>
{
    WithTemporaryTraceFile(path =>
    {
        var sink = new HashChainedJsonLineTraceSink(path);
        sink.Record(CreateTraceRecord(1));
        sink.Record(CreateTraceRecord(2));
        var lines = File.ReadAllLines(path);
        File.WriteAllLines(path, [lines[1], lines[0]], new UTF8Encoding(false));
        ExpectThrows<InvalidDataException>(
            () => HashChainedJsonLineTraceSink.Validate(path),
            "重排 Trace 通过校验");
    });
});

Run("REG-TRACE-009 Trace 重复记录篡改检测", () =>
{
    WithTemporaryTraceFile(path =>
    {
        var sink = new HashChainedJsonLineTraceSink(path);
        sink.Record(CreateTraceRecord(1));
        sink.Record(CreateTraceRecord(2));
        var lines = File.ReadAllLines(path);
        File.WriteAllLines(path, [lines[0], lines[1], lines[1]], new UTF8Encoding(false));
        ExpectThrows<InvalidDataException>(
            () => HashChainedJsonLineTraceSink.Validate(path),
            "重复 Trace 记录通过校验");
    });
});

Run("REG-TRACE-010 Trace 删除中间记录篡改检测", () =>
{
    WithTemporaryTraceFile(path =>
    {
        var sink = new HashChainedJsonLineTraceSink(path);
        sink.Record(CreateTraceRecord(1));
        sink.Record(CreateTraceRecord(2));
        sink.Record(CreateTraceRecord(3));
        var lines = File.ReadAllLines(path);
        File.WriteAllLines(path, [lines[0], lines[2]], new UTF8Encoding(false));
        ExpectThrows<InvalidDataException>(
            () => HashChainedJsonLineTraceSink.Validate(path),
            "删除中间 Trace 记录后通过校验");
    });
});

Run("REG-TRACE-011 Trace 追加伪造记录检测", () =>
{
    WithTemporaryTraceFile(path =>
    {
        var sink = new HashChainedJsonLineTraceSink(path);
        sink.Record(CreateTraceRecord(1));
        var forged = """
            {"sequence":2,"previousHash":"deadbeef","entryHash":"cafebabe","record":{"schemaVersion":"1.0","traceId":"forged","occurredAtUtc":"1970-01-01T00:00:00+00:00","principalId":"x","tenantId":"enterprise-internal","groups":[],"questionSha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","snapshotSourceId":"x","snapshotManifestSha256":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","policyVersion":"gate-f-acl-v1","decision":"answered","reasonCode":"forged","selectedDocumentId":null,"citationCount":0}}
            """;
        File.AppendAllText(path, forged.Trim() + Environment.NewLine, new UTF8Encoding(false));
        ExpectThrows<InvalidDataException>(
            () => HashChainedJsonLineTraceSink.Validate(path),
            "追加伪造 Trace 记录通过校验");
    });
});

await RunAsync("REG-API-001 HTTP 契约执行身份、ACL 与 Tenant 注入边界", async () =>
{
    var apiTraceSink = new InMemorySearchTraceSink();
    await using var host = await StartApiHostAsync(repository, "Development", apiTraceSink, enablePocIdentity: true);
    using var client = host.Client;

    using var financeRequest = CreateQueryRequest("alice-finance", "预算报销规则是什么");
    using var financeResponse = await client.SendAsync(financeRequest);
    var financePayload = await financeResponse.Content.ReadFromJsonAsync<QueryResponse>();
    AssertEqual(HttpStatusCode.OK, financeResponse.StatusCode, "财务 API 请求失败");
    AssertEqual("doc-finance-001", SingleCitation(financePayload!).DocumentId, "API 未返回财务引用");

    using var hrRequest = CreateQueryRequest("bob-hr", "预算报销规则是什么");
    using var hrResponse = await client.SendAsync(hrRequest);
    var hrPayload = await hrResponse.Content.ReadFromJsonAsync<QueryResponse>();
    AssertEqual(HttpStatusCode.OK, hrResponse.StatusCode, "HR API 请求失败");
    AssertEqual("refused", hrPayload!.Status, "HTTP 层泄漏财务答案");
    AssertEqual(0, hrPayload.Citations.Count, "HTTP 层泄漏财务引用");
    AssertNoAuthorizedLeak(await hrResponse.Content.ReadAsStringAsync());

    using var tenantInjection = new HttpRequestMessage(HttpMethod.Post, "/api/v1/query")
    {
        Content = new StringContent(
            "{\"question\":\"预算规则\",\"tenantId\":\"attacker-selected\"}",
            Encoding.UTF8,
            "application/json")
    };
    tenantInjection.Headers.Add("X-Poc-User", "alice-finance");
    using var tenantInjectionResponse = await client.SendAsync(tenantInjection);
    AssertEqual(HttpStatusCode.BadRequest, tenantInjectionResponse.StatusCode, "客户端 Tenant 注入未被拒绝");

    await using var productionHost = await StartApiHostAsync(
        repository,
        "Production",
        new InMemorySearchTraceSink(),
        enablePocIdentity: false);
    using var productionRequest = CreateQueryRequest("alice-finance", "预算报销规则是什么");
    using var productionResponse = await productionHost.Client.SendAsync(productionRequest);
    AssertEqual(
        HttpStatusCode.Unauthorized,
        productionResponse.StatusCode,
        "Production 默认配置接受了 X-Poc-User");
});

await RunAsync("REG-API-002 空白问题返回 4xx 且不落 Trace 原文", async () =>
{
    var sink = new InMemorySearchTraceSink();
    await using var host = await StartApiHostAsync(repository, "Development", sink, enablePocIdentity: true);
    using var request = CreateQueryRequest("alice-finance", "   ");
    using var response = await host.Client.SendAsync(request);
    AssertTrue((int)response.StatusCode is >= 400 and < 500, "空白问题未返回 4xx");
    AssertNoAuthorizedLeak(await response.Content.ReadAsStringAsync());
    AssertFalse(
        sink.Records.Any(record => JsonSerializer.Serialize(record).Contains("   ", StringComparison.Ordinal)),
        "空白问题不应以原文落入 Trace");
    AssertEqual(0, sink.Records.Count, "空白问题不应产生检索 Trace");
});

await RunAsync("REG-API-003 超长问题返回 4xx 且不落 Trace 原文", async () =>
{
    var sink = new InMemorySearchTraceSink();
    await using var host = await StartApiHostAsync(repository, "Development", sink, enablePocIdentity: true);
    var longQuestion = new string('问', 501);
    using var request = CreateQueryRequest("alice-finance", longQuestion);
    using var response = await host.Client.SendAsync(request);
    AssertTrue((int)response.StatusCode is >= 400 and < 500, "超长问题未返回 4xx");
    var body = await response.Content.ReadAsStringAsync();
    AssertNoAuthorizedLeak(body);
    AssertFalse(body.Contains(longQuestion, StringComparison.Ordinal), "错误响应回显了超长问题原文");
    AssertEqual(0, sink.Records.Count, "超长问题不应产生检索 Trace");
});

await RunAsync("REG-API-004 空测试身份返回 401", async () =>
{
    await using var host = await StartApiHostAsync(
        repository,
        "Development",
        new InMemorySearchTraceSink(),
        enablePocIdentity: true);
    using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/query")
    {
        Content = JsonContent.Create(new QueryRequest("预算报销规则是什么"))
    };
    request.Headers.Add("X-Poc-User", " ");
    using var response = await host.Client.SendAsync(request);
    AssertEqual(HttpStatusCode.Unauthorized, response.StatusCode, "空测试身份未返回 401");
    AssertNoAuthorizedLeak(await response.Content.ReadAsStringAsync());
});

await RunAsync("REG-API-005 未知测试身份返回 401", async () =>
{
    await using var host = await StartApiHostAsync(
        repository,
        "Development",
        new InMemorySearchTraceSink(),
        enablePocIdentity: true);
    using var request = CreateQueryRequest("not-a-known-user", "预算报销规则是什么");
    using var response = await host.Client.SendAsync(request);
    AssertEqual(HttpStatusCode.Unauthorized, response.StatusCode, "未知测试身份未返回 401");
    AssertNoAuthorizedLeak(await response.Content.ReadAsStringAsync());
});

await RunAsync("REG-API-006 错误 Content-Type 返回 4xx", async () =>
{
    await using var host = await StartApiHostAsync(
        repository,
        "Development",
        new InMemorySearchTraceSink(),
        enablePocIdentity: true);
    using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/query")
    {
        Content = new StringContent(
            "{\"question\":\"预算报销规则是什么\"}",
            Encoding.UTF8,
            "text/plain")
    };
    request.Headers.Add("X-Poc-User", "alice-finance");
    using var response = await host.Client.SendAsync(request);
    AssertTrue((int)response.StatusCode is >= 400 and < 500, "错误 Content-Type 未返回 4xx");
    AssertNoAuthorizedLeak(await response.Content.ReadAsStringAsync());
});

await RunAsync("REG-API-007 畸形 JSON 返回 4xx", async () =>
{
    await using var host = await StartApiHostAsync(
        repository,
        "Development",
        new InMemorySearchTraceSink(),
        enablePocIdentity: true);
    using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/query")
    {
        Content = new StringContent("{\"question\":", Encoding.UTF8, "application/json")
    };
    request.Headers.Add("X-Poc-User", "alice-finance");
    using var response = await host.Client.SendAsync(request);
    AssertTrue((int)response.StatusCode is >= 400 and < 500, "畸形 JSON 未返回 4xx");
    AssertNoAuthorizedLeak(await response.Content.ReadAsStringAsync());
});

await RunAsync("REG-API-008 未知字段返回 4xx", async () =>
{
    await using var host = await StartApiHostAsync(
        repository,
        "Development",
        new InMemorySearchTraceSink(),
        enablePocIdentity: true);
    using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/query")
    {
        Content = new StringContent(
            "{\"question\":\"预算规则\",\"extra\":\"nope\"}",
            Encoding.UTF8,
            "application/json")
    };
    request.Headers.Add("X-Poc-User", "alice-finance");
    using var response = await host.Client.SendAsync(request);
    AssertTrue((int)response.StatusCode is >= 400 and < 500, "未知字段未返回 4xx");
    AssertNoAuthorizedLeak(await response.Content.ReadAsStringAsync());
});

await RunAsync("REG-API-009 错误字段类型返回 4xx", async () =>
{
    await using var host = await StartApiHostAsync(
        repository,
        "Development",
        new InMemorySearchTraceSink(),
        enablePocIdentity: true);
    using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/query")
    {
        Content = new StringContent(
            "{\"question\":12345}",
            Encoding.UTF8,
            "application/json")
    };
    request.Headers.Add("X-Poc-User", "alice-finance");
    using var response = await host.Client.SendAsync(request);
    AssertTrue((int)response.StatusCode is >= 400 and < 500, "错误字段类型未返回 4xx");
    AssertNoAuthorizedLeak(await response.Content.ReadAsStringAsync());
});

await RunAsync("REG-API-010 并发请求保持 ACL 与 Trace 无问题原文", async () =>
{
    var sink = new InMemorySearchTraceSink();
    await using var host = await StartApiHostAsync(repository, "Development", sink, enablePocIdentity: true);
    const string secretQuestion = "并发预算报销规则探测-UNIQUE-MARKER";
    var tasks = Enumerable.Range(0, 16).Select(async index =>
    {
        var user = index % 2 == 0 ? "alice-finance" : "bob-hr";
        using var request = CreateQueryRequest(user, secretQuestion);
        using var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        AssertEqual(HttpStatusCode.OK, response.StatusCode, "并发请求未返回 200");
        if (user == "bob-hr")
        {
            AssertNoAuthorizedLeak(body);
            AssertFalse(body.Contains("doc-finance-001", StringComparison.Ordinal), "并发拒答泄漏财务文档 ID");
        }
        else
        {
            AssertTrue(body.Contains("doc-finance-001", StringComparison.Ordinal), "并发财务请求未返回引用");
        }
    });
    await Task.WhenAll(tasks);
    AssertEqual(16, sink.Records.Count, "并发 Trace 条目数不正确");
    foreach (var record in sink.Records)
    {
        var serialized = JsonSerializer.Serialize(record);
        AssertFalse(serialized.Contains(secretQuestion, StringComparison.Ordinal), "Trace 保存了问题原文");
    }
});

var goldenDatasetPath = ResolveGoldenDatasetPath();
foreach (var (id, name, test) in GoldenDatasetContractRegression.BuildCases(repository, goldenDatasetPath))
{
    Run($"{id} {name}", test);
}

if (failures.Count > 0)
{
    Console.Error.WriteLine($"REGRESSION_TESTS=FAILED count={failures.Count}");
    foreach (var failure in failures)
    {
        Console.Error.WriteLine(failure);
    }

    return 1;
}

const int ExpectedRegressionCount = 57;
Console.WriteLine($"REGRESSION_TESTS=PASS count={ExpectedRegressionCount}");
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
    Action<string> test,
    string owner = "regression-owner",
    string classification = "synthetic",
    string version = "1")
{
    WithTemporaryBinarySnapshot(
        Encoding.UTF8.GetBytes(fileContent),
        relativePath,
        allowedGroups,
        approvedHash,
        test,
        owner,
        classification,
        version);
}

static void WithTemporaryBinarySnapshot(
    byte[] fileContent,
    string relativePath,
    string[] allowedGroups,
    string approvedHash,
    Action<string> test,
    string owner = "regression-owner",
    string classification = "synthetic",
    string version = "1")
{
    WithTemporaryMultiPathBinarySnapshot(
        fileContent,
        [relativePath],
        allowedGroups,
        approvedHash,
        test,
        owner,
        classification,
        version);
}

static void WithTemporaryMultiPathSnapshot(
    string fileContent,
    string[] relativePaths,
    string[] allowedGroups,
    string approvedHash,
    Action<string> test)
{
    WithTemporaryMultiPathBinarySnapshot(
        Encoding.UTF8.GetBytes(fileContent),
        relativePaths,
        allowedGroups,
        approvedHash,
        test);
}

static void WithTemporaryMultiPathBinarySnapshot(
    byte[] fileContent,
    string[] relativePaths,
    string[] allowedGroups,
    string approvedHash,
    Action<string> test,
    string owner = "regression-owner",
    string classification = "synthetic",
    string version = "1")
{
    var regressionRoot = Path.Combine(
        Path.GetTempPath(),
        "enterprise-ai-poc-regression",
        Guid.NewGuid().ToString("N"));
    var fixtureDirectory = Path.Combine(regressionRoot, "fixtures");
    Directory.CreateDirectory(fixtureDirectory);
    File.WriteAllBytes(Path.Combine(fixtureDirectory, "document.md"), fileContent);

    var documents = relativePaths
        .Select((path, index) => new ApprovedSourceDocument(
            $"regression-document-{index + 1}",
            path,
            version,
            "回归文档",
            "测试节",
            allowedGroups,
            ["批准内容"],
            approvedHash))
        .ToArray();
    var manifest = new ApprovedSourceManifest(
        "regression-snapshot",
        PocIdentityDirectory.EnterpriseTenantId,
        owner,
        classification,
        ["gate-f"],
        documents);
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

static void WithTemporaryRawManifest(string json, Action<string> test)
{
    var regressionRoot = Path.Combine(
        Path.GetTempPath(),
        "enterprise-ai-poc-regression",
        Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(regressionRoot);
    var manifestPath = Path.Combine(regressionRoot, "approved-source.json");
    File.WriteAllText(manifestPath, json, new UTF8Encoding(false));
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

static HttpRequestMessage CreateQueryRequest(string user, string question)
{
    var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/query")
    {
        Content = JsonContent.Create(new QueryRequest(question))
    };
    request.Headers.Add("X-Poc-User", user);
    return request;
}

static void AssertNoAuthorizedLeak(string body)
{
    AssertFalse(body.Contains("成本中心", StringComparison.Ordinal), "响应泄漏财务正文");
    AssertFalse(body.Contains("预算报销", StringComparison.Ordinal) && body.Contains("doc-finance-001", StringComparison.Ordinal),
        "响应泄漏财务授权内容组合");
}

static async Task<ApiTestHost> StartApiHostAsync(
    DocumentRepository repository,
    string environment,
    ISearchTraceSink traceSink,
    bool enablePocIdentity)
{
    var args = enablePocIdentity
        ? new[] { "--GateF:PocIdentityEnabled=true" }
        : Array.Empty<string>();
    var application = PocApplication.Build(args, repository, environment, traceSink);
    application.Urls.Add("http://127.0.0.1:0");
    await application.StartAsync();
    var addresses = application.Services
        .GetRequiredService<IServer>()
        .Features
        .Get<IServerAddressesFeature>()?
        .Addresses;
    var address = addresses?.SingleOrDefault()
        ?? throw new InvalidOperationException("测试 API 未获得监听地址");
    var client = new HttpClient { BaseAddress = new Uri(address) };
    return new ApiTestHost(application, client);
}

file sealed class ApiTestHost(WebApplication application, HttpClient client) : IAsyncDisposable
{
    public HttpClient Client { get; } = client;

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await application.DisposeAsync();
    }
}

file sealed class FailingSearchTraceSink : ISearchTraceSink
{
    public void Record(SearchTraceRecord record)
    {
        throw new IOException("模拟 Trace 写入失败。");
    }
}

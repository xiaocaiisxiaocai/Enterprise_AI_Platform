using EnterpriseAI.Poc;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;

var dataPath = Path.Combine(AppContext.BaseDirectory, "Data", "documents.json");
var repository = DocumentRepository.Load(dataPath);
var identities = new PocIdentityDirectory();
var search = new PermissionAwareSearchService(repository);
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

await RunAsync("REG-API-001 HTTP 契约执行身份、ACL 与输入边界", async () =>
{
    await using var application = PocApplication.Build([], repository);
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

Console.WriteLine("REGRESSION_TESTS=PASS count=8");
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

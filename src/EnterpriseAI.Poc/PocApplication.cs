using System.Text.Json;
using System.Text.Json.Serialization;

namespace EnterpriseAI.Poc;

public static class PocApplication
{
    private static readonly JsonSerializerOptions QueryJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static WebApplication Build(
        string[] args,
        DocumentRepository? repository = null,
        string? environmentName = null,
        ISearchTraceSink? traceSink = null)
    {
        var options = string.IsNullOrWhiteSpace(environmentName)
            ? new WebApplicationOptions { Args = args }
            : new WebApplicationOptions { Args = args, EnvironmentName = environmentName };

        var builder = WebApplication.CreateBuilder(options);
        var pocIdentityEnabled = bool.TryParse(
            builder.Configuration["GateF:PocIdentityEnabled"],
            out var configuredIdentityEnabled) && configuredIdentityEnabled;
        if (pocIdentityEnabled && !builder.Environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                "X-Poc-User 测试身份只能在 Development 环境启用。");
        }

        // 输入边界错误由处理器显式返回 ErrorResponse，不抛到开发者异常页。
        builder.Services.Configure<RouteHandlerOptions>(routeOptions =>
        {
            routeOptions.ThrowOnBadRequest = false;
        });
        builder.Services.ConfigureHttpJsonOptions(jsonOptions =>
        {
            jsonOptions.SerializerOptions.PropertyNameCaseInsensitive = true;
            jsonOptions.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            jsonOptions.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        });
        builder.Services.AddSingleton<PocIdentityDirectory>();
        builder.Services.AddSingleton(repository ?? DocumentRepository.LoadApprovedSnapshot(
            Path.Combine(builder.Environment.ContentRootPath, "Data", "approved-source.json")));
        builder.Services.AddSingleton(traceSink ?? new HashChainedJsonLineTraceSink(
            Path.Combine(builder.Environment.ContentRootPath, ".gate-f", "search-traces.jsonl")));
        builder.Services.AddSingleton<PermissionAwareSearchService>();

        var application = builder.Build();

        application.MapGet("/healthz", () =>
            TypedResults.Ok(new { status = "healthy", scope = "gate-f-poc" }));

        application.MapPost("/api/v1/query", async Task<IResult> (
            HttpRequest httpRequest,
            PocIdentityDirectory identities,
            PermissionAwareSearchService search) =>
        {
            if (!pocIdentityEnabled ||
                !httpRequest.Headers.TryGetValue("X-Poc-User", out var userHeader) ||
                !identities.TryResolve(userHeader.ToString(), out var identity))
            {
                return TypedResults.Unauthorized();
            }

            if (!httpRequest.HasJsonContentType())
            {
                return TypedResults.BadRequest(new ErrorResponse(
                    "invalid_content_type",
                    "Content-Type 必须为 application/json。"));
            }

            QueryRequest? request;
            try
            {
                request = await JsonSerializer.DeserializeAsync<QueryRequest>(
                    httpRequest.Body,
                    QueryJsonOptions);
            }
            catch (JsonException)
            {
                return TypedResults.BadRequest(new ErrorResponse(
                    "invalid_request",
                    "请求体无效、字段类型错误或包含未知字段。"));
            }

            if (request is null)
            {
                return TypedResults.BadRequest(new ErrorResponse(
                    "invalid_request",
                    "请求体不能为空。"));
            }

            if (string.IsNullOrWhiteSpace(request.Question) || request.Question.Length > 500)
            {
                return TypedResults.BadRequest(new ErrorResponse(
                    "invalid_question",
                    "问题不能为空且长度不能超过 500 个字符。"));
            }

            return TypedResults.Ok(search.Query(identity, request.Question));
        });

        return application;
    }
}

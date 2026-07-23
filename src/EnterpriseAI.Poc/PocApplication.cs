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
        ISearchTraceSink? traceSink = null,
        LocalStateStore? stateStore = null)
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
        var configuredStatePath = builder.Configuration["GateF:LocalState:Path"];
        var localStateStore = stateStore ??
            (!string.IsNullOrWhiteSpace(configuredStatePath)
                ? new LocalStateStore(configuredStatePath)
                : null);
        if (localStateStore is not null)
        {
            builder.Services.AddSingleton(localStateStore);
        }
        builder.Services.AddSingleton(new PocIdentityDirectory(localStateStore));
        var documentRepository = repository ?? DocumentRepository.LoadApprovedSnapshot(
            Path.Combine(builder.Environment.ContentRootPath, "Data", "approved-source.json"),
            localStateStore);
        builder.Services.AddSingleton(documentRepository);
        var ingestionRoot = builder.Configuration["GateF:LocalIngestion:RootPath"];
        if (!string.IsNullOrWhiteSpace(ingestionRoot))
        {
            var allowedGroups = builder.Configuration
                .GetSection("GateF:LocalIngestion:AllowedGroups")
                .GetChildren()
                .Select(child => child.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .ToArray();
            var ingestion = new LocalFileIngestionService(
                documentRepository,
                new LocalFileIngestionOptions(
                    ingestionRoot,
                    builder.Configuration["GateF:LocalIngestion:SourceId"] ?? string.Empty,
                    builder.Configuration["GateF:LocalIngestion:Owner"] ?? string.Empty,
                    builder.Configuration["GateF:LocalIngestion:Classification"] ?? string.Empty,
                    allowedGroups),
                localStateStore,
                new LocalFileIngestionLimits(
                    ReadPositiveInt(builder.Configuration["GateF:LocalIngestion:MaxFiles"], 1_000),
                    ReadNonNegativeInt(builder.Configuration["GateF:LocalIngestion:MaxDirectoryDepth"], 8),
                    ReadPositiveLong(
                        builder.Configuration["GateF:LocalIngestion:MaxBatchBytes"],
                        32L * 1024 * 1024)));
            ingestion.Synchronize();
            builder.Services.AddSingleton(ingestion);
            var intervalSeconds = ReadNonNegativeInt(
                builder.Configuration["GateF:LocalIngestion:IntervalSeconds"],
                0);
            if (intervalSeconds > 0)
            {
                var timeoutSeconds = ReadPositiveInt(
                    builder.Configuration["GateF:LocalIngestion:TimeoutSeconds"],
                    30);
                builder.Services.AddSingleton(new LocalFileIngestionWorkerOptions(
                    TimeSpan.FromSeconds(intervalSeconds),
                    TimeSpan.FromSeconds(timeoutSeconds)));
                builder.Services.AddHostedService<LocalFileIngestionWorker>();
            }
        }
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

    private static int ReadPositiveInt(string? value, int defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }
        return int.TryParse(value, out var parsed) && parsed > 0
            ? parsed
            : throw new InvalidDataException("本地摄取正整数配置无效。");
    }

    private static int ReadNonNegativeInt(string? value, int defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }
        return int.TryParse(value, out var parsed) && parsed >= 0
            ? parsed
            : throw new InvalidDataException("本地摄取非负整数配置无效。");
    }

    private static long ReadPositiveLong(string? value, long defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }
        return long.TryParse(value, out var parsed) && parsed > 0
            ? parsed
            : throw new InvalidDataException("本地摄取字节限制配置无效。");
    }
}

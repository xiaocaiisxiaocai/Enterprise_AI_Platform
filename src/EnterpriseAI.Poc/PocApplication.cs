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
        LocalStateStore? stateStore = null,
        string? webRootPath = null)
    {
        var options = string.IsNullOrWhiteSpace(environmentName)
            ? new WebApplicationOptions { Args = args, WebRootPath = webRootPath }
            : new WebApplicationOptions
            {
                Args = args,
                EnvironmentName = environmentName,
                WebRootPath = webRootPath
            };

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
            jsonOptions.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
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

        if (pocIdentityEnabled && application.Environment.IsDevelopment())
        {
            application.UseDefaultFiles();
            application.UseStaticFiles();
        }

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

        application.MapGet("/api/v1/poc/governance/overview", (
            HttpRequest httpRequest,
            PocIdentityDirectory identities,
            DocumentRepository documents) =>
        {
            var rejection = AuthorizeGovernance(
                httpRequest,
                identities,
                pocIdentityEnabled,
                application.Environment.IsDevelopment());
            if (rejection is not null)
            {
                return rejection;
            }

            return TypedResults.Ok(new GovernanceOverview(
                "local-single-enterprise-poc",
                identities.Revision,
                documents.Revision,
                documents.SourceId,
                documents.ManifestSha256,
                identities.GetGovernanceSnapshot(),
                documents.GetGovernanceSnapshot()));
        });

        application.MapPut("/api/v1/poc/governance/identities/{principalId}/groups",
            async Task<IResult> (
                string principalId,
                HttpRequest httpRequest,
                PocIdentityDirectory identities) =>
            {
                var rejection = AuthorizeGovernance(
                    httpRequest,
                    identities,
                    pocIdentityEnabled,
                    application.Environment.IsDevelopment());
                if (rejection is not null)
                {
                    return rejection;
                }

                var (request, error) = await ReadJsonAsync<ReplaceGroupsRequest>(httpRequest);
                if (error is not null)
                {
                    return error;
                }
                if (request?.Groups is null ||
                    request.Groups.Length == 0 ||
                    request.Groups.Any(string.IsNullOrWhiteSpace))
                {
                    return TypedResults.BadRequest(new ErrorResponse(
                        "invalid_groups",
                        "Groups 必须至少包含一个非空值。"));
                }
                if (string.Equals(
                        principalId,
                        "admin-governance",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return TypedResults.BadRequest(new ErrorResponse(
                        "protected_identity",
                        "治理管理员身份不能通过演示接口修改。"));
                }

                try
                {
                    identities.ReplaceGroups(principalId, request.Groups);
                    return TypedResults.NoContent();
                }
                catch (KeyNotFoundException)
                {
                    return TypedResults.NotFound(new ErrorResponse(
                        "identity_not_found",
                        "本地测试身份不存在。"));
                }
            });

        application.MapPut("/api/v1/poc/governance/documents/{documentId}/acl",
            async Task<IResult> (
                string documentId,
                HttpRequest httpRequest,
                PocIdentityDirectory identities,
                DocumentRepository documents) =>
            {
                var rejection = AuthorizeGovernance(
                    httpRequest,
                    identities,
                    pocIdentityEnabled,
                    application.Environment.IsDevelopment());
                if (rejection is not null)
                {
                    return rejection;
                }

                var (request, error) = await ReadJsonAsync<ReplaceGroupsRequest>(httpRequest);
                if (error is not null)
                {
                    return error;
                }
                try
                {
                    documents.ReplaceAllowedGroups(documentId, request?.Groups ?? []);
                    return TypedResults.NoContent();
                }
                catch (ArgumentException)
                {
                    return TypedResults.BadRequest(new ErrorResponse(
                        "invalid_groups",
                        "文档 ACL 必须至少包含一个非空 Group。"));
                }
                catch (KeyNotFoundException)
                {
                    return TypedResults.NotFound(new ErrorResponse(
                        "document_not_found",
                        "本地知识文档不存在。"));
                }
                catch (InvalidOperationException)
                {
                    return TypedResults.Conflict(new ErrorResponse(
                        "document_deleted",
                        "已删除文档不能修改 ACL。"));
                }
            });

        application.MapPost(
            "/api/v1/poc/governance/documents/{documentId}/lifecycle/{action}",
            (
                string documentId,
                string action,
                HttpRequest httpRequest,
                PocIdentityDirectory identities,
                DocumentRepository documents) =>
            {
                var rejection = AuthorizeGovernance(
                    httpRequest,
                    identities,
                    pocIdentityEnabled,
                    application.Environment.IsDevelopment());
                if (rejection is not null)
                {
                    return rejection;
                }

                try
                {
                    if (string.Equals(action, "publish", StringComparison.OrdinalIgnoreCase))
                    {
                        documents.Publish(documentId);
                    }
                    else if (string.Equals(action, "withdraw", StringComparison.OrdinalIgnoreCase))
                    {
                        documents.Withdraw(documentId);
                    }
                    else
                    {
                        return TypedResults.BadRequest(new ErrorResponse(
                            "invalid_lifecycle_action",
                            "生命周期操作仅支持 publish 或 withdraw。"));
                    }
                    return TypedResults.NoContent();
                }
                catch (KeyNotFoundException)
                {
                    return TypedResults.NotFound(new ErrorResponse(
                        "document_not_found",
                        "本地知识文档不存在。"));
                }
                catch (InvalidOperationException)
                {
                    return TypedResults.Conflict(new ErrorResponse(
                        "document_deleted",
                        "已删除文档不能重新发布或撤回。"));
                }
            });

        return application;
    }

    private static IResult? AuthorizeGovernance(
        HttpRequest request,
        PocIdentityDirectory identities,
        bool pocIdentityEnabled,
        bool isDevelopment)
    {
        if (!pocIdentityEnabled ||
            !isDevelopment ||
            !request.Headers.TryGetValue("X-Poc-User", out var userHeader) ||
            !identities.TryResolve(userHeader.ToString(), out var identity))
        {
            return TypedResults.Unauthorized();
        }

        return identity.Groups.Contains(PocIdentityDirectory.GovernanceAdminGroup)
            ? null
            : TypedResults.StatusCode(StatusCodes.Status403Forbidden);
    }

    private static async Task<(T? Request, IResult? Error)> ReadJsonAsync<T>(
        HttpRequest httpRequest)
    {
        if (!httpRequest.HasJsonContentType())
        {
            return (default, TypedResults.BadRequest(new ErrorResponse(
                "invalid_content_type",
                "Content-Type 必须为 application/json。")));
        }

        try
        {
            var request = await JsonSerializer.DeserializeAsync<T>(
                httpRequest.Body,
                QueryJsonOptions);
            return request is null
                ? (default, TypedResults.BadRequest(new ErrorResponse(
                    "invalid_request",
                    "请求体不能为空。")))
                : (request, null);
        }
        catch (JsonException)
        {
            return (default, TypedResults.BadRequest(new ErrorResponse(
                "invalid_request",
                "请求体无效、字段类型错误或包含未知字段。")));
        }
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

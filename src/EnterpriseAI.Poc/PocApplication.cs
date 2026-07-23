using System.Text.Json.Serialization;

namespace EnterpriseAI.Poc;

public static class PocApplication
{
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

        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
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

        application.MapPost("/api/v1/query", IResult (
            HttpRequest httpRequest,
            QueryRequest request,
            PocIdentityDirectory identities,
            PermissionAwareSearchService search) =>
        {
            if (!pocIdentityEnabled ||
                !httpRequest.Headers.TryGetValue("X-Poc-User", out var userHeader) ||
                !identities.TryResolve(userHeader.ToString(), out var identity))
            {
                return TypedResults.Unauthorized();
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

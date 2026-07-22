using System.Text.Json.Serialization;

namespace EnterpriseAI.Poc;

public static class PocApplication
{
    public static WebApplication Build(string[] args, DocumentRepository? repository = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        });
        builder.Services.AddSingleton<PocIdentityDirectory>();
        builder.Services.AddSingleton(repository ?? DocumentRepository.Load(
            Path.Combine(builder.Environment.ContentRootPath, "Data", "documents.json")));
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
            if (!httpRequest.Headers.TryGetValue("X-Poc-User", out var userHeader) ||
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

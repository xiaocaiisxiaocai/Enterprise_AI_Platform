namespace EnterpriseAI.Poc;

public sealed class PocIdentityDirectory
{
    public const string EnterpriseTenantId = "enterprise-internal";

    private readonly Dictionary<string, PocIdentity> _identities =
        new Dictionary<string, PocIdentity>(StringComparer.OrdinalIgnoreCase)
        {
            ["alice-finance"] = new(
                "alice-finance",
                EnterpriseTenantId,
                ["employees", "finance"]),
            ["bob-hr"] = new(
                "bob-hr",
                EnterpriseTenantId,
                ["employees", "hr"])
        };

    public bool TryResolve(string? pocUser, out PocIdentity identity)
    {
        if (!string.IsNullOrWhiteSpace(pocUser) &&
            _identities.TryGetValue(pocUser.Trim(), out var resolved))
        {
            identity = resolved;
            return true;
        }

        identity = null!;
        return false;
    }
}

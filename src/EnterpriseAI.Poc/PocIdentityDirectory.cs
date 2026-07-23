namespace EnterpriseAI.Poc;

public sealed class PocIdentityDirectory
{
    public const string EnterpriseTenantId = "enterprise-internal";

    private readonly object _sync = new();
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
    private readonly HashSet<string> _disabled = new(StringComparer.OrdinalIgnoreCase);
    private long _revision;

    public long Revision
    {
        get
        {
            lock (_sync)
            {
                return _revision;
            }
        }
    }

    public bool TryResolve(string? pocUser, out PocIdentity identity)
    {
        lock (_sync)
        {
            if (!string.IsNullOrWhiteSpace(pocUser))
            {
                var principalId = pocUser.Trim();
                if (!_disabled.Contains(principalId) &&
                    _identities.TryGetValue(principalId, out var resolved))
                {
                    identity = resolved;
                    return true;
                }
            }

            identity = null!;
            return false;
        }
    }

    public void ReplaceGroups(string principalId, IEnumerable<string> groups)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);
        ArgumentNullException.ThrowIfNull(groups);
        var normalizedGroups = groups
            .Select(group => group?.Trim())
            .Where(group => !string.IsNullOrWhiteSpace(group))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        lock (_sync)
        {
            if (!_identities.TryGetValue(principalId, out var current))
            {
                throw new KeyNotFoundException("本地测试身份不存在。");
            }

            _identities[principalId] = new PocIdentity(
                current.PrincipalId,
                current.TenantId,
                normalizedGroups);
            _revision = checked(_revision + 1);
        }
    }

    public void SetEnabled(string principalId, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);
        lock (_sync)
        {
            if (!_identities.ContainsKey(principalId))
            {
                throw new KeyNotFoundException("本地测试身份不存在。");
            }

            var changed = enabled
                ? _disabled.Remove(principalId)
                : _disabled.Add(principalId);
            if (changed)
            {
                _revision = checked(_revision + 1);
            }
        }
    }
}

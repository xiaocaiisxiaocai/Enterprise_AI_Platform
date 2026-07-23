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
    private readonly LocalStateStore? _stateStore;
    private long _revision;

    public PocIdentityDirectory(LocalStateStore? stateStore = null)
    {
        _stateStore = stateStore;
        if (stateStore is not null)
        {
            Replay(stateStore.ReadEvents());
        }
    }

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

            _stateStore?.Append(new LocalStateEvent(
                "identity_groups_replaced",
                current.PrincipalId,
                Groups: normalizedGroups));
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
                ? _disabled.Contains(principalId)
                : !_disabled.Contains(principalId);
            if (changed)
            {
                _stateStore?.Append(new LocalStateEvent(
                    "identity_enabled_changed",
                    principalId,
                    Enabled: enabled));
                if (enabled)
                {
                    _disabled.Remove(principalId);
                }
                else
                {
                    _disabled.Add(principalId);
                }
                _revision = checked(_revision + 1);
            }
        }
    }

    private void Replay(IEnumerable<LocalStateEnvelope> events)
    {
        foreach (var envelope in events)
        {
            var stateEvent = envelope.Event;
            if (!_identities.TryGetValue(stateEvent.TargetId, out var identity))
            {
                if (stateEvent.EventType.StartsWith("identity_", StringComparison.Ordinal))
                {
                    throw new InvalidDataException("本地身份事件引用未知测试身份。");
                }
                continue;
            }

            switch (stateEvent.EventType)
            {
                case "identity_groups_replaced":
                    var groups = stateEvent.Groups
                        ?? throw new InvalidDataException("身份 Group 事件缺少 Groups。");
                    _identities[stateEvent.TargetId] = new PocIdentity(
                        identity.PrincipalId,
                        identity.TenantId,
                        groups);
                    _revision = checked(_revision + 1);
                    break;
                case "identity_enabled_changed":
                    var enabled = stateEvent.Enabled
                        ?? throw new InvalidDataException("身份启用事件缺少 Enabled。");
                    if (enabled)
                    {
                        _disabled.Remove(stateEvent.TargetId);
                    }
                    else
                    {
                        _disabled.Add(stateEvent.TargetId);
                    }
                    _revision = checked(_revision + 1);
                    break;
            }
        }
    }
}

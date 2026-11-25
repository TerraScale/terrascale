using System.Collections.Frozen;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Rules;

namespace TerraBase.Features.Callers;

public static class Scopes
{
    public const string SqlRead = "sql:read";

    public const string SqlWrite = "sql:write";

    public const string DatabasesRead = "databases:read";

    public const string DatabasesWrite = "databases:write";

    public static IReadOnlyList<string> ApiKey { get; } = [SqlRead, SqlWrite, DatabasesRead, DatabasesWrite];
}

public closed record class Caller;

public sealed record AnonymousCaller : Caller
{
    public static AnonymousCaller Instance { get; } = new();
}

public sealed record PeerCaller : Caller;

public sealed record ServiceCaller(string Name, FrozenSet<string> Grants) : Caller;

public sealed record ApiKeyCaller(ApiKeyRecord Key) : Caller;

public sealed record UserCaller(string UserId, bool ViaOperator) : Caller
{
    public bool Delegated { get; init; }
}

public sealed record ProjectCaller(string UserId, Ulid ProjectId, Ulid? OrganizationId, string? Database, FrozenSet<string> Scopes) : Caller
{
    public bool Delegated { get; init; }
}

public sealed record EndUserCaller(Ulid ProjectId, string? Database, RulesAuth Auth) : Caller;

internal static class CallerContext
{
    extension(HttpContext context)
    {
        public Caller Caller => context.Features.Get<CallerFeature>()?.Caller ?? AnonymousCaller.Instance;

        public void SetCaller(Caller caller) => context.Features.Set(new CallerFeature(caller));
    }

    public static string Actor(Caller caller) => caller switch
    {
        AnonymousCaller => "anonymous",
        PeerCaller => "operator",
        ServiceCaller service => service.Name,
        ApiKeyCaller key => key.Key.Id,
        UserCaller user => user.UserId,
        ProjectCaller project => project.UserId,
        EndUserCaller endUser => endUser.Auth.Uid,
    };

    private sealed record CallerFeature(Caller Caller);
}

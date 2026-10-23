using TerraBase.Features.Sql;
using TerraBase.Tenancy;

namespace TerraBase.Features.Hrana;

/// <summary>One stream: its SQL session.</summary>
internal sealed class HranaStream(ISqlSession session, string owner, TenantContext database)
{
    public ISqlSession Session { get; } = session;

    public string Owner { get; } = owner;

    public TenantContext Database { get; } = database;

    public ValueTask DisposeAsync() => Session.DisposeAsync();
}

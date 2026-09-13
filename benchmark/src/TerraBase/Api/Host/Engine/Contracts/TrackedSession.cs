// <copyright file="TrackedSession.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api;

/// <summary>One sync session this host tracked on the engine: the tenant, the scope, the generation.</summary>
/// <param name="SessionId">The engine-assigned session id.</param>
/// <param name="Tenant">The tenant whose channel opened it.</param>
/// <param name="Collection">The collection scope.</param>
/// <param name="Generation">The engine generation that owns it.</param>
public sealed record TrackedSession(ulong SessionId, string Tenant, string Collection, int Generation);

// <copyright file="StatusDtos.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api;

using TerraBase.Protocol;

/// <summary>The migration state of one collection.</summary>
public sealed record MigrationStatusDto(MigrationStateDto State);

/// <summary>The observable state of one Schema Migration drive.</summary>
/// <param name="Kind">The snake_case state kind: none, in_progress, rolling_back, or complete.</param>
/// <param name="Manifest">The manifest digest of the driving contract.</param>
/// <param name="FromVersion">The from-version.</param>
/// <param name="ToVersion">The to-version.</param>
/// <param name="Documents">The documents scanned so far.</param>
/// <param name="Transformed">The documents transformed so far.</param>
/// <param name="Pages">The pages committed so far.</param>
public sealed record MigrationStateDto(
    string Kind,
    string Manifest,
    ulong FromVersion,
    ulong ToVersion,
    ulong Documents,
    ulong Transformed,
    ulong Pages)
{
    /// <summary>Builds from the protocol state.</summary>
    /// <param name="state">The protocol state.</param>
    /// <returns>The DTO.</returns>
    public static MigrationStateDto Of(MigrationState state) => new(
        state.Kind.Variant.ToSnakeCase(),
        state.Manifest,
        state.FromVersion,
        state.ToVersion,
        state.Documents,
        state.Transformed,
        state.Pages);
}

// <copyright file="HealthDtos.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api;

/// <summary>The composite health answer; never optimistic.</summary>
public sealed record HealthResponse(string Status, EngineHealth Engine);

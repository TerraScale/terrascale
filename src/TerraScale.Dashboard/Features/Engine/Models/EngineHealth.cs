namespace TerraScale.Dashboard.Features.Engine.Models;

/// <summary>TerraBase serving member health facts from GET /api/v1/health.</summary>
public sealed record EngineHealth(string Region, string Version, bool Healthy);

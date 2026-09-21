namespace TerraScale.Auth.Templates;

using System.Net;
using System.Text.RegularExpressions;
using TerraScale.Auth.Api;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Keys;
using TerraScale.Auth.Store;
using TerraScale.DotNet;

/// <summary>Update JWT template request body; null fields are left unchanged.</summary>
internal sealed record UpdateJwtTemplateRequest(
    string? Name,
    Dictionary<string, string>? Claims,
    [property: System.Text.Json.Serialization.JsonPropertyName("lifetime_seconds")] int? LifetimeSeconds);

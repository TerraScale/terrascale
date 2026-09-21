namespace TerraScale.Auth.Templates;

using System.Net;
using System.Text.RegularExpressions;
using TerraScale.Auth.Api;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Keys;
using TerraScale.Auth.Store;
using TerraScale.DotNet;

/// <summary>Create JWT template request body.</summary>
internal sealed record CreateJwtTemplateRequest(
    string? Name,
    Dictionary<string, string>? Claims,
    [property: System.Text.Json.Serialization.JsonPropertyName("lifetime_seconds")] int? LifetimeSeconds);

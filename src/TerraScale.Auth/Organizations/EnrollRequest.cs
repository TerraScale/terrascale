namespace TerraScale.Auth.Organizations;

using System.Net;
using TerraScale.Auth.Api;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Store;
using TerraScale.DotNet;
using TerraScale.Auth.Templates;

/// <summary>Domain enrollment request body.</summary>
internal sealed record EnrollRequest([property: System.Text.Json.Serialization.JsonPropertyName("user_id")] string? UserId);

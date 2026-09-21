namespace TerraScale.Auth.Templates;

using System.Net;
using System.Text.RegularExpressions;
using TerraScale.Auth.Api;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Keys;
using TerraScale.Auth.Store;
using TerraScale.DotNet;

/// <summary>Mint JWT template token request body.</summary>
internal sealed record MintTokenRequest([property: System.Text.Json.Serialization.JsonPropertyName("user_id")] string? UserId);

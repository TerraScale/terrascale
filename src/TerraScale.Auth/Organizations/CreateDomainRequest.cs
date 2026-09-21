namespace TerraScale.Auth.Organizations;

using System.Net;
using TerraScale.Auth.Api;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Store;
using TerraScale.DotNet;
using TerraScale.Auth.Templates;

/// <summary>Create org domain request body.</summary>
internal sealed record CreateDomainRequest(string? Domain);

namespace TerraScale.Auth.Instances;

using TerraScale.Auth.Api;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Keys;
using TerraScale.Auth.Store;
using TerraScale.Auth.Observability;
using TerraScale.DotNet;

/// <summary>Operator request body for instance creation.</summary>
public sealed record CreateInstanceRequest(string Slug, string Name);

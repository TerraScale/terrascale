// <copyright file="CliConfiguration.cs" company="TerraScale">
// TerraBase (SE-13). See LICENSE.md.
// </copyright>

namespace TerraBase.Cli;

/// <summary>
/// The resolved configuration of one CLI invocation. Precedence is
/// documented and fixed: a flag beats its environment variable, and
/// the environment beats the default. There are no credentials on
/// this local surface (PD-2026-08-26-11): the principal and tenant
/// are backend-supplied context labels the host's local gate checks,
/// never secrets, and the CLI never logs them.
/// </summary>
public sealed record CliConfiguration(
    string Mode,
    string Url,
    string SocketPath,
    string Principal,
    string Tenant,
    string OutputFormat)
{
    /// <summary>The default host-mode URL (the host's documented loopback listener).</summary>
    public const string DefaultUrl = "http://127.0.0.1:8420";

    /// <summary>The default engine socket (the required UDS binding, PD-2026-08-26-11 D1).</summary>
    public const string DefaultSocketPath = "/run/terrabase/engine.tbep.sock";

    /// <summary>The default backend-supplied principal of a local operator session.</summary>
    public const string DefaultPrincipal = "terrabase-cli";

    /// <summary>The default backend-supplied tenant of a local operator session.</summary>
    public const string DefaultTenant = "local";
}

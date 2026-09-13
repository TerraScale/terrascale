// <copyright file="CliTransport.cs" company="TerraScale">
// TerraBase (SE-13). See LICENSE.md.
// </copyright>

namespace TerraBase.Cli;

/// <summary>
/// The transport hook of the test family: when set, the host-mode
/// surface builds its client through the factory (the dropped-connection
/// rows wrap the real transport with a faulting stream); production
/// leaves it unset and takes the plain client. The CLI's own code path
/// is identical either way.
/// </summary>
internal static class CliTransport
{
    /// <summary>The client factory, or <c>null</c> for the plain client.</summary>
    public static Func<HttpClient>? HandlerFactory;
}

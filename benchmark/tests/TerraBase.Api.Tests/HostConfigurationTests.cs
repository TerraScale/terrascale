// <copyright file="HostConfigurationTests.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api.Tests;

/// <summary>Startup configuration safety rows for the host listener.</summary>
public sealed class HostConfigurationTests
{
    /// <summary>The API listener refuses wildcard and multi-address bindings.</summary>
    [Test]
    public async Task TheListenerIsLoopbackOnly()
    {
        foreach (var listenUrl in new[]
        {
            "http://0.0.0.0:8420",
            "http://[::]:8420",
            "http://127.0.0.1:8420;http://0.0.0.0:8421",
        })
        {
            await Assert.That(() => TerraBaseHost.Create(listenUrl)).Throws<ArgumentException>();
        }
    }
}

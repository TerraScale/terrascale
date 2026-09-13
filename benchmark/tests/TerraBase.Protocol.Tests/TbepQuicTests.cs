// <copyright file="TbepQuicTests.cs" company="TerraScale">
// TerraBase (SE-14). See LICENSE.md.
// </copyright>

namespace TerraBase.Protocol.Tests;

/// <summary>Focused regression probes for the QUIC transport's TLS and lifecycle invariants.</summary>
public sealed class TbepQuicTests
{
    /// <summary>The QUIC client must validate the server chain and release a connected transport if stream opening fails.</summary>
    [Test]
    public async Task QuicTransportKeepsCertificateValidationAndOpenFailureCleanup()
    {
        var source = File.ReadAllText(SourcePath());

        await Assert.That(source.Contains("RemoteCertificateValidationCallback", StringComparison.Ordinal)).IsFalse();
        await Assert.That(source.Contains("trustServerCertificate", StringComparison.Ordinal)).IsFalse();

        var streamOpen = source.IndexOf("OpenOutboundStreamAsync", StringComparison.Ordinal);
        var failureHandler = source.IndexOf("catch", streamOpen, StringComparison.Ordinal);
        var connectionDispose = source.IndexOf("await connection.DisposeAsync()", failureHandler, StringComparison.Ordinal);

        await Assert.That(streamOpen).IsGreaterThanOrEqualTo(0);
        await Assert.That(failureHandler).IsGreaterThan(streamOpen);
        await Assert.That(connectionDispose).IsGreaterThan(failureHandler);
    }

    private static string SourcePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "TerraBase", "Protocol", "TbepQuic.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("the TbepQuic source was not found beside the test binary");
    }
}

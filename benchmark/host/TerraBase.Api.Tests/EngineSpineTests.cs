// <copyright file="EngineSpineTests.cs" company="TerraScale">
// TerraBaseV2 (SE-08). See LICENSE.md.
// </copyright>

using System.Diagnostics;

namespace TerraBase.Api.Tests;

/// <summary>
/// The real-engine document spine end to end, cross-language (P4): the
/// C# client drives the repository's real durable Engine through the
/// synthesized harness binary in its engine mode — write with idempotent
/// retry, read with version, conflict outcomes, and durability across a
/// full process restart at the same location.
/// </summary>
public sealed class EngineSpineTests
{
    private static string Location()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tbep-engine-spine-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static async Task<Process> StartAsync(string socket, string location)
    {
        var binary = await EngineHarness.BinaryPathAsync();
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = binary,
                Arguments = $"{socket} 1 engine:{location}",
                RedirectStandardOutput = true,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        _ = process.Start();
        // The harness announces itself on stdout once its listener binds.
        var announcement = await process.StandardOutput.ReadLineAsync();
        if (announcement is null || !announcement.StartsWith("serving ", StringComparison.Ordinal))
        {
            process.Kill();
            throw new InvalidOperationException($"the engine spine harness did not announce itself: {announcement}");
        }

        return process;
    }

    private static async Task StopAsync(Process process)
    {
        await process.StandardInput.WriteLineAsync(string.Empty);
        if (!process.WaitForExit(10_000))
        {
            process.Kill();
        }

        process.Dispose();
    }

    private static DocumentPut Order() => new()
    {
        Id = "order-1",
        Collection = "orders",
        Body = TbepValue.Of(new[]
        {
            new ObjectField("item", TbepValue.Of("bookshelf")),
            new ObjectField("quantity", TbepValue.Of(2L)),
        }),
    };

    /// <summary>The spine: commit, idempotent replay, conflict, read, and durability across restart.</summary>
    [Test]
    public async Task TheClientDrivesTheRealEngineAcrossARestart()
    {
        var location = Location();
        var socket = Path.Combine(Path.GetTempPath(), $"tbep-spine-{Guid.NewGuid():N}.sock");
        var process = await StartAsync(socket, location);
        try
        {
            await using var client = TbepClient.Connect(socket);
            await client.BindChannelAsync("backend", "tenant-a");

            // Commit through the real engine.
            var write = await client.DocumentWriteAsync(Order(), ExpectedVersion.Absent(), "op-spine-1", "op-spine-1");
            await Assert.That(write.Outcome.Variant).IsEqualTo("Committed");
            var receipt = (CommitReceipt)write.Outcome.Payload!;
            await Assert.That(receipt.OperationId).IsEqualTo("op-spine-1");
            await Assert.That(receipt.Version).IsEqualTo(1ul);

            // The idempotent retry replays the recorded receipt.
            var retry = await client.DocumentWriteAsync(Order(), ExpectedVersion.Absent(), "op-spine-1", "op-spine-1");
            await Assert.That(retry.Outcome.Variant).IsEqualTo("Committed");
            var replayed = (CommitReceipt)retry.Outcome.Payload!;
            await Assert.That(replayed.Sequence).IsEqualTo(receipt.Sequence);

            // An absent expectation on the existing document conflicts.
            var conflict = await client.DocumentWriteAsync(Order(), ExpectedVersion.Absent(), "op-spine-2", "op-spine-2");
            await Assert.That(conflict.Outcome.Variant).IsEqualTo("Conflict");

            // The read serves the published body and version.
            var read = await client.DocumentReadAsync("orders", "order-1");
            await Assert.That(read.Found).IsTrue();
            await Assert.That(read.Document!.Version).IsEqualTo(1ul);
            await Assert.That(read.Document.Body).IsEqualTo(Order().Body);

            // The P4 typed surface: the receipt names its acknowledgement
            // level, and the read names the position it observed.
            await Assert.That(receipt.Acknowledgement).IsEqualTo(Acknowledgement.RegionalQuorum());
            await Assert.That(read.Position).IsNotNull();
        }
        finally
        {
            await StopAsync(process);
        }

        // Durability: a fresh process over the same durable location
        // serves the committed body.
        var reopenedSocket = Path.Combine(Path.GetTempPath(), $"tbep-spine-restart-{Guid.NewGuid():N}.sock");
        var reopened = await StartAsync(reopenedSocket, location);
        try
        {
            await using var client = TbepClient.Connect(reopenedSocket);
            await client.BindChannelAsync("backend", "tenant-a");
            var read = await client.DocumentReadAsync("orders", "order-1");
            await Assert.That(read.Found).IsTrue();
            await Assert.That(read.Document!.Body).IsEqualTo(Order().Body);
        }
        finally
        {
            await StopAsync(reopened);
            Directory.Delete(location, recursive: true);
        }
    }
}

/// <summary>
/// The connection-open negotiation against the real engine (P4): the
/// highest common version wins, unknown capabilities are ignored, and a
/// version mismatch fails closed with the typed error before any
/// operation frame runs.
/// </summary>
public sealed class EngineNegotiationTests
{
    private static string Location()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tbep-engine-negotiate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static async Task<Process> StartAsync(string socket, string location)
    {
        var binary = await EngineHarness.BinaryPathAsync();
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = binary,
                Arguments = $"{socket} 1 engine:{location}",
                RedirectStandardOutput = true,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        _ = process.Start();
        var announcement = await process.StandardOutput.ReadLineAsync();
        if (announcement is null || !announcement.StartsWith("serving ", StringComparison.Ordinal))
        {
            process.Kill();
            throw new InvalidOperationException($"the negotiation harness did not announce itself: {announcement}");
        }

        return process;
    }

    private static async Task StopAsync(Process process)
    {
        await process.StandardInput.WriteLineAsync(string.Empty);
        if (!process.WaitForExit(10_000))
        {
            process.Kill();
        }

        process.Dispose();
    }

    /// <summary>The negotiation: selection, ignored unknowns, and the typed mismatch.</summary>
    [Test]
    public async Task TheClientNegotiatesVersionAndCapabilitiesWithTheRealEngine()
    {
    var location = Location();
    var socket = Path.Combine(Path.GetTempPath(), $"tbep-negotiate-{Guid.NewGuid():N}.sock");
    var process = await StartAsync(socket, location);
    try
    {
        await using var client = TbepClient.Connect(socket);

        // The highest common version and the known capabilities are
        // accepted; the unknown one is ignored, never refused.
        var negotiated = await client.NegotiateAsync([9, 2, 1], ["query-surface", "quantum-tunneling"]);
        await Assert.That(negotiated.SelectedVersion).IsEqualTo((ushort)1);
        await Assert.That(negotiated.AcceptedCapabilities).IsEquivalentTo(["query-surface"]);

        // The channel still operates after negotiating.
        await client.BindChannelAsync("backend", "tenant-a");
        var read = await client.DocumentReadAsync("orders", "order-none");
        await Assert.That(read.Found).IsFalse();
    }
    finally
    {
        await StopAsync(process);
        Directory.Delete(location, recursive: true);
    }

    // A version the server cannot speak fails closed with the typed
    // error before any operation frame runs.
    var mismatchSocket = Path.Combine(Path.GetTempPath(), $"tbep-mismatch-{Guid.NewGuid():N}.sock");
    var mismatchLocation = Location();
    var mismatch = await StartAsync(mismatchSocket, mismatchLocation);
    try
    {
        await using var client = TbepClient.Connect(mismatchSocket);
        await Assert.That(async () => await client.NegotiateAsync([2, 3], [])).Throws<TbepException>();
    }
    finally
    {
        await StopAsync(mismatch);
        Directory.Delete(mismatchLocation, recursive: true);
    }
}
}

/// <summary>
/// The Storage Tier Mover's typed administration against the real engine
/// (P4): configure, inspect, and the dry run's receipted explanation.
/// </summary>
public sealed class EngineTierTests
{
    private static string Location()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tbep-engine-tier-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static async Task<Process> StartAsync(string socket, string location)
    {
        var binary = await EngineHarness.BinaryPathAsync();
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = binary,
                Arguments = $"{socket} 1 engine:{location}",
                RedirectStandardOutput = true,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        _ = process.Start();
        var announcement = await process.StandardOutput.ReadLineAsync();
        if (announcement is null || !announcement.StartsWith("serving ", StringComparison.Ordinal))
        {
            process.Kill();
            throw new InvalidOperationException($"the tier harness did not announce itself: {announcement}");
        }

        return process;
    }

    /// <summary>The tier controls: configure records, inspect observes, and the dry pass receipts its reasons.</summary>
    [Test]
    public async Task TheClientAdministersTiersOnTheRealEngine()
    {
        var location = Location();
        var socket = Path.Combine(Path.GetTempPath(), $"tbep-tier-{Guid.NewGuid():N}.sock");
        var process = await StartAsync(socket, location);
        try
        {
            await using var client = TbepClient.Connect(socket);
            await client.BindChannelAsync("backend", "tenant-a");

            var configured = await client.TierConfigureAsync("prod", 3, 64UL << 20, 1, 2, [], "op-tier-1");
            await Assert.That(configured.PolicyId).IsEqualTo("prod");
            await Assert.That(configured.Version).IsEqualTo(3u);

            var inspect = await client.TierInspectAsync();
            await Assert.That(inspect.Configured).IsTrue();
            await Assert.That(inspect.Paused).IsFalse();

            var dry = await client.TierDryRunAsync();
            await Assert.That(dry.Receipt.Decision.Variant).IsEqualTo("None");
            await Assert.That(dry.Receipt.DryRun).IsTrue();
            await Assert.That(dry.Receipt.Reasons).IsNotEmpty();
        }
        finally
        {
            await process.StandardInput.WriteLineAsync(string.Empty);
            if (!process.WaitForExit(10_000))
            {
                process.Kill();
            }

            process.Dispose();
            Directory.Delete(location, recursive: true);
        }
    }
}

/// <summary>
/// The QUIC+TLS binding cross-language (P4): the C# client connects
/// over TLS 1.3 to the real engine's QUIC listener and drives the same
/// spine — the identical frame layer on the network binding.
/// </summary>
public sealed class EngineQuicTests
{
    private static string Location()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tbep-engine-quic-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>The spine over QUIC: bind, commit with receipt, read back.</summary>
    [Test]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task TheClientDrivesTheRealEngineOverQuicTls()
    {
        // The row provisions its own self-signed identity; the Rust
        // process reads the PEM pair we write.
        var home = Path.Combine(Path.GetTempPath(), $"tbep-quic-identity-{Guid.NewGuid():N}");
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=tbep-quic-row",
            rsa,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        var names = new System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddDays(1));
        Directory.CreateDirectory(home);
        var certPem = Path.Combine(home, "cert.pem");
        var keyPem = Path.Combine(home, "key.pem");
        await File.WriteAllTextAsync(certPem, certificate.ExportCertificatePem());
        await File.WriteAllTextAsync(keyPem, rsa.ExportPkcs8PrivateKeyPem());

        var location = Location();
        var socket = Path.Combine(Path.GetTempPath(), $"tbep-quic-row-{Guid.NewGuid():N}.sock");
        var port = 34000 + Random.Shared.Next(1000);
        var binary = await EngineHarness.BinaryPathAsync();
        var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = binary,
                Arguments = $"{socket} 1 engine:{location} quic:{port}:{certPem}:{keyPem}",
                RedirectStandardOutput = true,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        _ = process.Start();
        var announcement = await process.StandardOutput.ReadLineAsync();
        if (announcement is null || !announcement.StartsWith("quic ", StringComparison.Ordinal))
        {
            process.Kill();
            throw new InvalidOperationException($"the QUIC harness did not announce its listener: {announcement}");
        }

        try
        {
            if (!System.Net.Quic.QuicConnection.IsSupported)
            {
                // The host carries no msquic closure: the QUIC row cannot
                // run here, and the skip records that honestly rather
                // than failing a working binding (the Rust lane covers
                // the same transport end to end).
                throw new TUnit.Core.Exceptions.SkipTestException(
                    "this host carries no QUIC platform support (msquic)");
            }

            await using var client = await TerraBase.Protocol.TbepQuic.ConnectAsync("localhost", port, trustServerCertificate: true);
            await client.BindChannelAsync("backend", "tenant-a");

            var write = await client.DocumentWriteAsync(
                new DocumentPut
                {
                    Id = "order-1",
                    Collection = "orders",
                    Body = TbepValue.Of(new[]
                    {
                        new ObjectField("item", TbepValue.Of("bookshelf")),
                        new ObjectField("quantity", TbepValue.Of(2L)),
                    }),
                },
                ExpectedVersion.Absent(),
                "op-quic-1",
                "op-quic-1");
            await Assert.That(write.Outcome.Variant).IsEqualTo("Committed");
            var receipt = (CommitReceipt)write.Outcome.Payload!;
            await Assert.That(receipt.Version).IsEqualTo(1ul);
            await Assert.That(receipt.Acknowledgement).IsEqualTo(Acknowledgement.RegionalQuorum());

            var read = await client.DocumentReadAsync("orders", "order-1");
            await Assert.That(read.Found).IsTrue();
            await Assert.That(read.Position).IsNotNull();
        }
        finally
        {
            await process.StandardInput.WriteLineAsync(string.Empty);
            if (!process.WaitForExit(10_000))
            {
                process.Kill();
            }

            process.Dispose();
            Directory.Delete(location, recursive: true);
            try
            {
                Directory.Delete(home, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}

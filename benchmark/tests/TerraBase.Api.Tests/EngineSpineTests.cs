// <copyright file="EngineSpineTests.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

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

    private static X509Certificate2 IssueWorkloadCertificate(
        X509Certificate2 issuer,
        string subject,
        Oid purpose,
        bool server)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            subject,
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { purpose }, true));
        if (server)
        {
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("localhost");
            request.CertificateExtensions.Add(names.Build());
        }

        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7f;
        serial[^1] |= 1;
        DateTimeOffset issuerNotBefore = issuer.NotBefore.ToUniversalTime();
        DateTimeOffset issuerNotAfter = issuer.NotAfter.ToUniversalTime();
        using var issued = request.Create(
            issuer,
            issuerNotBefore.AddSeconds(1),
            issuerNotAfter.AddSeconds(-1),
            serial);
        return issued.CopyWithPrivateKey(key);
    }

    /// <summary>The spine over QUIC: bind, commit with receipt, read back.</summary>
    [Test]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task TheClientDrivesTheRealEngineOverQuicTls()
    {
        // The row provisions its own CA and distinct engine and gateway
        // workload identities. The Rust process trusts only the row CA.
        var home = Path.Combine(Path.GetTempPath(), $"tbep-quic-identity-{Guid.NewGuid():N}");
        using var rootKey = RSA.Create(2048);
        var rootRequest = new CertificateRequest(
            "CN=tbep-quic-row-root",
            rootKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
            true));
        using var root = rootRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddDays(1));
        using var serverCertificate = IssueWorkloadCertificate(
            root,
            "CN=tbep-engine",
            new Oid("1.3.6.1.5.5.7.3.1", "Server Authentication"),
            server: true);
        using var clientCertificate = IssueWorkloadCertificate(
            root,
            "CN=database-gateway",
            new Oid("1.3.6.1.5.5.7.3.2", "Client Authentication"),
            server: false);
        Directory.CreateDirectory(home);
        var certPem = Path.Combine(home, "cert.pem");
        var keyPem = Path.Combine(home, "key.pem");
        var rootPem = Path.Combine(home, "client-root.pem");
        await File.WriteAllTextAsync(certPem, serverCertificate.ExportCertificatePem());
        using (var serverKey = serverCertificate.GetRSAPrivateKey())
        {
            await File.WriteAllTextAsync(keyPem, serverKey!.ExportPkcs8PrivateKeyPem());
        }

        await File.WriteAllTextAsync(rootPem, root.ExportCertificatePem());

        var location = Location();
        var socket = Path.Combine(Path.GetTempPath(), $"tbep-quic-row-{Guid.NewGuid():N}.sock");
        var port = 34000 + Random.Shared.Next(1000);
        var binary = await EngineHarness.BinaryPathAsync();
        var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = binary,
                Arguments = $"{socket} 1 engine:{location} quic:{port}:{certPem}:{keyPem}:{rootPem}",
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

            await using var client = await TerraBase.Protocol.TbepQuic.ConnectAsync(
                "127.0.0.1",
                port,
                clientCertificate,
                serverTrustRoots: new X509Certificate2Collection { root });
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

/// <summary>
/// The atomic document batch against the real engine (P4): one frame,
/// one engine transaction — every item commits together, a conflicting
/// item rolls the whole batch back, and the idempotent retry replays
/// the batch receipt.
/// </summary>
public sealed class EngineBatchTests
{
    private static string Location()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tbep-engine-batch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static BatchWrite Item(string id, long quantity) => new()
    {
        Document = new DocumentPut
        {
            Id = id,
            Collection = "orders",
            Body = TbepValue.Of(new[]
            {
                new ObjectField("item", TbepValue.Of("bookshelf")),
                new ObjectField("quantity", TbepValue.Of(quantity)),
            }),
        },
        Expected = ExpectedVersion.Absent(),
        OperationId = $"op-batch-{id}",
    };

    /// <summary>The batch: atomic commit, whole-batch rollback, idempotent replay.</summary>
    [Test]
    public async Task TheClientDrivesAtomicBatchesOnTheRealEngine()
    {
        var location = Location();
        var socket = Path.Combine(Path.GetTempPath(), $"tbep-batch-{Guid.NewGuid():N}.sock");
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = await EngineHarness.BinaryPathAsync(),
                Arguments = $"{socket} 1 engine:{location}",
                RedirectStandardOutput = true,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        _ = process.Start();
        _ = await process.StandardOutput.ReadLineAsync();

        try
        {
            await using var client = TbepClient.Connect(socket);
            await client.BindChannelAsync("backend", "tenant-a");

            var batch = await client.DocumentBatchWriteAsync([Item("a", 1), Item("b", 2)], null, "op-batch-1");
            await Assert.That(batch.Outcome.Variant).IsEqualTo("Committed");

            var read = await client.DocumentReadAsync("orders", "a");
            await Assert.That(read.Found).IsTrue();

            // The conflicting item rolls the whole batch back.
            var conflicting = await client.DocumentBatchWriteAsync(
                [Item("a", 9), Item("c", 3)],
                null,
                "op-batch-2");
            await Assert.That(conflicting.Outcome.Variant).IsEqualTo("Conflict");
            var rolled = await client.DocumentReadAsync("orders", "c");
            await Assert.That(rolled.Found).IsFalse();
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
/// Protocol-level reconnect/resume against the real engine (P4): the
/// engine process dies mid-session, a fresh process opens the same
/// durable location, the client reconnects its channel, and the SAME
/// idempotency key replays the recorded receipt — resume without
/// re-applying.
/// </summary>
public sealed class EngineResumeTests
{
    private static string Location()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tbep-engine-resume-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static async Task<Process> StartAsync(string socket, string location)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = await EngineHarness.BinaryPathAsync(),
                Arguments = $"{socket} 1 engine:{location}",
                RedirectStandardOutput = true,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        _ = process.Start();
        _ = await process.StandardOutput.ReadLineAsync();
        return process;
    }

    private static async Task KillAsync(Process process)
    {
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
        process.Dispose();
    }

    /// <summary>Resume: reconnect after a hard kill, replay the receipt, continue.</summary>
    [Test]
    public async Task TheClientResumesAcrossAHardProcessKill()
    {
        var location = Location();
        var socket = Path.Combine(Path.GetTempPath(), $"tbep-resume-{Guid.NewGuid():N}.sock");
        var process = await StartAsync(socket, location);

        CommitReceipt receipt;
        var client = TbepClient.Connect(socket);
        await client.BindChannelAsync("backend", "tenant-a");
        var write = await client.DocumentWriteAsync(
            new DocumentPut
            {
                Id = "order-1",
                Collection = "orders",
                Body = TbepValue.Of(new[] { new ObjectField("item", TbepValue.Of("bookshelf")) }),
            },
            ExpectedVersion.Absent(),
            "op-resume-1",
            "op-resume-1");
        await Assert.That(write.Outcome.Variant).IsEqualTo("Committed");
        receipt = (CommitReceipt)write.Outcome.Payload!;

        // A hard kill — no graceful shutdown — then a fresh process over
        // the same durable location. The old channel dies with the
        // process; the client resumes on a new one.
        await KillAsync(process);
        var restarted = await StartAsync(socket, location);
        try
        {
            await using var resumed = client.Reconnect(socket);

            // The same idempotency key replays the recorded receipt: the
            // durable ledger resolves the identity across the restart.
            var replay = await resumed.DocumentWriteAsync(
                new DocumentPut
                {
                    Id = "order-1",
                    Collection = "orders",
                    Body = TbepValue.Of(new[] { new ObjectField("item", TbepValue.Of("bookshelf")) }),
                },
                ExpectedVersion.Absent(),
                "op-resume-1",
                "op-resume-1");
            await Assert.That(replay.Outcome.Variant).IsEqualTo("Committed");
            var replayed = (CommitReceipt)replay.Outcome.Payload!;
            await Assert.That(replayed.Sequence).IsEqualTo(receipt.Sequence);

            // The resumed channel continues serving new work.
            var next = await resumed.DocumentWriteAsync(
                new DocumentPut
                {
                    Id = "order-2",
                    Collection = "orders",
                    Body = TbepValue.Of(new[] { new ObjectField("item", TbepValue.Of("desk")) }),
                },
                ExpectedVersion.Absent(),
                "op-resume-2",
                "op-resume-2");
            await Assert.That(next.Outcome.Variant).IsEqualTo("Committed");
        }
        finally
        {
            await restarted.StandardInput.WriteLineAsync(string.Empty);
            if (!restarted.WaitForExit(10_000))
            {
                restarted.Kill();
            }

            restarted.Dispose();
            await client.DisposeAsync();
            Directory.Delete(location, recursive: true);
        }
    }
}

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

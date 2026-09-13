// <copyright file="DtoMappingTests.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api.Tests;

using System.Text.Json;

/// <summary>The public DTO mappings that preserve protocol receipt facts.</summary>
public sealed class DtoMappingTests
{
    /// <summary>A committed receipt exposes its acknowledgement and backup outcome.</summary>
    [Test]
    public async Task CommitReceiptMapsAcknowledgementAndBackup()
    {
        var dto = CommitReceiptDto.Of(new CommitReceipt
        {
            Sequence = 17,
            Version = 3,
            OperationId = "operation-17",
            Acknowledgement = Acknowledgement.GlobalQuorum(),
            Backup = BackupOutcome.Partial(),
        });

        await Assert.That(dto.Sequence).IsEqualTo(17ul);
        await Assert.That(dto.Version).IsEqualTo(3ul);
        await Assert.That(dto.OperationId).IsEqualTo("operation-17");
        await Assert.That(dto.Acknowledgement).IsEqualTo("global_quorum");
        await Assert.That(dto.Backup).IsEqualTo("partial");
    }

    /// <summary>Absent optional protocol facts stay absent on the public receipt.</summary>
    [Test]
    public async Task CommitReceiptLeavesAbsentAcknowledgementAndBackupNull()
    {
        var dto = CommitReceiptDto.Of(new CommitReceipt
        {
            Sequence = 17,
            Version = 3,
            OperationId = "operation-17",
        });

        await Assert.That(dto.Acknowledgement).IsNull();
        await Assert.That(dto.Backup).IsNull();
    }

    /// <summary>A document value must select exactly one wire discriminator.</summary>
    [Test]
    public async Task JsonValueRejectsAmbiguousOrFalseNullDiscriminators()
    {
        await Assert.ThrowsAsync<JsonException>(() =>
        {
            _ = new JsonValue { Null = true, String = "ambiguous" }.ToValue();
            return Task.CompletedTask;
        });

        await Assert.ThrowsAsync<JsonException>(() =>
        {
            _ = new JsonValue { Null = false }.ToValue();
            return Task.CompletedTask;
        });
    }

    /// <summary>A nested null value cannot bypass the document discriminator invariant.</summary>
    [Test]
    public async Task JsonValueRejectsNullArrayAndObjectChildren()
    {
        await Assert.ThrowsAsync<JsonException>(() =>
        {
            _ = new JsonValue { Array = [null!] }.ToValue();
            return Task.CompletedTask;
        });

        await Assert.ThrowsAsync<JsonException>(() =>
        {
            _ = new JsonValue { Object = new Dictionary<string, JsonValue> { ["missing"] = null! } }.ToValue();
            return Task.CompletedTask;
        });
    }
}

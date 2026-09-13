// <copyright file="DtoMappingTests.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api.Tests;

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
}

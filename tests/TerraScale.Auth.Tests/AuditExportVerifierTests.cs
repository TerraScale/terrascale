namespace TerraScale.Auth.Tests;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraScale.Auth.Store;

public sealed class AuditExportVerifierTests
{
    [Test]
    public async Task VerifiesExportedEventsAndIdentifiesTampering()
    {
        var key = Encoding.UTF8.GetBytes("audit-verifier-test-key");
        var unsigned = new AuditEvent
        {
            Id = "evt_verifier",
            InstanceId = "ins_verifier",
            Type = "user.updated",
            ActorId = "usr_operator",
            Data = new Dictionary<string, object> { ["field"] = "name" },
            CreatedAt = 1_700_000_000,
        };
        var hash = AuditEvent.ComputeIntegrityHash(unsigned);
        var signed = unsigned with
        {
            IntegrityVersion = 1,
            IntegrityHash = hash,
            IntegritySignature = Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"v1:{hash}"))),
            IntegrityKeyFingerprint = AuditEvent.ComputeKeyFingerprint(key),
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            new
            {
                id = signed.Id,
                instance_id = signed.InstanceId,
                type = signed.Type,
                actor_id = signed.ActorId,
                data = signed.Data,
                created_at = signed.CreatedAt,
                integrity_version = signed.IntegrityVersion,
                integrity_hash = signed.IntegrityHash,
                integrity_signature = signed.IntegritySignature,
                integrity_key_fingerprint = signed.IntegrityKeyFingerprint,
            },
        });

        var result = AuditExportVerifier.VerifyJson(json, [key]);
        await Assert.That(result.IsAuthentic).IsTrue();
        await Assert.That(result.ValidAuthenticityCount).IsEqualTo(1);

        var wrongFingerprint = Encoding.UTF8.GetString(json)
            .Replace(signed.IntegrityKeyFingerprint!, new string('0', 64), StringComparison.Ordinal);
        var wrongFingerprintResult = AuditExportVerifier.VerifyJson(
            Encoding.UTF8.GetBytes(wrongFingerprint), [key]);
        await Assert.That(wrongFingerprintResult.IsValid).IsTrue();
        await Assert.That(wrongFingerprintResult.IsAuthentic).IsFalse();

        var tampered = Encoding.UTF8.GetString(json).Replace("user.updated", "user.deleted", StringComparison.Ordinal);
        var tamperedResult = AuditExportVerifier.VerifyJson(Encoding.UTF8.GetBytes(tampered), [key]);
        await Assert.That(tamperedResult.IsValid).IsFalse();
        await Assert.That(tamperedResult.InvalidIntegrityIds).Contains("evt_verifier");
    }
}

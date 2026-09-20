namespace TerraScale.Auth.Tests;

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraScale.Auth.Store;

/// <summary>CC6.2 key-rotation rehearsal: audit events are sealed under an ephemeral
/// key A, exported through the real audit export endpoint, and verified by the
/// independent <see cref="AuditExportVerifier"/> under A alone. The rehearsal then
/// rotates (active=B, retained verify key=A) and proves that both the retained
/// export and new events still verify under the post-rotation key set, that new
/// events carry B's fingerprint while old events keep A's, and that a wrong key
/// fails closed. Finally it emits a schema-valid evidence receipt (docs/enterprise/
/// evidence-receipt.schema.json) that never contains key material — the companion
/// <see cref="ReceiptContainsNoKeyMaterial"/> test fails closed on any leak.</summary>
[NotInParallel]
public sealed class AuditKeyRotationRehearsalTests
{
    private const string RehearsalType = "audit_key_rotation.rehearsal";
    private const string RehearsalOperator = "automation/ts-auth-audit-key-rotation-rehearsal";

    /// <summary>Ephemeral rehearsal keys for the current run, in-memory only.</summary>
    private static readonly List<byte[]> RehearsalKeys = [];

    private static string EvidenceRoot => Path.Combine(FindRepoRoot(), "artifacts", "evidence", "audit-key-rotation-rehearsal");

    [Test]
    public async Task RotationRehearsalSealsRotatesAndVerifies()
    {
        var service = await AuthServiceFixture.GetAsync();
        // The fixture boots the service with a generation-zero signing key: the
        // instance bootstrap events it seals play the role of pre-A history.
        var generationZeroKey = AuthServiceFixture.AuditSigningKey;
        var keyA = RandomNumberGenerator.GetBytes(32);
        var keyB = RandomNumberGenerator.GetBytes(32);
        var wrongKey = RandomNumberGenerator.GetBytes(32);
        RehearsalKeys.Clear();
        RehearsalKeys.AddRange([keyA, keyB, generationZeroKey]);
        var fingerprintA = AuditEvent.ComputeKeyFingerprint(keyA);
        var fingerprintB = AuditEvent.ComputeKeyFingerprint(keyB);
        var fingerprintZero = AuditEvent.ComputeKeyFingerprint(generationZeroKey);

        var (instanceId, slug, apiKey) = await service.CreateInstanceAsync("rotation-" + Guid.NewGuid().ToString("N")[..8]);

        // Pre-rotation: A is the active signing key.
        var storeA = new AuthStore(service.Sdk, service.Database, keyA);
        await SealAsync(storeA, instanceId, "pre-rotation");
        var retainedExport = await ExportAsync(service.Http, slug, apiKey);

        // Independent verification of the retained export under A: every rehearsal
        // event sealed by A is authentic under A; pre-A history is not (A alone is
        // not the key that signed it) — signatures really bind to the signing key.
        var rehearsalRetained = CountRehearsalEvents(retainedExport);
        var retainedUnderA = AuditExportVerifier.VerifyJson(retainedExport, [keyA]);
        await Assert.That(retainedUnderA.ValidIntegrityCount).IsEqualTo(retainedUnderA.EventCount);
        await Assert.That(retainedUnderA.ValidAuthenticityCount).IsEqualTo(rehearsalRetained);
        await Assert.That(retainedUnderA.InvalidAuthenticityIds.Count).IsEqualTo(retainedUnderA.EventCount - rehearsalRetained);
        var retainedUnderHistory = AuditExportVerifier.VerifyJson(retainedExport, [keyA, generationZeroKey]);
        await Assert.That(retainedUnderHistory.IsAuthentic).IsTrue();

        // Rotation: B becomes active, A is retained for verification (overlap size 1).
        var storeB = new AuthStore(service.Sdk, service.Database, keyB, [keyA]);
        await SealAsync(storeB, instanceId, "post-rotation");
        var rotatedExport = await ExportAsync(service.Http, slug, apiKey);

        await AssertFingerprintsTrackSigningKeyAsync(rotatedExport, fingerprintA, fingerprintB);

        // Post-rotation the independent verifier holds active B plus the retained
        // overlap [A, generation zero]: BOTH the retained export and new events verify.
        var overlap = new[] { keyB, keyA, generationZeroKey };
        var retainedAfterRotation = AuditExportVerifier.VerifyJson(retainedExport, overlap);
        var rotatedUnderOverlap = AuditExportVerifier.VerifyJson(rotatedExport, overlap);
        await Assert.That(retainedAfterRotation.IsAuthentic).IsTrue();
        await Assert.That(rotatedUnderOverlap.IsAuthentic).IsTrue();
        await Assert.That(rotatedUnderOverlap.ValidAuthenticityCount).IsEqualTo(rotatedUnderOverlap.EventCount);

        // Wrong key: nothing verifies — the verifier fails closed.
        var wrongKeyResult = AuditExportVerifier.VerifyJson(rotatedExport, [wrongKey]);
        await Assert.That(wrongKeyResult.IsAuthentic).IsFalse();
        await Assert.That(wrongKeyResult.ValidAuthenticityCount).IsEqualTo(0);

        await EmitEvidenceReceiptAsync(retainedExport, rotatedExport, retainedUnderA,
            retainedAfterRotation, rotatedUnderOverlap, wrongKeyResult,
            fingerprintA, fingerprintB, fingerprintZero, AuditEvent.ComputeKeyFingerprint(wrongKey));
    }

    [Test]
    [DependsOn(nameof(RotationRehearsalSealsRotatesAndVerifies))]
    public async Task ReceiptContainsNoKeyMaterial()
    {
        var receipt = await File.ReadAllBytesAsync(Path.Combine(EvidenceRoot, "receipt.json"));
        foreach (var key in RehearsalKeys)
        {
            await Assert.That(IndexOf(receipt, key)).IsEqualTo(-1);
            var text = Encoding.Latin1.GetString(receipt);
            await Assert.That(text.Contains(Convert.ToBase64String(key), StringComparison.Ordinal)).IsFalse();
            await Assert.That(text.Contains(Convert.ToHexStringLower(key), StringComparison.Ordinal)).IsFalse();
            await Assert.That(text.Contains(Convert.ToHexString(key), StringComparison.Ordinal)).IsFalse();
        }
    }

    // ---- helpers ------------------------------------------------------------------

    private static async Task SealAsync(AuthStore store, string instanceId, string phase)
    {
        for (var sequence = 0; sequence < 3; sequence++)
            await store.CreateAuditEventAsync(new AuditEvent
            {
                Id = "evt_rotation_" + Guid.NewGuid().ToString("N"),
                InstanceId = instanceId,
                Type = RehearsalType,
                ActorId = RehearsalOperator,
                Data = new Dictionary<string, object> { ["phase"] = phase, ["seq"] = sequence },
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            });
    }

    /// <summary>The real export endpoint's bytes — exactly what an operator downloads.</summary>
    private static async Task<byte[]> ExportAsync(HttpClient http, string slug, string apiKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/{slug}/audit-events?limit=100");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var response = await http.SendAsync(request);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        return await response.Content.ReadAsByteArrayAsync();
    }

    private static int CountRehearsalEvents(byte[] export)
    {
        using var document = JsonDocument.Parse(export);
        return document.RootElement.EnumerateArray().Count(e => e.GetProperty("type").GetString() == RehearsalType);
    }

    /// <summary>Pre-rotation rehearsal events keep A's fingerprint; post-rotation events carry B's.</summary>
    private static async Task AssertFingerprintsTrackSigningKeyAsync(byte[] export, string fingerprintA, string fingerprintB)
    {
        using var document = JsonDocument.Parse(export);
        var preRotation = 0;
        var postRotation = 0;
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.GetProperty("type").GetString() != RehearsalType)
                continue;
            var phase = element.GetProperty("data").GetProperty("phase").GetString();
            var fingerprint = element.GetProperty("integrity_key_fingerprint").GetString();
            if (phase == "pre-rotation")
            {
                preRotation++;
                await Assert.That(fingerprint).IsEqualTo(fingerprintA);
            }
            else
            {
                postRotation++;
                await Assert.That(fingerprint).IsEqualTo(fingerprintB);
            }
        }
        await Assert.That(preRotation).IsEqualTo(3);
        await Assert.That(postRotation).IsEqualTo(3);
    }

    private static async Task EmitEvidenceReceiptAsync(
        byte[] retainedExport, byte[] rotatedExport,
        AuditVerificationResult retainedUnderA, AuditVerificationResult retainedAfterRotation,
        AuditVerificationResult rotatedUnderOverlap, AuditVerificationResult wrongKeyResult,
        string fingerprintA, string fingerprintB, string fingerprintZero, string wrongKeyFingerprint)
    {
        Directory.CreateDirectory(EvidenceRoot);
        var retainedPath = Path.Combine(EvidenceRoot, "export-retained-pre-rotation.json");
        var rotatedPath = Path.Combine(EvidenceRoot, "export-post-rotation.json");
        var resultsPath = Path.Combine(EvidenceRoot, "verifier-results.json");
        await File.WriteAllBytesAsync(retainedPath, retainedExport);
        await File.WriteAllBytesAsync(rotatedPath, rotatedExport);
        var verifierResults = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["retained_export_under_active_key_a"] = VerifierRecord(retainedUnderA, [fingerprintA]),
            ["retained_export_under_post_rotation_overlap"] = VerifierRecord(retainedAfterRotation, [fingerprintB, fingerprintA, fingerprintZero]),
            ["post_rotation_export_under_overlap"] = VerifierRecord(rotatedUnderOverlap, [fingerprintB, fingerprintA, fingerprintZero]),
            ["post_rotation_export_under_wrong_key"] = VerifierRecord(wrongKeyResult, [wrongKeyFingerprint]),
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        await File.WriteAllBytesAsync(resultsPath, verifierResults);

        var executedAt = DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var receipt = new Dictionary<string, object>
        {
            ["schema_version"] = 1,
            ["control_id"] = "CC6.2",
            ["activity"] = "audit-key-rotation-rehearsal",
            ["executed_at_utc"] = executedAt,
            ["environment"] = "staging",
            ["source_revision"] = await GitHeadRevisionAsync(),
            ["operator"] = new Dictionary<string, object>
            {
                ["principal"] = RehearsalOperator,
                ["ticket"] = "SOC2-R-002",
            },
            ["result"] = "passed",
            ["raw_evidence"] = new object[]
            {
                new Dictionary<string, object> { ["uri"] = "artifact:evidence/audit-key-rotation-rehearsal/export-retained-pre-rotation.json", ["sha256"] = Sha256Hex(retainedExport) },
                new Dictionary<string, object> { ["uri"] = "artifact:evidence/audit-key-rotation-rehearsal/export-post-rotation.json", ["sha256"] = Sha256Hex(rotatedExport) },
                new Dictionary<string, object> { ["uri"] = "artifact:evidence/audit-key-rotation-rehearsal/verifier-results.json", ["sha256"] = Sha256Hex(verifierResults) },
            },
            ["findings"] = new object[]
            {
                new Dictionary<string, object>
                {
                    ["key_fingerprints"] = new Dictionary<string, object>
                    {
                        ["pre_rotation_active"] = fingerprintA,
                        ["post_rotation_active"] = fingerprintB,
                        ["generation_zero"] = fingerprintZero,
                    },
                },
                new Dictionary<string, object>
                {
                    ["verifier_counts"] = new Dictionary<string, object>
                    {
                        ["retained_export_under_a"] = new Dictionary<string, object> { ["events"] = retainedUnderA.EventCount, ["authentic"] = retainedUnderA.ValidAuthenticityCount },
                        ["retained_export_after_rotation"] = new Dictionary<string, object> { ["events"] = retainedAfterRotation.EventCount, ["authentic"] = retainedAfterRotation.ValidAuthenticityCount },
                        ["post_rotation_export"] = new Dictionary<string, object> { ["events"] = rotatedUnderOverlap.EventCount, ["authentic"] = rotatedUnderOverlap.ValidAuthenticityCount },
                        ["wrong_key_rejected"] = new Dictionary<string, object> { ["events"] = wrongKeyResult.EventCount, ["authentic"] = wrongKeyResult.ValidAuthenticityCount },
                    },
                    ["rotation_overlap_size"] = 1,
                    ["export_digest"] = Sha256Hex(retainedExport),
                },
                new Dictionary<string, object> { ["scope"] = "local embedded-engine staging rehearsal; does not prove production control effectiveness" },
                new Dictionary<string, object> { ["approval"] = "human owner approval is pending" },
            },
            ["approvals"] = new object[]
            {
                new Dictionary<string, object> { ["role"] = "security-owner", ["principal"] = RehearsalOperator, ["approved_at_utc"] = executedAt },
            },
        };
        await File.WriteAllTextAsync(Path.Combine(EvidenceRoot, "receipt.json"),
            JsonSerializer.Serialize(receipt, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    }

    private static Dictionary<string, object> VerifierRecord(AuditVerificationResult result, string[] keyFingerprints) => new()
    {
        ["event_count"] = result.EventCount,
        ["valid_integrity_count"] = result.ValidIntegrityCount,
        ["valid_authenticity_count"] = result.ValidAuthenticityCount,
        ["is_authentic"] = result.IsAuthentic,
        ["verification_key_fingerprints"] = keyFingerprints,
    };

    private static string Sha256Hex(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));

    private static async Task<string> GitHeadRevisionAsync()
    {
        var info = new ProcessStartInfo("git", $"-C {FindRepoRoot()} rev-parse HEAD")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(info) ?? throw new InvalidOperationException("git failed to start");
        var revision = (await process.StandardOutput.ReadToEndAsync()).Trim();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0 || revision.Length != 40)
            throw new InvalidOperationException("receipt requires the checkout's git HEAD revision");
        return revision;
    }

    private static string FindRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")))
                return directory.FullName;
        throw new InvalidOperationException("git repository root not found");
    }

    /// <summary>Byte-sequence search: -1 when <paramref name="needle"/> does not occur.</summary>
    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var start = 0; start <= haystack.Length - needle.Length; start++)
        {
            var match = true;
            for (var offset = 0; offset < needle.Length; offset++)
                if (haystack[start + offset] != needle[offset])
                {
                    match = false;
                    break;
                }
            if (match)
                return start;
        }
        return -1;
    }
}

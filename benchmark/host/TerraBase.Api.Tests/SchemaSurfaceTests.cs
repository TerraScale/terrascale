// <copyright file="SchemaSurfaceTests.cs" company="TerraScale">
// TerraBase (SM-04). See LICENSE.md.
// </copyright>

namespace TerraBase.Api.Tests;

using System.Text.Json;

/// <summary>
/// The SM-04 schema-surface rows of the API host: the six typed
/// schema-lifecycle endpoints over the scripted engine (the engine-backed
/// TBEP host is the recorded integrator action, so the fixture scripts the
/// same wire answers), the local gate, tenant isolation from the bound
/// channel, and the lifecycle audit that never carries a document body.
/// </summary>
public sealed class SchemaSurfaceTests
{
    private static string SocketPath() =>
        Path.Combine(Path.GetTempPath(), $"tbep-schema-host-{Guid.NewGuid():N}.sock");

    /// <summary>The schema document of the documented JSON Schema subset the rows declare.</summary>
    private static Dictionary<string, object> UsersSchema(ulong version) => new()
    {
        ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object>
        {
            ["email"] = new Dictionary<string, object> { ["type"] = "string" },
            ["login_count"] = new Dictionary<string, object> { ["type"] = "integer" },
        },
        ["required"] = new[] { "email" },
        ["x-terrabase-schema-version"] = version,
    };

    /// <summary>A migration contract that removes <c>nick</c> and adds <c>login_count</c>.</summary>
    private static Dictionary<string, object> UsersContract() => new()
    {
        ["collection"] = "users",
        ["fromVersion"] = 1ul,
        ["toVersion"] = 2ul,
        ["target"] = UsersSchema(2),
        ["transforms"] = new object[]
        {
            new Dictionary<string, object> { ["kind"] = "remove_field", ["field"] = "nick" },
            new Dictionary<string, object>
            {
                ["kind"] = "add_field",
                ["field"] = "login_count",
                ["default"] = new Dictionary<string, object> { ["integer"] = 0L },
            },
        },
    };

    [Test]
    public async Task TheSchemaLifecycleRoundTripsThroughTheTypedEndpoints()
    {
        var path = SocketPath();
        await using var engine = new ScriptedTbepEngine(path);
        await using var host = await HostHarness.StartAsync(new EngineOptions { SocketPath = path });
        var caller = host.As("backend-operator", "tenant-a");

        // Declare: the collection records version 1.
        var (declareStatus, declare) = await HostHarness.SendAsync(
            caller,
            "/v1/schemas/users",
            new Dictionary<string, object> { ["schema"] = UsersSchema(1) },
            idempotencyKey: "op-declare-1",
            method: "PUT");
        await Assert.That(declareStatus).IsEqualTo(200);
        await Assert.That(declare.RootElement.GetProperty("enforcedVersion").GetUInt64()).IsEqualTo(1ul);
        await Assert.That(declare.RootElement.GetProperty("ruleCount").GetInt32()).IsEqualTo(3);

        // Status: the recorded version, without a mutation key.
        var (statusStatus, status) = await HostHarness.SendAsync(caller, "/v1/schemas/users");
        await Assert.That(statusStatus).IsEqualTo(200);
        await Assert.That(status.RootElement.GetProperty("enforcedVersion").GetUInt64()).IsEqualTo(1ul);

        // Validate: a typed violation names the field, never the value.
        var (validateStatus, validation) = await HostHarness.SendAsync(
            caller,
            "/v1/schemas/users/validate",
            new Dictionary<string, object> { ["document"] = new Dictionary<string, object> { ["email"] = 7, ["nick"] = "ada" } });
        await Assert.That(validateStatus).IsEqualTo(200);
        await Assert.That(validation.RootElement.GetProperty("valid").GetBoolean()).IsFalse();
        await Assert.That(validation.RootElement.GetProperty("enforcedVersion").GetUInt64()).IsEqualTo(1ul);
        var violations = validation.RootElement.GetProperty("violations");
        await Assert.That(violations.GetArrayLength()).IsGreaterThan(0);
        await Assert.That(violations[0].GetProperty("field").GetString()).IsEqualTo("email");
        var rendered = validation.RootElement.GetRawText();
        await Assert.That(rendered.Contains("ada")).IsFalse();

        // Migration status: no drive is live.
        var (migrationStatusStatus, migrationStatus) = await HostHarness.SendAsync(caller, "/v1/migrations/users");
        await Assert.That(migrationStatusStatus).IsEqualTo(200);
        await Assert.That(migrationStatus.RootElement.GetProperty("state").GetProperty("kind").GetString()).IsEqualTo("none");

        // Dry run: the plan is ready and states its evidence.
        var (dryRunStatus, dryRun) = await HostHarness.SendAsync(
            caller,
            "/v1/migrations/users/dry-run",
            new Dictionary<string, object> { ["contract"] = UsersContract() });
        if (dryRunStatus != 200)
        {
            Assert.Fail($"dry-run answered {dryRunStatus}: {dryRun.RootElement.GetRawText()}");
        }

        await Assert.That(dryRunStatus).IsEqualTo(200);
        await Assert.That(dryRun.RootElement.GetRawText()).Contains("plan");
        await Assert.That(dryRun.RootElement.GetProperty("plan").GetProperty("ready").GetBoolean()).IsTrue();
        await Assert.That(dryRun.RootElement.GetProperty("plan").GetProperty("fromVersion").GetUInt64()).IsEqualTo(1ul);
        await Assert.That(dryRun.RootElement.GetProperty("plan").GetProperty("toVersion").GetUInt64()).IsEqualTo(2ul);

        // Apply: the receipt seats version 2.
        var (applyStatus, apply) = await HostHarness.SendAsync(
            caller,
            "/v1/migrations/users/apply",
            new Dictionary<string, object> { ["contract"] = UsersContract(), ["acknowledgeNotRollbackable"] = true },
            idempotencyKey: "op-apply-1");
        await Assert.That(applyStatus).IsEqualTo(200);
        await Assert.That(apply.RootElement.GetProperty("receipt").GetProperty("toVersion").GetUInt64()).IsEqualTo(2ul);
        await Assert.That(apply.RootElement.GetProperty("receipt").GetProperty("rollbackLimit").GetString()).IsEqualTo("not_reversible");

        // The collection now records version 2.
        var (afterStatus, after) = await HostHarness.SendAsync(caller, "/v1/schemas/users");
        await Assert.That(afterStatus).IsEqualTo(200);
        await Assert.That(after.RootElement.GetProperty("enforcedVersion").GetUInt64()).IsEqualTo(2ul);
    }

    /// <summary>The schema endpoints sit behind the local gate: no backend context, no engine frame.</summary>
    [Test]
    public async Task TheSchemaEndpointsAreRefusedWithoutBackendContext()
    {
        var path = SocketPath();
        await using var engine = new ScriptedTbepEngine(path);
        await using var host = await HostHarness.StartAsync(new EngineOptions { SocketPath = path });
        var before = engine.Received.Count;

        var requests = new (string Path, string? Method)[]
        {
            ("/v1/schemas/users", "GET"),
            ("/v1/schemas/users", "PUT"),
            ("/v1/schemas/users/validate", "POST"),
            ("/v1/migrations/users", "GET"),
            ("/v1/migrations/users/dry-run", "POST"),
            ("/v1/migrations/users/apply", "POST"),
        };
        foreach (var (requestPath, method) in requests)
        {
            var (status, body) = await HostHarness.SendAsync(host.Http, requestPath, body: new { }, method: method);
            if (status != 401)
            {
                Assert.Fail($"{requestPath} answered {status}: {body.RootElement.GetRawText()}");
            }

            await Assert.That(status).IsEqualTo(401);
            await Assert.That(body.RootElement.GetProperty("code").GetString()).IsEqualTo("unauthorized");
        }

        await Assert.That(engine.Received.Count).IsEqualTo(before);
        await Assert.That(host.Audit.Recent(16).Count(record => record.Kind == AuditKind.GateRefused)).IsEqualTo(6);
    }

    /// <summary>The engine's bound channel enforces tenant isolation; the refusal is surfaced and audited.</summary>
    [Test]
    public async Task ASchemaDeclareForAnotherTenantIsRefusedTyped()
    {
        var path = SocketPath();
        await using var engine = new ScriptedTbepEngine(path);
        await using var host = await HostHarness.StartAsync(new EngineOptions { SocketPath = path });
        var caller = host.As("backend-operator", "tenant-a");

        // The scripted engine owns the collection for another tenant: the
        // engine refuses the frame, and the host surfaces the typed refusal.
        engine.SchemaOwner = ("users", "tenant-b");
        var (status, body) = await HostHarness.SendAsync(
            caller,
            "/v1/schemas/users",
            new Dictionary<string, object> { ["schema"] = UsersSchema(1) },
            idempotencyKey: "op-declare-cross",
            method: "PUT");
        await Assert.That(status).IsEqualTo(403);
        await Assert.That(body.RootElement.GetProperty("code").GetString()).IsEqualTo("tenant-refused");
        await Assert.That(host.Audit.Recent(8).Any(record => record.Kind == AuditKind.TenantRefused)).IsTrue();
    }

    /// <summary>A schema declaration without an idempotency key is refused before any engine frame.</summary>
    [Test]
    public async Task ASchemaMutationWithoutAnIdempotencyKeyIsRefused()
    {
        var path = SocketPath();
        await using var engine = new ScriptedTbepEngine(path);
        await using var host = await HostHarness.StartAsync(new EngineOptions { SocketPath = path });
        var caller = host.As("backend-operator", "tenant-a");
        var before = engine.Received.Count;

        var (status, body) = await HostHarness.SendAsync(caller, "/v1/schemas/users", new Dictionary<string, object> { ["schema"] = UsersSchema(1) }, method: "PUT");
        await Assert.That(status).IsEqualTo(400);
        await Assert.That(body.RootElement.GetProperty("code").GetString()).IsEqualTo("idempotency-key-invalid");
        await Assert.That(engine.Received.Count).IsEqualTo(before);
    }

    /// <summary>The lifecycle is audited with its own operation rows and never a document body.</summary>
    [Test]
    public async Task TheSchemaLifecycleIsAuditedWithoutDocumentBodies()
    {
        var path = SocketPath();
        await using var engine = new ScriptedTbepEngine(path);
        await using var host = await HostHarness.StartAsync(new EngineOptions { SocketPath = path });
        var caller = host.As("backend-operator", "tenant-a");
        const string canary = "terrabase-schema-audit-canary";

        await HostHarness.SendAsync(caller, "/v1/schemas/users/validate", new Dictionary<string, object> { ["document"] = new Dictionary<string, object> { ["email"] = canary } });
        await HostHarness.SendAsync(
            caller,
            "/v1/schemas/users",
            new Dictionary<string, object> { ["schema"] = UsersSchema(1) },
            idempotencyKey: "op-declare-audit",
            method: "PUT");

        var records = host.Audit.Recent(32);
        await Assert.That(records.Any(record => record.Operation is "schema.declare" or "schema.validate")).IsTrue();
        foreach (var record in records)
        {
            await Assert.That(record.Detail.Contains(canary)).IsFalse();
            await Assert.That(JsonSerializer.Serialize(record).Contains(canary)).IsFalse();
        }
    }
}

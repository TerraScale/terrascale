// <copyright file="GateTests.cs" company="TerraScale">
// TerraBaseV2 (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api.Tests;

/// <summary>
/// The authorization and tenant-isolation rows of the AC (against the
/// scripted engine): the boundary is local and backend-only
/// (PD-2026-08-26-11 D1 — no authentication surface here), so the
/// tested guarantees are the local gate (a caller without
/// backend-supplied context is refused before any engine frame is
/// sent), the engine-enforced tenant isolation over the session and
/// subscription lifecycle (a cross-tenant frame is refused and audited,
/// and the owner's channel keeps full ownership), and the scope
/// validation of every named scope. The scripted engine's
/// <see cref="ScriptedTbepEngine.Received"/> queue is the gate proof: a
/// refused caller never reaches the engine (only the host's own admin
/// channel binding, which predates every caller, is exempt).
/// </summary>
public sealed class GateTests
{
    private static string SocketPath() => Path.Combine(Path.GetTempPath(), $"se08-gate-{Guid.NewGuid():N}.sock");

    /// <summary>A caller without backend-supplied context is refused at the gate, and the engine hears nothing.</summary>
    [Test]
    public async Task MissingBackendContextIsRefusedAtTheLocalGate()
    {
        var path = SocketPath();
        await using var engine = new ScriptedTbepEngine(path);
        await using var host = await HostHarness.StartAsync(new EngineOptions { SocketPath = path });
        var framesBeforeAnyCaller = engine.Received.Count;

        foreach (var requestPath in new[] { "/v1/documents/orders/d-01", "/v1/engine/status", "/v1/sync/sessions" })
        {
            var (status, body) = await HostHarness.SendAsync(host.Http, requestPath);
            await Assert.That(status).IsEqualTo(401);
            await Assert.That(body.RootElement.GetProperty("code").GetString()).IsEqualTo("unauthorized");
        }

        // The gate proof: no engine frame was sent for any caller.
        await Assert.That(engine.Received.Count).IsEqualTo(framesBeforeAnyCaller);
        var refusals = host.Audit.Recent(16).Where(record => record.Kind == AuditKind.GateRefused).ToArray();
        await Assert.That(refusals.Length).IsEqualTo(3);
        await Assert.That(refusals.All(record => record.Outcome == AuditOutcome.Refused && record.Operation == "gate")).IsTrue();
    }

    /// <summary>Partial context (a principal without a tenant, or the reverse) is the same refusal.</summary>
    [Test]
    public async Task PartialBackendContextIsRefused()
    {
        var path = SocketPath();
        await using var engine = new ScriptedTbepEngine(path);
        await using var host = await HostHarness.StartAsync(new EngineOptions { SocketPath = path });
        var framesBeforeAnyCaller = engine.Received.Count;

        var principalOnly = host.As("backend-operator", tenant: string.Empty);
        var (status, _) = await HostHarness.SendAsync(principalOnly, "/v1/engine/status");
        await Assert.That(status).IsEqualTo(401);
        principalOnly.Dispose();

        var tenantOnly = new HttpClient { BaseAddress = host.Http.BaseAddress };
        tenantOnly.DefaultRequestHeaders.Add("X-TB-Tenant", "tenant-a");
        var (tenantStatus, _) = await HostHarness.SendAsync(tenantOnly, "/v1/engine/status");
        await Assert.That(tenantStatus).IsEqualTo(401);
        tenantOnly.Dispose();
        await Assert.That(engine.Received.Count).IsEqualTo(framesBeforeAnyCaller);
    }

    /// <summary>A gate-passing caller is served: the engine sees the bound tenant on every frame.</summary>
    [Test]
    public async Task PresentedBackendContextReachesTheEngineBound()
    {
        var path = SocketPath();
        await using var engine = new ScriptedTbepEngine(path);
        await using var host = await HostHarness.StartAsync(new EngineOptions { SocketPath = path });
        engine.Documents["tenant-a/orders/d-01"] = new DocumentBody
        {
            Id = "d-01",
            Collection = "orders",
            Version = 3,
            Body = TbepValue.Of("hello"),
        };

        var caller = host.As("backend-operator", "tenant-a");
        var (status, body) = await HostHarness.SendAsync(caller, "/v1/documents/orders/d-01");
        await Assert.That(status).IsEqualTo(200);
        await Assert.That(body.RootElement.GetProperty("found").GetBoolean()).IsTrue();
        await Assert.That(engine.Received.Last()).IsEqualTo("tenant-a:DocumentReadRequest");
    }

    /// <summary>A scope that does not validate is refused before any engine frame, and audited.</summary>
    [Test]
    public async Task InvalidScopesAreRefusedAndAudited()
    {
        var path = SocketPath();
        await using var engine = new ScriptedTbepEngine(path);
        await using var host = await HostHarness.StartAsync(new EngineOptions { SocketPath = path });
        var caller = host.As("backend-operator", "tenant-a");
        var before = engine.Received.Count;

        foreach (var (scope, document) in new[]
        {
            ("orders$", "d-01"),
            ("orders", "d-0 1"),
            ("orders#", "d-01"),
            (new string('x', 129), "d-01"),
            ("orders", "d-01!"),
        })
        {
            var (status, body) = await HostHarness.SendAsync(caller, $"/v1/documents/{Uri.EscapeDataString(scope)}/{Uri.EscapeDataString(document)}");
            await Assert.That(status).IsEqualTo(422);
            await Assert.That(body.RootElement.GetProperty("code").GetString()).IsEqualTo("scope-invalid");
        }

        // A sync session on a scope that does not validate is the same refusal.
        var (sessionStatus, sessionBody) = await HostHarness.SendAsync(caller, "/v1/sync/sessions", new { collection = "bad scope!" });
        await Assert.That(sessionStatus).IsEqualTo(422);
        await Assert.That(sessionBody.RootElement.GetProperty("code").GetString()).IsEqualTo("scope-invalid");
        await Assert.That(engine.Received.Count).IsEqualTo(before);
        await Assert.That(host.Audit.Recent(16).Count(record => record.Kind == AuditKind.ScopeRefused)).IsEqualTo(6);
    }

    /// <summary>
    /// The expected-version conditional rider (PD-2026-08-26-14 D1): the
    /// <c>X-TB-Expected</c> header maps onto every wire conditional —
    /// <c>any</c>, an exact version, and (absent or not a conditional)
    /// absent — on both the write and the delete path.
    /// </summary>
    [Test]
    public async Task TheExpectedVersionConditionalMapsEveryHeaderCase()
    {
        var path = SocketPath();
        await using var engine = new ScriptedTbepEngine(path);
        await using var host = await HostHarness.StartAsync(new EngineOptions { SocketPath = path });
        var caller = host.As("backend-operator", "tenant-a");
        var body = new { @string = "body" };

        // Every conditional case of the mapping, on the write path.
        var (any, _) = await HostHarness.SendAsync(caller, "/v1/documents/orders/e-any", body, idempotencyKey: "op-e-any", method: "PUT", expected: "any");
        await Assert.That(any).IsEqualTo(200);
        var (exact, _) = await HostHarness.SendAsync(caller, "/v1/documents/orders/e-exact", body, idempotencyKey: "op-e-exact", method: "PUT", expected: "7");
        await Assert.That(exact).IsEqualTo(200);
        var (absent, _) = await HostHarness.SendAsync(caller, "/v1/documents/orders/e-absent", body, idempotencyKey: "op-e-absent", method: "PUT");
        await Assert.That(absent).IsEqualTo(200);
        var (notAConditional, _) = await HostHarness.SendAsync(caller, "/v1/documents/orders/e-garbage", body, idempotencyKey: "op-e-garbage", method: "PUT", expected: "latest");
        await Assert.That(notAConditional).IsEqualTo(200);

        // And on the delete path: the exact conditional maps there too.
        var (delete, _) = await HostHarness.SendAsync(caller, "/v1/documents/orders/e-any", idempotencyKey: "op-e-delete", method: "DELETE", expected: "3");
        await Assert.That(delete).IsEqualTo(200);

        // The engine received exactly the mapped conditionals, in order.
        await Assert.That(engine.WriteConditionals.ToArray()).IsEquivalentTo(
        [
            "tenant-a:write:any",
            "tenant-a:write:exact:7",
            "tenant-a:write:absent",
            "tenant-a:write:absent",
            "tenant-a:delete:exact:3",
        ]);
    }

    /// <summary>Mutations without a valid idempotency key are refused at the host (the wire would refuse them too).</summary>
    [Test]
    public async Task MutationsRequireAValidIdempotencyKey()
    {
        var path = SocketPath();
        await using var engine = new ScriptedTbepEngine(path);
        await using var host = await HostHarness.StartAsync(new EngineOptions { SocketPath = path });
        var caller = host.As("backend-operator", "tenant-a");
        var before = engine.Received.Count;

        var body = new { @string = "body" };
        var (missing, missingBody) = await HostHarness.SendAsync(caller, "/v1/documents/orders/d-01", body, method: "PUT");
        await Assert.That(missing).IsEqualTo(400);
        await Assert.That(missingBody.RootElement.GetProperty("code").GetString()).IsEqualTo("idempotency-key-invalid");

        var (control, _) = await HostHarness.SendAsync(caller, "/v1/documents/orders/d-01", body, idempotencyKey: "bad\u0007key", method: "PUT");
        await Assert.That(control).IsEqualTo(400);
        var (oversize, _) = await HostHarness.SendAsync(caller, "/v1/documents/orders/d-01", body, idempotencyKey: new string('k', 129), method: "PUT");
        await Assert.That(oversize).IsEqualTo(400);
        await Assert.That(engine.Received.Count).IsEqualTo(before);
    }

    /// <summary>A cross-tenant subscription lifecycle frame is refused by the engine, audited by the host, and the owner keeps ownership.</summary>
    [Test]
    public async Task CrossTenantSubscriptionLifecycleIsRefusedAndAuditedAndTheOwnerKeepsOwnership()
    {
        var path = SocketPath();
        await using var engine = new ScriptedTbepEngine(path);
        await using var host = await HostHarness.StartAsync(new EngineOptions { SocketPath = path });
        var owner = host.As("backend-operator", "tenant-a");
        var intruder = host.As("backend-operator", "tenant-b");

        // The owner opens one stream and one session.
        await using var stream = await SseReader.OpenAsync(owner, string.Empty);
        var opened = await stream.ExpectAsync("opened");
        var subscriptionId = opened.Data.RootElement.GetProperty("subscriptionId").GetUInt64();
        var (sessionStatus, sessionBody) = await HostHarness.SendAsync(owner, "/v1/sync/sessions", new { collection = "orders" });
        await Assert.That(sessionStatus).IsEqualTo(200);
        var sessionId = sessionBody.RootElement.GetProperty("sessionId").GetUInt64();

        // The intruder's tenant channel tries to end both lifecycles.
        var (ackStatus, ackBody) = await HostHarness.SendAsync(intruder, $"/v1/streams/{subscriptionId}/ack", new { throughSequence = 1 });
        await Assert.That(ackStatus).IsEqualTo(403);
        await Assert.That(ackBody.RootElement.GetProperty("code").GetString()).IsEqualTo("tenant-refused");
        var (unsubscribeStatus, unsubscribeBody) = await HostHarness.SendAsync(intruder, $"/v1/streams/{subscriptionId}", method: "DELETE");
        await Assert.That(unsubscribeStatus).IsEqualTo(403);
        await Assert.That(unsubscribeBody.RootElement.GetProperty("code").GetString()).IsEqualTo("tenant-refused");
        var (deltaStatus, deltaBody) = await HostHarness.SendAsync(
            intruder,
            $"/v1/sync/sessions/{sessionId}/delta",
            new { from = Convert.ToBase64String([(byte)0, 0x6f, 0x70]) });
        await Assert.That(deltaStatus).IsEqualTo(403);
        await Assert.That(deltaBody.RootElement.GetProperty("code").GetString()).IsEqualTo("tenant-refused");

        // All three engine-enforced refusals are audited.
        await Assert.That(host.Audit.Recent(32).Count(record => record.Kind == AuditKind.TenantRefused)).IsEqualTo(3);

        // The owner's channel keeps full lifecycle ownership: the owner
        // unsubscribes its own stream and closes its own session.
        var (ownerUnsubscribe, _) = await HostHarness.SendAsync(owner, $"/v1/streams/{subscriptionId}", method: "DELETE");
        await Assert.That(ownerUnsubscribe).IsEqualTo(200);
        var (ownerClose, _) = await HostHarness.SendAsync(owner, $"/v1/sync/sessions/{sessionId}/close", method: "POST");
        await Assert.That(ownerClose).IsEqualTo(200);
    }

    /// <summary>The session registry answers only the caller's tenant.</summary>
    [Test]
    public async Task TheSessionRegistryIsTenantScoped()
    {
        var path = SocketPath();
        await using var engine = new ScriptedTbepEngine(path);
        await using var host = await HostHarness.StartAsync(new EngineOptions { SocketPath = path });
        var owner = host.As("backend-operator", "tenant-a");
        var other = host.As("backend-operator", "tenant-b");

        var (status, _) = await HostHarness.SendAsync(owner, "/v1/sync/sessions", new { collection = "orders" });
        await Assert.That(status).IsEqualTo(200);
        var (otherStatus, _) = await HostHarness.SendAsync(other, "/v1/sync/sessions", new { collection = "orders" });
        await Assert.That(otherStatus).IsEqualTo(200);

        var (listing, listingBody) = await HostHarness.SendAsync(owner, "/v1/sync/sessions");
        await Assert.That(listing).IsEqualTo(200);
        await Assert.That(listingBody.RootElement.EnumerateArray().Count()).IsEqualTo(1);
        await Assert.That(listingBody.RootElement.EnumerateArray().Single().GetProperty("collection").GetString()).IsEqualTo("orders");
    }

    /// <summary>Audit records carry lifecycle facts only — never a document body, never a credential.</summary>
    [Test]
    public async Task AuditRecordsCarryNoBodiesAndNoCredentials()
    {
        var path = SocketPath();
        await using var engine = new ScriptedTbepEngine(path);
        await using var host = await HostHarness.StartAsync(new EngineOptions { SocketPath = path });
        const string credential = "Bearer eyJhbGci-must-never-be-audited";
        const string bodyText = "order-body-must-not-be-audited";
        var caller = host.As("backend-operator", "tenant-a");
        caller.DefaultRequestHeaders.Add("Authorization", credential);

        // A document write whose body must never appear in the audit.
        var (write, _) = await HostHarness.SendAsync(
            caller,
            "/v1/documents/orders/d-01",
            new { @string = bodyText },
            idempotencyKey: "op-audit-01",
            method: "PUT");
        await Assert.That(write).IsEqualTo(200);

        // A mutation whose fields must never appear either.
        var (sessionStatus, sessionBody) = await HostHarness.SendAsync(caller, "/v1/sync/sessions", new { collection = "orders" });
        await Assert.That(sessionStatus).IsEqualTo(200);
        var sessionId = sessionBody.RootElement.GetProperty("sessionId").GetUInt64();
        var (mutation, _) = await HostHarness.SendAsync(
            caller,
            $"/v1/sync/sessions/{sessionId}/mutations",
            new
            {
                document = "d-02",
                change = new { put = new[] { new { name = "note", value = new { @string = bodyText } } } },
            },
            idempotencyKey: "op-audit-02");
        await Assert.That(mutation).IsEqualTo(200);

        // The audited surface (the JSON the endpoint serves) carries no
        // body text and no credential value.
        var (auditStatus, auditBody) = await HostHarness.SendAsync(caller, "/v1/audit?limit=1024");
        await Assert.That(auditStatus).IsEqualTo(200);
        var auditText = auditBody.RootElement.GetRawText();
        await Assert.That(auditText.Contains(bodyText, StringComparison.Ordinal)).IsFalse();
        await Assert.That(auditText.Contains(credential, StringComparison.Ordinal)).IsFalse();

        // The enumerable lifecycle facts are exactly what is recorded:
        // the backend-supplied principal, the operation, the outcome.
        var records = host.Audit.Recent(AuditLog.Capacity);
        await Assert.That(records).IsNotEmpty();
        var applied = records.Single(record => record.Kind == AuditKind.MutationApplied);
        await Assert.That(applied.Principal).IsEqualTo("backend-operator");
        await Assert.That(applied.Tenant).IsEqualTo("tenant-a");
        await Assert.That(applied.Operation).IsEqualTo("sync.mutation_submit");
        await Assert.That(applied.Outcome).IsEqualTo(AuditOutcome.Accepted);
        await Assert.That(applied.Detail.Length).IsLessThanOrEqualTo(AuditLog.MaxDetailLength);
    }

    /// <summary>The audit detail is byte-bounded even when the caller tries to smuggle length in.</summary>
    [Test]
    public async Task AuditDetailsAreByteBounded()
    {
        var path = SocketPath();
        await using var engine = new ScriptedTbepEngine(path);
        await using var host = await HostHarness.StartAsync(new EngineOptions { SocketPath = path });
        var intruder = host.As("backend-operator", "tenant-b");
        var owner = host.As("backend-operator", "tenant-a");

        var (sessionStatus, sessionBody) = await HostHarness.SendAsync(owner, "/v1/sync/sessions", new { collection = "orders" });
        await Assert.That(sessionStatus).IsEqualTo(200);
        var sessionId = sessionBody.RootElement.GetProperty("sessionId").GetUInt64();

        // A cross-tenant refusal whose engine detail is bounded on the
        // wire (256 bytes); the audit record stays bounded regardless.
        var (refused, _) = await HostHarness.SendAsync(intruder, $"/v1/sync/sessions/{sessionId}/close", method: "POST");
        await Assert.That(refused).IsEqualTo(403);
        var record = host.Audit.Recent(8).Single(entry => entry.Kind == AuditKind.TenantRefused);
        await Assert.That(record.Detail.Length).IsLessThanOrEqualTo(AuditLog.MaxDetailLength);
    }
}

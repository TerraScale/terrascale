using System.Globalization;
using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Features.Documents;

namespace TerraBase.Fast.Documents;

/// <summary><c>…/collections/{collection}/ttl</c> (docs/terrabase/collection-ttl.md): declared expiry and the home region's sweep.</summary>
public sealed class CollectionTtlTests
{
    private static readonly Faker Fake = new();

    [Test]
    [Arguments("2000-01-01", false)]
    [Arguments("2000-01-01T00:00:00", false)]
    [Arguments("01/01/2000", false)]
    [Arguments("01 Jan 2000", false)]
    [Arguments("2000-01-01 00:00:00Z", false)]
    [Arguments("2000-01-01T00:00:00+01:00", false)]
    [Arguments("2000-01-01T00:00:00-01:00", false)]
    [Arguments("2000-01-01T00:00:00-00:00", false)]
    [Arguments("2000-01-01T00:00:00.Z", false)]
    [Arguments("2000-02-30T00:00:00Z", false)]
    [Arguments("2000-01-01T00:00:00Z trailing", false)]
    [Arguments("2000-01-01T00:00:00Z", true)]
    [Arguments("2000-01-01T00:00:00+00:00", true)]
    [Arguments("2000-01-01T00:00:00.125Z", true)]
    [Arguments("2000-01-01T00:00:00.12345678Z", true)]
    [Arguments("2000-01-01T00:00:00.1234567+00:00", true)]
    [Arguments("2026-10-26t11:59:59z", true)]
    [Arguments("2026-10-26t11:59:59.125+00:00", true)]
    [Arguments("2026-10-26T12:00:00.5000000Z", false)]
    [Arguments("2026-10-26T12:00:00.5000001Z", false)]
    [Arguments("2099-01-01T00:00:00Z", false)]
    public async Task Only_expired_RFC3339_UTC_timestamps_are_deleted(string expiresAt, bool expired)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 26, 12, 0, 0, 500, TimeSpan.Zero));
        await using var api = await DocumentApi.StartAsync(time: clock);
        (await api.SendAsync(HttpMethod.Put, $"{api.Collections}/{DocumentApi.Collection}/ttl", new { path = "/expiresAt" })).Status.ShouldBe(HttpStatusCode.OK);
        var id = DocumentApi.NewId();
        (await api.PutAsync(id, new { expiresAt })).Status.ShouldBe(HttpStatusCode.Created);

        await api.Server.Services.GetRequiredService<CollectionTtlSweeper>().SweepAsync(CancellationToken.None);

        (await api.GetAsync(id)).Status.ShouldBe(expired ? HttpStatusCode.NotFound : HttpStatusCode.OK);
    }

    [Test]
    public async Task An_expired_document_is_swept_and_a_live_one_stays()
    {
        await using var api = await DocumentApi.StartAsync();
        var ttl = $"{api.Collections}/{DocumentApi.Collection}/ttl";
        var schema = $"{api.Collections}/{DocumentApi.Collection}/schema";
        (await api.SendRawAsync(HttpMethod.Put, schema, """{"type":"object"}""", "application/json")).Status.ShouldBe(HttpStatusCode.OK);
        await Assert.That((await api.SendAsync(HttpMethod.Get, ttl)).Json.GetProperty("path").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That((await api.SendAsync(HttpMethod.Put, ttl, new { path = "no-pointer" })).Code).IsEqualTo("invalid_ttl");
        var declared = await api.SendAsync(HttpMethod.Put, ttl, new { path = "/session/expiresAt" });
        declared.Status.ShouldBe(HttpStatusCode.OK);
        await Assert.That((await api.SendAsync(HttpMethod.Get, ttl)).Json.GetProperty("path").GetString()).IsEqualTo("/session/expiresAt");

        var now = DateTimeOffset.UtcNow;
        var expired = DocumentApi.NewId();
        var live = DocumentApi.NewId();
        var undated = DocumentApi.NewId();
        (await api.PutAsync(expired, new { name = Fake.Name.FirstName(), session = new { expiresAt = Stamp(now.AddSeconds(-1)) } })).Status.ShouldBe(HttpStatusCode.Created);
        (await api.PutAsync(live, new { name = Fake.Name.FirstName(), session = new { expiresAt = Stamp(now.AddHours(1)) } })).Status.ShouldBe(HttpStatusCode.Created);
        (await api.PutAsync(undated, new { name = Fake.Name.FirstName(), session = new { expiresAt = 0 } })).Status.ShouldBe(HttpStatusCode.Created);

        var sweeper = api.Server.Services.GetRequiredService<CollectionTtlSweeper>();
        await Assert.That(await sweeper.SweepAsync(CancellationToken.None)).IsEqualTo(1);

        (await api.GetAsync(expired)).Status.ShouldBe(HttpStatusCode.NotFound);
        (await api.GetAsync(live)).Status.ShouldBe(HttpStatusCode.OK);
        (await api.GetAsync(undated)).Status.ShouldBe(HttpStatusCode.OK);

        // Cleared, nothing expires; the attached schema survives both TTL writes.
        await Assert.That((await api.SendAsync(HttpMethod.Delete, ttl)).Json.GetProperty("path").ValueKind).IsEqualTo(JsonValueKind.Null);
        (await api.PutAsync(expired, new { session = new { expiresAt = Stamp(now.AddSeconds(-1)) } })).Status.ShouldBe(HttpStatusCode.Created);
        await Assert.That(await sweeper.SweepAsync(CancellationToken.None)).IsEqualTo(0);
        await Assert.That((await api.SendAsync(HttpMethod.Get, schema)).Json.GetProperty("schema").GetProperty("type").GetString()).IsEqualTo("object");
    }

    private static string Stamp(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraBase.Features.Regions;
using TerraBase.Testing;
using static TerraBase.Testing.SqlNativeDatabase;

namespace TerraBase.Fast.Sql;

public sealed class SqlSocketJourneyTests
{
    [Test]
    public async Task A_project_token_creates_reads_updates_and_deletes_rows_over_websocket()
    {
        await using var database = await StartSharedAsync();
        await using var socket = await database.BrowserSocketAsync();
        var admitted = await socket.AuthAsync(database.Api.ProjectToken(database.Database.Project, "sql:read sql:write"));
        var name = new Faker().Name.FirstName();
        var answer = await socket.ExecuteAsync(
            Stmt("CREATE TABLE notes (id INTEGER PRIMARY KEY, name TEXT NOT NULL)"),
            Stmt("INSERT INTO notes VALUES (?1, ?2)", 1L, name),
            Stmt("SELECT name FROM notes WHERE id = ?1", 1L),
            Stmt("UPDATE notes SET name = ?1 WHERE id = ?2", "updated", 1L),
            Stmt("DELETE FROM notes WHERE id = ?1", 1L),
            Stmt("SELECT count(*) FROM notes"));

        await Assert.That(admitted.GetProperty("type").GetString()).IsEqualTo("result");
        await Assert.That(answer.GetProperty("type").GetString()).IsEqualTo("result");
        var results = answer.GetProperty("results");
        await Assert.That(results[2].GetProperty("rows")[0][0].GetString()).IsEqualTo(name);
        await Assert.That(results[3].GetProperty("rows_affected").GetInt64()).IsEqualTo(1);
        await Assert.That(results[4].GetProperty("rows_affected").GetInt64()).IsEqualTo(1);
        await Assert.That(results[5].GetProperty("rows")[0][0].GetInt64()).IsEqualTo(0);
    }

    [Test]
    public async Task A_header_authenticated_upgrade_for_a_database_homed_elsewhere_answers_wrong_region_naming_its_home()
    {
        using var issuer = new TestIssuer();
        var mesh = new Mesh();
        await using var gru = await StartAsync("gru", issuer, mesh);
        await using var fra = await StartAsync("fra", issuer, mesh);
        mesh.Peers = [new RegionPeer("gru", gru.Server.BaseAddress), new RegionPeer("fra", fra.Server.BaseAddress)];
        var database = await gru.CreateManagedDatabaseAsync();
        var token = gru.ProjectToken(database.Project, "sql:read sql:write");

        using var client = fra.DataClient();
        using var upgrade = new HttpRequestMessage(HttpMethod.Get, ManagementApi.Uri(database.Data + "/sql/ws"))
        {
            Version = HttpVersion.Version11,
        };
        upgrade.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        upgrade.Headers.TryAddWithoutValidation("Connection", "Upgrade");
        upgrade.Headers.TryAddWithoutValidation("Upgrade", "websocket");
        upgrade.Headers.TryAddWithoutValidation("Sec-WebSocket-Key", "dGhlIHNhbXBsZSBub25jZQ==");
        upgrade.Headers.TryAddWithoutValidation("Sec-WebSocket-Version", "13");
        upgrade.Headers.TryAddWithoutValidation("Sec-WebSocket-Protocol", SqlSocketClient.Protocol);
        using var misdirected = await client.SendAsync(upgrade);
        var refused = await misdirected.Content.ReadFromJsonAsync<JsonElement>();

        await Assert.That(misdirected.StatusCode).IsEqualTo(HttpStatusCode.MisdirectedRequest).Because(refused.GetRawText());
        await Assert.That(refused.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("wrong_region");
        await Assert.That(refused.GetProperty("error").GetProperty("url").GetString()).Contains("gru.api.terrascale.tech");
        await Assert.That(misdirected.Headers.GetValues("X-TerraBase-Database-Home")).IsEquivalentTo(["gru"]);

        await using var home = await SqlSocketClient.ConnectAsync(gru, database.Data, token);
        var answer = await home.ExecuteAsync(Stmt("SELECT 1"));
        await Assert.That(answer.GetProperty("type").GetString()).IsEqualTo("result");
        await Assert.That(answer.GetProperty("results")[0].GetProperty("rows")[0][0].GetInt64()).IsEqualTo(1);
    }

    private static Task<ManagementApi> StartAsync(string region, TestIssuer issuer, Mesh mesh) => ManagementApi.StartAsync(
        settings => settings["region"] = region,
        services => services.Replace(ServiceDescriptor.Singleton<IRegionPeers>(new RegionView(region, mesh))),
        sharedIssuer: issuer);

    private sealed class Mesh
    {
        public RegionPeer[] Peers { get; set; } = [];
    }

    private sealed class RegionView(string region, Mesh mesh) : IRegionPeers
    {
        public ValueTask<IReadOnlyList<RegionPeer>> OtherRegionsAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<RegionPeer>>([.. mesh.Peers.Where(peer => peer.Region != region)]);
    }
}

using System.Net;
using System.Net.Http.Json;
using Microsoft.IdentityModel.Tokens;
using TerraBase.Primitives;
using TerraBase.Testing;
using static TerraBase.Testing.SqlNativeDatabase;

namespace TerraBase.Fast.Shapes;

public sealed class ShapeGrantTests
{
    private static readonly string[] Granted = ["id", "owner_id", "body"];

    private const string Notes = "CREATE TABLE notes (id INTEGER PRIMARY KEY, owner_id TEXT NOT NULL, body TEXT, secret TEXT)";

    [Test]
    public async Task An_end_user_reads_only_their_own_rows_and_the_granted_columns()
    {
        await using var db = await StartSharedAsync();
        var (alice, bob) = (EntityIds.New().ToString(), EntityIds.New().ToString());
        await db.OkAsync(Notes);
        await db.OkAsync(Stmt("INSERT INTO notes (id, owner_id, body, secret) VALUES (1, ?, 'a1', 's'), (2, ?, 'b1', 's'), (3, ?, 'a2', 's')", alice, bob, alice));
        await GrantAsync(db, "notes", new { where = "owner_id = :sub", columns = Granted });

        using var browser = db.Api.WithToken(EndUser(db, alice, "notes"));
        var shape = new TableShapeClient(browser, db.Database.Data, "notes");
        await shape.SyncAsync();
        await Assert.That(shape.State.Keys.Order()).IsEquivalentTo(["1", "3"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(shape.State.Values.All(row => !row.ContainsKey("secret") && row["owner_id"]!.GetValue<string>() == alice)).IsTrue();

        await db.OkAsync(Stmt("INSERT INTO notes (id, owner_id, body) VALUES (4, ?, 'b2'), (5, ?, 'a3')", bob, alice));
        await shape.CatchUpAsync();
        await Assert.That(shape.State.Keys.Order()).IsEquivalentTo(["1", "3", "5"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Ungranted_tables_and_expired_end_user_tokens_are_refused()
    {
        await using var db = await StartSharedAsync();
        var alice = EntityIds.New().ToString();
        await db.OkAsync(Notes, "CREATE TABLE audit (id INTEGER PRIMARY KEY, entry TEXT)");
        await GrantAsync(db, "notes", new { where = "owner_id = :sub" });

        using var browser = db.Api.WithToken(EndUser(db, alice, "notes", "audit"));
        using var ungranted = await browser.GetAsync(ManagementApi.Uri($"{db.Database.Data}/tables/audit/shape?offset=-1"));
        await Assert.That(ungranted.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);

        using var narrow = db.Api.WithToken(EndUser(db, alice, "audit"));
        using var outsideToken = await narrow.GetAsync(ManagementApi.Uri($"{db.Database.Data}/tables/notes/shape?offset=-1"));
        await Assert.That(outsideToken.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);

        var now = DateTime.UtcNow;
        using var stale = db.Api.WithToken(EndUser(db, alice, ["notes"], token =>
        {
            token.IssuedAt = now.AddMinutes(-20);
            token.NotBefore = now.AddMinutes(-20);
            token.Expires = now.AddMinutes(-2);
        }));
        using var expired = await stale.GetAsync(ManagementApi.Uri($"{db.Database.Data}/tables/notes/shape?offset=-1"));
        await Assert.That(expired.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);

        using var allowed = await browser.GetAsync(ManagementApi.Uri($"{db.Database.Data}/tables/notes/shape?offset=-1"));
        await Assert.That(allowed.StatusCode).IsEqualTo(HttpStatusCode.TemporaryRedirect);
    }

    private static async Task GrantAsync(SqlNativeDatabase db, string table, object grant)
    {
        using var owner = db.Api.WithToken(db.Api.Issuer.Mint(db.Api.User, token =>
        {
            token.Claims["project_id"] = db.Database.Project;
            token.Claims["scope"] = "databases:read databases:write";
        }));
        using var response = await owner.PutAsJsonAsync(ManagementApi.Uri($"{db.Database.Data}/tables/{table}/read-grant"), grant);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await response.Content.ReadAsStringAsync());
    }

    private static string EndUser(SqlNativeDatabase db, string subject, params string[] tables) => EndUser(db, subject, tables, null);

    private static string EndUser(SqlNativeDatabase db, string subject, string[] tables, Action<SecurityTokenDescriptor>? edit) =>
        db.Api.Issuer.Mint(subject, token =>
        {
            token.Claims["token_kind"] = "end_user";
            token.Claims["project_id"] = db.Database.Project;
            token.Claims["instance_id"] = db.Database.Project;
            token.Claims["database"] = db.Database.Id;
            token.Claims["roles"] = Array.Empty<string>();
            token.Claims["tables"] = tables;
            edit?.Invoke(token);
        });
}

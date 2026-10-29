using Microsoft.Extensions.DependencyInjection;
using OtpNet;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Mfa;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Authentication.Fast.Identity;

[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class PasscodeTests(TerraBaseFixture fixture)
{
    private const string Email = "alice@example.test";




    [Test]
    public async Task Only_a_master_keyed_hmac_of_the_code_is_stored()
    {
        var (tenant, database) = await fixture.CreateDatabaseAsync();
        var clock = new StoreTestClock();
        var options = AuthOptions.Load(AuthTestServer.DefaultSettings());
        var (users, _, passcodes) = this.Stack(tenant, clock, options);
        await users.CreateAsync("instance", Email);

        var issued = (await passcodes.RequestAsync("instance", "  Alice@Example.TEST "))!;
        await Assert.That(issued.Email).IsEqualTo(Email);
        await Assert.That(issued.Code.Length).IsEqualTo(6);
        await Assert.That(issued.Code.All(char.IsAsciiDigit)).IsTrue();
        await Assert.That(issued.ExpiresAt).IsEqualTo(clock.UtcNow + Passcodes.Lifetime);
        var stored = (await database.ListDocumentsAsync("passcodes")).Items.Single().Document;
        var codeHash = stored.GetProperty("codeHash").GetString()!;
        await Assert.That(codeHash.Length).IsEqualTo(64);
        await Assert.That(codeHash.All(char.IsAsciiHexDigitLower)).IsTrue();
        await Assert.That(codeHash).IsNotEqualTo(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(issued.Code))));
        await Assert.That(stored.EnumerateObject().Any(field => field.Value.ValueKind == JsonValueKind.String ? field.Value.GetString() == issued.Code : field.Value.GetRawText() == issued.Code)).IsFalse();

        var (_, _, otherKey) = this.Stack(tenant, clock, AuthOptions.Load(AuthTestServer.DefaultSettings()));
        await Assert.That(await otherKey.VerifyAsync("instance", Email, issued.Code)).IsNull();
        await Assert.That(await this.Stack(tenant, clock, options).Passcodes.VerifyAsync("instance", Email, issued.Code)).IsNotNull();
    }

    [Test]
    public async Task A_code_signs_in_once_and_a_reissue_kills_every_earlier_code()
    {
        var (tenant, database) = await fixture.CreateDatabaseAsync();
        var options = AuthOptions.Load(AuthTestServer.DefaultSettings());
        var (users, sessions, passcodes) = this.Stack(tenant, TimeProvider.System, options);
        var user = (await users.CreateAsync("instance", Email))!;

        var earlier = (await passcodes.RequestAsync("instance", Email))!;
        var current = (await passcodes.RequestAsync("instance", Email))!;
        var latest = (await passcodes.RequestAsync("instance", Email))!;
        await Assert.That((await database.ListDocumentsAsync("passcodes")).Items.Count).IsEqualTo(1);
        // A superseded code never signs in (QA round 4, auth:5); equal codes (one in a million) would be indistinguishable.
        foreach (var superseded in new[] { earlier.Code, current.Code }.Where(code => code != latest.Code))
        {
            await Assert.That(await passcodes.VerifyAsync("instance", Email, superseded)).IsNull();
        }

        var signedIn = await this.Stack(tenant, TimeProvider.System, options).Passcodes.VerifyAsync("instance", "ALICE@example.test", $" {latest.Code} ");
        await Assert.That(signedIn).IsNotNull();
        await Assert.That((await sessions.ResolveAsync("instance", signedIn!.Token))!.Value.User.Id).IsEqualTo(user.Id);
        await Assert.That(await passcodes.VerifyAsync("instance", Email, latest.Code)).IsNull();
        var stored = (await database.ListDocumentsAsync("passcodes")).Items.Single().Document;
        await Assert.That(stored.GetProperty("consumed").GetBoolean()).IsTrue();
        await Assert.That((await database.ListDocumentsAsync("sessions")).Items.Count).IsEqualTo(1);
    }



    [Test]
    [Arguments(0, false)]
    public async Task Codes_expire_at_the_exact_boundary(int secondsFromExpiry, bool works)
    {
        var (tenant, database) = await fixture.CreateDatabaseAsync();
        var clock = new StoreTestClock();
        var (users, _, passcodes) = this.Stack(tenant, clock, AuthOptions.Load(AuthTestServer.DefaultSettings()));
        await users.CreateAsync("instance", Email);
        var issued = (await passcodes.RequestAsync("instance", Email))!;
        clock.UtcNow = issued.ExpiresAt.AddSeconds(secondsFromExpiry);

        await Assert.That(await passcodes.VerifyAsync("instance", Email, issued.Code) is not null).IsEqualTo(works);
        var stored = (await database.ListDocumentsAsync("passcodes")).Items.Single().Document;
        await Assert.That(stored.GetProperty("consumed").GetBoolean()).IsEqualTo(works);
        await Assert.That(stored.GetProperty("failedAttempts").GetInt32()).IsEqualTo(0);
    }








    [Test]
    public async Task Unknown_emails_write_nothing_and_never_verify()
    {
        var (tenant, database) = await fixture.CreateDatabaseAsync();
        var (users, _, passcodes) = this.Stack(tenant, TimeProvider.System, AuthOptions.Load(AuthTestServer.DefaultSettings()));
        await users.CreateAsync("instance", Email);

        await Assert.That(await passcodes.RequestAsync("instance", "nobody@example.test")).IsNull();
        await Assert.That(await passcodes.RequestAsync("other-instance", Email)).IsNull();
        await Assert.That(await passcodes.RequestAsync("instance", "not-an-email")).IsNull();
        await Assert.That((await database.ListDocumentsAsync("passcodes")).Items.Count).IsEqualTo(0);
        await Assert.That(await passcodes.VerifyAsync("instance", "nobody@example.test", "123456")).IsNull();
        await Assert.That((await database.ListDocumentsAsync("sessions")).Items.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Codes_are_bound_to_their_user_and_instance()
    {
        var (tenant, database) = await fixture.CreateDatabaseAsync();
        var (users, _, passcodes) = this.Stack(tenant, TimeProvider.System, AuthOptions.Load(AuthTestServer.DefaultSettings()));
        var alice = (await users.CreateAsync("instance-a", Email))!;
        await users.CreateAsync("instance-a", "bob@example.test");
        await users.CreateAsync("instance-b", Email);
        var aliceCode = (await passcodes.RequestAsync("instance-a", Email))!.Code;
        var bobCode = (await passcodes.RequestAsync("instance-a", "bob@example.test"))!.Code;
        var elsewhereCode = (await passcodes.RequestAsync("instance-b", Email))!.Code;

        // Equal random codes (one in a million) are legitimately valid for both accounts.
        if (aliceCode != bobCode)
        {
            await Assert.That(await passcodes.VerifyAsync("instance-a", "bob@example.test", aliceCode)).IsNull();
        }

        if (aliceCode != elsewhereCode)
        {
            await Assert.That(await passcodes.VerifyAsync("instance-b", Email, aliceCode)).IsNull();
        }

        var signedIn = await passcodes.VerifyAsync("instance-a", Email, aliceCode);
        await Assert.That(signedIn!.Session.UserId).IsEqualTo(alice.Id);
        await Assert.That(signedIn.Session.InstanceId).IsEqualTo("instance-a");
        await Assert.That((await database.ListDocumentsAsync("sessions")).Items.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Revoking_all_sessions_voids_the_outstanding_code()
    {
        var (tenant, database) = await fixture.CreateDatabaseAsync();
        var (users, sessions, passcodes) = this.Stack(tenant, TimeProvider.System, AuthOptions.Load(AuthTestServer.DefaultSettings()));
        var user = (await users.CreateAsync("instance", Email))!;
        var issued = (await passcodes.RequestAsync("instance", Email))!;

        await Assert.That(await sessions.RevokeAllAsync("instance", user.Id)).IsTrue();
        await Assert.That(await passcodes.VerifyAsync("instance", Email, issued.Code)).IsNull();
        await Assert.That((await database.ListDocumentsAsync("sessions")).Items.Count).IsEqualTo(0);
        var reissued = (await passcodes.RequestAsync("instance", Email))!;
        await Assert.That((await passcodes.VerifyAsync("instance", Email, reissued.Code))!.Session.AuthEpoch).IsEqualTo(1);
    }










    private (Users Users, Sessions Sessions, Passcodes Passcodes) Stack(TerraBaseTestTenant tenant, TimeProvider clock, AuthOptions options)
    {
        var store = new AuthStore(fixture.Server.Documents(tenant.ApiKey).Database(tenant.Project.Id, "auth"), clock);
        var users = new Users(store, clock);
        var totp = new TotpCredentials(store, users, new AttemptLimiter(new TerraBaseDocumentStore(store), clock, options), options, clock);
        var sessions = new Sessions(store, users, totp, clock);
        return (users, sessions, new Passcodes(store, users, sessions, clock, options));
    }
}

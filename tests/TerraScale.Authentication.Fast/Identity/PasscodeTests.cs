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
    public async Task A_code_signs_in_once_and_a_reissue_kills_every_earlier_code()
    {
        var (tenant, database) = await fixture.CreateDatabaseAsync();
        var options = AuthOptions.Load(AuthTestServer.DefaultSettings());
        var (users, sessions, passcodes) = this.Stack(tenant, TimeProvider.System, options);
        var user = (await users.CreateAsync("instance", Email))!;

        var earlier = (await passcodes.RequestAsync("instance", Email))!;
        var current = (await passcodes.RequestAsync("instance", Email))!;
        var latest = (await passcodes.RequestAsync("instance", Email))!;
        await Assert.That((await database.RowsAsync("passcodes")).Count).IsEqualTo(1);
        // A superseded code never signs in (QA round 4, auth:5); equal codes (one in a million) would be indistinguishable.
        foreach (var superseded in new[] { earlier.Code, current.Code }.Where(code => code != latest.Code))
        {
            await Assert.That(await passcodes.VerifyAsync("instance", Email, superseded)).IsNull();
        }

        var signedIn = await this.Stack(tenant, TimeProvider.System, options).Passcodes.VerifyAsync("instance", "ALICE@example.test", $" {latest.Code} ");
        await Assert.That(signedIn).IsNotNull();
        await Assert.That((await sessions.ResolveAsync("instance", signedIn!.Token))!.Value.User.Id).IsEqualTo(user.Id);
        await Assert.That(await passcodes.VerifyAsync("instance", Email, latest.Code)).IsNull();
        var stored = (await database.RowsAsync("passcodes")).Single().Document;
        await Assert.That(stored.GetProperty("consumed").GetBoolean()).IsTrue();
        await Assert.That((await database.RowsAsync("sessions")).Count).IsEqualTo(1);
    }

    private (Users Users, Sessions Sessions, Passcodes Passcodes) Stack(TerraBaseTestTenant tenant, TimeProvider clock, AuthOptions options)
    {
        var store = new AuthStore(fixture.Server.Sql(tenant.ApiKey).Database(tenant.Project.Id, "auth"), clock);
        var users = new Users(store, clock);
        var totp = new TotpCredentials(store, users, new AttemptLimiter(new TerraBaseDocumentStore(store), clock, options), options, clock);
        var sessions = new Sessions(store, users, totp, clock);
        return (users, sessions, new Passcodes(store, users, sessions, clock, options));
    }
}

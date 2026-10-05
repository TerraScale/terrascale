using System.Globalization;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Features.Identity;
using TerraScale.Auth.Store;
using TerraScale.Auth.Tests;
using TerraScale.DotNet.Testing;

namespace TerraScale.Auth.Slow.Identity;

[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class IdentityConcurrencyTests(TerraBaseFixture fixture)
{
    private const string Email = "alice@example.test";

    [Test]
    public async Task Every_issued_code_has_exactly_one_redemption_across_rounds()
    {
        var (tenant, database) = await fixture.CreateDatabaseAsync();
        var options = AuthOptions.Load(AuthTestServer.DefaultSettings());
        var (users, sessions, passcodes) = this.Stack(tenant, options);
        await users.CreateAsync("instance", Email);

        for (var round = 1; round <= 3; round++)
        {
            var issued = (await passcodes.RequestAsync("instance", Email))!;
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var attempts = Enumerable.Range(0, 32).Select(async _ =>
            {
                var independent = this.Stack(tenant, options).Passcodes;
                await start.Task;
                return await independent.VerifyAsync("instance", Email, issued.Code);
            }).ToArray();
            start.SetResult();
            var results = await Task.WhenAll(attempts);

            await Assert.That(results.Count(result => result is not null)).IsEqualTo(1);
            await Assert.That(await sessions.ResolveAsync("instance", results.Single(result => result is not null)!.Token)).IsNotNull();
            await Assert.That((await database.ListDocumentsAsync("sessions")).Items.Count).IsEqualTo(round);
            await Assert.That(await passcodes.VerifyAsync("instance", Email, issued.Code)).IsNull();
        }
    }

    [Test]
    public async Task A_correct_code_racing_wrong_guesses_wins_at_most_once_and_only_within_the_attempt_limit()
    {
        var (tenant, database) = await fixture.CreateDatabaseAsync();
        var options = AuthOptions.Load(AuthTestServer.DefaultSettings());
        var (users, _, passcodes) = this.Stack(tenant, options);
        await users.CreateAsync("instance", Email);
        var issued = (await passcodes.RequestAsync("instance", Email))!;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 64).Select(async guess =>
        {
            var independent = this.Stack(tenant, options).Passcodes;
            var code = guess == 32
                ? issued.Code
                : ((int.Parse(issued.Code, CultureInfo.InvariantCulture) + guess + 1) % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
            await start.Task;
            return await independent.VerifyAsync("instance", Email, code);
        }).ToArray();
        start.SetResult();
        var successes = (await Task.WhenAll(attempts)).Count(result => result is not null);

        var stored = (await database.ListDocumentsAsync("passcodes")).Items.Single().Document;
        var failedAttempts = stored.GetProperty("failedAttempts").GetInt32();
        var consumed = stored.GetProperty("consumed").GetBoolean();
        await Assert.That(successes).IsLessThanOrEqualTo(1);
        await Assert.That((await database.ListDocumentsAsync("sessions")).Items.Count).IsEqualTo(successes);
        if (successes == 1)
        {
            await Assert.That(consumed).IsTrue();
            await Assert.That(failedAttempts).IsLessThanOrEqualTo(Passcodes.MaxAttempts - 1);
        }
        else
        {
            await Assert.That(consumed).IsFalse();
            await Assert.That(failedAttempts).IsEqualTo(Passcodes.MaxAttempts);
        }

        await Assert.That(await passcodes.VerifyAsync("instance", Email, issued.Code)).IsNull();
    }

    [Test]
    public async Task Concurrent_account_creation_from_independent_stores_has_one_owner()
    {
        var (tenant, database) = await fixture.CreateDatabaseAsync();
        var options = AuthOptions.Load(AuthTestServer.DefaultSettings());
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 32).Select(async index =>
        {
            var users = this.Stack(tenant, options).Users;
            await start.Task;
            return await users.CreateAsync("instance", index % 2 == 0 ? Email : " ALICE@example.TEST ");
        }).ToArray();
        start.SetResult();
        var results = await Task.WhenAll(attempts);

        await Assert.That(results.Count(user => user is not null)).IsEqualTo(1);
        await Assert.That((await database.ListDocumentsAsync("users")).Items.Count).IsEqualTo(1);
        await Assert.That((await database.ListDocumentsAsync("uniques")).Items.Count).IsEqualTo(1);
        var owner = await this.Stack(tenant, options).Users.FindByEmailAsync("instance", Email);
        await Assert.That(owner!.Id).IsEqualTo(results.Single(user => user is not null)!.Id);
    }

    private (Users Users, Sessions Sessions, Passcodes Passcodes) Stack(TerraBaseTestTenant tenant, AuthOptions options)
    {
        var store = new AuthStore(fixture.Server.Documents(tenant.ApiKey).Database(tenant.Project.Id, "auth"), TimeProvider.System);
        var users = new Users(store, TimeProvider.System);
        var sessions = new Sessions(store, users, TimeProvider.System);
        return (users, sessions, new Passcodes(store, users, sessions, TimeProvider.System, options));
    }
}

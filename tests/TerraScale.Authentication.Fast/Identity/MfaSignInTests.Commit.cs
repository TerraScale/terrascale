using System.Net;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Authentication.Fast.Identity;

public sealed partial class MfaSignInTests
{
    private const string MfaPath = "/ui/terrascale/sign-in/mfa";

    [Test]
    public async Task A_completed_challenge_opens_no_second_session_on_any_replica_and_spends_no_further_proof()
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        await using var replica = await oidc.StartReplicaAsync(clock: clock);
        var user = await oidc.CreateUserAsync("dana@example.test");
        var (secret, codes, _) = await EnrolAsync(oidc, await oidc.SignInAsync(user.Email), clock);
        clock.UtcNow += HostedAuth.MailCooldown;
        var challenge = await ChallengeAsync(oidc, user.Email);

        // A refused proof leaves the challenge open.
        using (var wrong = await oidc.PostFormAsync(MfaPath, new() { ["challenge"] = challenge, ["code"] = WrongCode(secret, clock) }))
        {
            await Assert.That(wrong.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
            await Assert.That(await wrong.Content.ReadAsStringAsync()).Contains("That code is wrong or already used");
        }

        using (var first = await oidc.PostFormAsync(MfaPath, new() { ["challenge"] = challenge, ["code"] = codes[0] }))
        {
            await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(OidcHarness.SessionCookie(first)).IsNotNull();
        }

        // A newly started auth instance sees the durable completion too.
        await using var restarted = await oidc.StartReplicaAsync(clock: clock);
        // Another unused recovery code or TOTP step cannot reopen it on any of these instances.
        foreach (var (client, code) in new[] { (oidc.Client, codes[1]), (replica.Client, Code(secret, clock)), (restarted.Client, codes[2]) })
        {
            using var again = await oidc.PostFormAsync(MfaPath, new() { ["challenge"] = challenge, ["code"] = code }, client: client);
            await Assert.That(again.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
            await Assert.That(OidcHarness.SessionCookie(again)).IsNull();
            await Assert.That(await again.Content.ReadAsStringAsync()).Contains("Sign-in expired");
        }

        clock.UtcNow += HostedAuth.MailCooldown;
        using var fresh = await oidc.PostFormAsync(MfaPath, new() { ["challenge"] = await ChallengeAsync(oidc, user.Email), ["code"] = codes[1] });
        await Assert.That(fresh.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Racing_completions_of_one_challenge_with_distinct_proofs_commit_once_and_the_loser_keeps_its_proof()
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        // Holds the losing request after the challenge check passed and before its proof read and commit.
        var pause = new PausingTransactions(MfaRead) { InnerHandler = TerraBaseFixture.NewHandler() };
        using var http = new HttpClient(pause);
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock, services: services => services.AddSingleton(http));
        await using var replica = await oidc.StartReplicaAsync(clock: clock);
        var user = await oidc.CreateUserAsync("erin@example.test");
        var (_, codes, _) = await EnrolAsync(oidc, await oidc.SignInAsync(user.Email), clock);
        clock.UtcNow += HostedAuth.MailCooldown;
        var challenge = await ChallengeAsync(oidc, user.Email);

        var held = await pause.HoldNextAsync(() => oidc.PostFormAsync(MfaPath, new() { ["challenge"] = challenge, ["code"] = codes[0] }));
        HttpResponseMessage late;
        try
        {
            using var winner = await oidc.PostFormAsync(MfaPath, new() { ["challenge"] = challenge, ["code"] = codes[1] }, client: replica.Client);
            await Assert.That(winner.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(OidcHarness.SessionCookie(winner)).IsNotNull();
        }
        finally
        {
            late = await pause.ReleaseAsync(held);
        }

        using (late)
        {
            await Assert.That(late.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
            await Assert.That(OidcHarness.SessionCookie(late)).IsNull();
        }

        clock.UtcNow += HostedAuth.MailCooldown;
        using var fresh = await oidc.PostFormAsync(MfaPath, new() { ["challenge"] = await ChallengeAsync(oidc, user.Email), ["code"] = codes[0] });
        await Assert.That(fresh.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    [Arguments("begin")]
    [Arguments("confirm")]
    [Arguments("disable")]
    public async Task Signing_the_caller_session_out_before_a_factor_change_commits_refuses_the_change(string transition)
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        // Holds the request after its session was admitted and before the factor change commits.
        var pause = new PausingTransactions(MfaRead) { InnerHandler = TerraBaseFixture.NewHandler() };
        using var http = new HttpClient(pause);
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock, services: services => services.AddSingleton(http));
        await using var replica = await oidc.StartReplicaAsync(clock: clock);
        var user = await oidc.CreateUserAsync($"{transition}@example.test");
        var session = await oidc.SignInAsync(user.Email);
        var path = "/ui/terrascale/security/totp";
        var form = new Dictionary<string, string>();
        if (transition == "confirm")
        {
            using var begin = await SendAsync(oidc, HttpMethod.Post, path, session, []);
            path += "/confirm";
            form["code"] = Code(SecretPattern().Match(await begin.Content.ReadAsStringAsync()).Groups[1].Value, clock);
        }
        else if (transition == "disable")
        {
            var (_, codes, enrolled) = await EnrolAsync(oidc, session, clock);
            session = enrolled;
            path += "/disable";
            form["code"] = codes[0];
        }

        var before = await SnapshotAsync(oidc);
        var held = await pause.HoldNextAsync(() => SendAsync(oidc, HttpMethod.Post, path, session, form));
        HttpResponseMessage response;
        try
        {
            await Assert.That(await replica.App.Services.GetRequiredService<Sessions>().RevokeAsync(user.InstanceId, session)).IsTrue();
        }
        finally
        {
            response = await pause.ReleaseAsync(held);
        }

        using (response)
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
            await Assert.That(OidcHarness.SessionCookie(response)).IsNull();
        }

        // No factor, recovery, epoch or session change committed, and no replacement session exists.
        var after = await SnapshotAsync(oidc);
        await Assert.That(after.Factors).IsEqualTo(before.Factors);
        await Assert.That(after.Users).IsEqualTo(before.Users);
        await Assert.That(after.Sessions).IsEquivalentTo(before.Sessions);
    }

    private static bool MfaRead(HttpRequestMessage request) => request.Method == HttpMethod.Get
        && request.RequestUri!.AbsolutePath.Contains($"/collections/{AuthStore.MfaCollection}/documents/", StringComparison.Ordinal);

    private static async Task<(string Factors, string Users, string[] Sessions)> SnapshotAsync(OidcHarness oidc)
    {
        static string Join(IEnumerable<TerraBaseDocument> items) =>
            string.Join('\n', items.OrderBy(item => item.Id, StringComparer.Ordinal).Select(item => $"{item.Id} {item.Version} {item.Document.GetRawText()}"));
        return (Join((await oidc.Database.ListDocumentsAsync(AuthStore.MfaCollection)).Items),
            Join((await oidc.Database.ListDocumentsAsync(AuthStore.UserCollection)).Items),
            [.. (await oidc.Database.ListDocumentsAsync(AuthStore.SessionCollection)).Items.Select(item => item.Id)]);
    }
}

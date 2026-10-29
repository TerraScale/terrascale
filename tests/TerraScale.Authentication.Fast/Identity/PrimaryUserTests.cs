using System.Globalization;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using OtpNet;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Mfa;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Identity;

/// <summary>
/// The per-request checks every bearer-authenticated call makes: the user at the token's epoch, read after its factor and
/// refused while one is active, with the sign-in and token entry read alongside instead of one after the other.
/// </summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class PrimaryUserTests(TerraBaseFixture fixture)
{
    private const string Instance = "instance";

    [Test]
    public async Task A_platform_call_checks_the_sign_in_and_token_entry_alongside_the_factor()
    {
        var pairing = new PairingHandler();
        using var http = new HttpClient(pairing);
        await using var oidc = await OidcHarness.StartAsync(fixture, services: services => services.AddSingleton(http));
        var (_, token) = await oidc.SignInUserAsync("carol@example.test");

        pairing.Armed = true;
        using var response = await oidc.CallAsync(HttpMethod.Get, PlatformCalls.Organizations, token);
        pairing.Armed = false;
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        // The factor, sign-in and token entry reads are in flight together; read one after the other, never more than one is.
        await Assert.That(pairing.MostInFlight).IsGreaterThanOrEqualTo(3);
    }

    [Test]
    public async Task Only_the_current_epoch_of_an_existing_user_passes_and_an_active_factor_needs_its_proof()
    {
        var (_, database) = await fixture.CreateDatabaseAsync();
        var clock = new StoreTestClock();
        var store = new AuthStore(database, clock);
        var user = (await new Users(store, clock).CreateAsync(Instance, "bob@example.test"))!;
        var mfa = Credentials(store, clock);

        await Assert.That(await mfa.PrimaryUserAsync(Instance, user.Id, Epoch(user) + "1", user.AuthStamp, false)).IsNull();
        await Assert.That(await mfa.PrimaryUserAsync(Instance, user.Id, null, user.AuthStamp, false)).IsNull();
        await Assert.That(await mfa.PrimaryUserAsync("other-instance", user.Id, Epoch(user), user.AuthStamp, false)).IsNull();
        await Assert.That(await mfa.PrimaryUserAsync(Instance, "not-an-id", Epoch(user), user.AuthStamp, false)).IsNull();
        await Assert.That(await mfa.PrimaryUserAsync(Instance, user.Id, Epoch(user), "another-stamp", false)).IsNull();
        await Assert.That(await mfa.AllowsPrimaryAuthenticationAsync(user with { AuthEpoch = user.AuthEpoch + 1 })).IsFalse();

        // A pending enrollment still permits primary authentication; an active factor does not.
        var pending = (await mfa.BeginAsync(user))!;
        await Assert.That((await mfa.PrimaryUserAsync(Instance, user.Id, Epoch(user), user.AuthStamp, false))?.Id).IsEqualTo(user.Id);
        var active = (await mfa.ConfirmAsync(user, new Totp(Base32Encoding.ToBytes(pending.Secret)).ComputeTotp(clock.UtcNow.UtcDateTime)))!;
        await Assert.That(await mfa.PrimaryUserAsync(Instance, user.Id, Epoch(active.User), active.User.AuthStamp, false)).IsNull();
        // A token whose sign-in proved the factor passes and carries the proof.
        await Assert.That((await mfa.PrimaryUserAsync(Instance, user.Id, Epoch(active.User), active.User.AuthStamp, true))?.MultiFactor).IsTrue();
        await Assert.That(await mfa.AllowsPrimaryAuthenticationAsync(active.User)).IsFalse();
    }

    private static string Epoch(TerraScale.Authentication.Contracts.UserRecord user) => user.AuthEpoch.ToString(CultureInfo.InvariantCulture);

    private static TotpCredentials Credentials(AuthStore store, TimeProvider clock)
    {
        var options = AuthOptions.Load(AuthTestServer.DefaultSettings());
        return new TotpCredentials(store, new Users(store, clock), new AttemptLimiter(new TerraBaseDocumentStore(store), clock, options), options, clock);
    }

    // Once armed, holds the first reads until three are in flight and records the most seen at once.
    private sealed class PairingHandler() : DelegatingHandler(TerraBaseFixture.NewHandler())
    {
        private readonly TaskCompletionSource paired = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int inFlight;
        private int most;

        public bool Armed { get; set; }

        public int MostInFlight => Volatile.Read(ref this.most);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!this.Armed)
            {
                return await base.SendAsync(request, cancellationToken);
            }

            var now = Interlocked.Increment(ref this.inFlight);
            int seen;
            while ((seen = Volatile.Read(ref this.most)) < now && Interlocked.CompareExchange(ref this.most, now, seen) != seen)
            {
            }

            try
            {
                if (now >= 3)
                {
                    this.paired.TrySetResult();
                }

                await this.paired.Task.WaitAsync(cancellationToken);

                return await base.SendAsync(request, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref this.inFlight);
            }
        }
    }
}

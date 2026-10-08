using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Features.Identity;
using TerraScale.Auth.Store;
using TerraScale.Auth.Tests;
using TerraScale.DotNet.TerraBase;
using TerraScale.DotNet.Testing;

namespace TerraScale.Auth.Fast.Identity;

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
    public async Task A_code_signs_in_once_and_a_reissue_supersedes_the_previous_code()
    {
        var (tenant, database) = await fixture.CreateDatabaseAsync();
        var options = AuthOptions.Load(AuthTestServer.DefaultSettings());
        var (users, sessions, passcodes) = this.Stack(tenant, TimeProvider.System, options);
        var user = (await users.CreateAsync("instance", Email))!;

        var superseded = (await passcodes.RequestAsync("instance", Email))!;
        var current = (await passcodes.RequestAsync("instance", Email))!;
        await Assert.That((await database.ListDocumentsAsync("passcodes")).Items.Count).IsEqualTo(1);
        // Equal codes (one in a million) would make the superseded code indistinguishable from the current one.
        if (superseded.Code != current.Code)
        {
            await Assert.That(await passcodes.VerifyAsync("instance", Email, superseded.Code)).IsNull();
        }

        var signedIn = await this.Stack(tenant, TimeProvider.System, options).Passcodes.VerifyAsync("instance", "ALICE@example.test", $" {current.Code} ");
        await Assert.That(signedIn).IsNotNull();
        await Assert.That((await sessions.ResolveAsync("instance", signedIn!.Token))!.Value.User.Id).IsEqualTo(user.Id);
        await Assert.That(await passcodes.VerifyAsync("instance", Email, current.Code)).IsNull();
        var stored = (await database.ListDocumentsAsync("passcodes")).Items.Single().Document;
        await Assert.That(stored.GetProperty("consumed").GetBoolean()).IsTrue();
        await Assert.That((await database.ListDocumentsAsync("sessions")).Items.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Five_wrong_guesses_kill_the_code_even_for_the_right_code()
    {
        var (tenant, database) = await fixture.CreateDatabaseAsync();
        var (users, _, passcodes) = this.Stack(tenant, TimeProvider.System, AuthOptions.Load(AuthTestServer.DefaultSettings()));
        await users.CreateAsync("instance", Email);
        var issued = (await passcodes.RequestAsync("instance", Email))!;

        for (var guess = 1; guess <= Passcodes.MaxAttempts; guess++)
        {
            await Assert.That(await passcodes.VerifyAsync("instance", Email, Wrong(issued.Code, guess))).IsNull();
        }

        var stored = (await database.ListDocumentsAsync("passcodes")).Items.Single().Document;
        await Assert.That(stored.GetProperty("failedAttempts").GetInt32()).IsEqualTo(5);
        await Assert.That(await passcodes.VerifyAsync("instance", Email, issued.Code)).IsNull();
        await Assert.That((await database.ListDocumentsAsync("sessions")).Items.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Malformed_codes_are_rejected_without_charging_an_attempt()
    {
        var (tenant, database) = await fixture.CreateDatabaseAsync();
        var (users, _, passcodes) = this.Stack(tenant, TimeProvider.System, AuthOptions.Load(AuthTestServer.DefaultSettings()));
        await users.CreateAsync("instance", Email);
        var issued = (await passcodes.RequestAsync("instance", Email))!;
        var before = (await database.ListDocumentsAsync("passcodes")).Items.Single();

        foreach (var malformed in new[] { "", "   ", "12345", "1234567", "abcdef", "12 345", "１２３４５６", issued.Code + "0" })
        {
            await Assert.That(await passcodes.VerifyAsync("instance", Email, malformed)).IsNull();
        }

        var after = (await database.ListDocumentsAsync("passcodes")).Items.Single();
        await Assert.That(after.Version).IsEqualTo(before.Version);
        await Assert.That(after.Document.GetProperty("failedAttempts").GetInt32()).IsEqualTo(0);
        await Assert.That(await passcodes.VerifyAsync("instance", Email, issued.Code)).IsNotNull();
    }

    [Test]
    [Arguments(-1, true)]
    [Arguments(0, false)]
    [Arguments(1, false)]
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
    public async Task A_redemption_that_completes_after_expiry_consumes_the_code_and_grants_nothing()
    {
        var (tenant, database) = await fixture.CreateDatabaseAsync();
        var options = AuthOptions.Load(AuthTestServer.DefaultSettings());
        var clock = new StoreTestClock();
        var (users, _, passcodes) = this.Stack(tenant, clock, options);
        await users.CreateAsync("instance", Email);
        var issued = (await passcodes.RequestAsync("instance", Email))!;

        // The eligibility check sees a live code; the consuming write completes at the expiry instant.
        var late = this.Stack(tenant, new ExpiringClock(issued.ExpiresAt.AddSeconds(-1), issued.ExpiresAt), options).Passcodes;
        await Assert.That(await late.VerifyAsync("instance", Email, issued.Code)).IsNull();
        var stored = (await database.ListDocumentsAsync("passcodes")).Items.Single().Document;
        await Assert.That(stored.GetProperty("consumed").GetBoolean()).IsTrue();
        await Assert.That((await database.ListDocumentsAsync("sessions")).Items.Count).IsEqualTo(0);
        await Assert.That(await passcodes.VerifyAsync("instance", Email, issued.Code)).IsNull();
    }

    [Test]
    public async Task A_verification_paused_across_a_reissue_cannot_consume_or_overwrite_the_replacement()
    {
        var (tenant, database) = await fixture.CreateDatabaseAsync();
        var options = AuthOptions.Load(AuthTestServer.DefaultSettings());
        var clock = new StoreTestClock();
        var (users, _, passcodes) = this.Stack(tenant, clock, options);
        await users.CreateAsync("instance", Email);
        var stale = (await passcodes.RequestAsync("instance", Email))!;
        using var paused = new PausingClock(clock.UtcNow);
        var verifier = this.Stack(tenant, paused, options).Passcodes;

        // The verifier has read the stale code's version and is held before its conditional write.
        var verification = Task.Run(() => verifier.VerifyAsync("instance", Email, stale.Code));
        await paused.Reached.Task;
        var replacement = (await passcodes.RequestAsync("instance", Email))!;
        while (replacement.Code == stale.Code)
        {
            // Two random codes collide one time in a million; the race needs distinct codes.
            replacement = (await passcodes.RequestAsync("instance", Email))!;
        }

        paused.Release();
        await Assert.That(await verification).IsNull();
        var stored = (await database.ListDocumentsAsync("passcodes")).Items.Single().Document;
        await Assert.That(stored.GetProperty("consumed").GetBoolean()).IsFalse();
        // The stale write lost; the retry was judged against the replacement and charged as one wrong guess.
        await Assert.That(stored.GetProperty("failedAttempts").GetInt32()).IsEqualTo(1);
        await Assert.That((await database.ListDocumentsAsync("sessions")).Items.Count).IsEqualTo(0);
        await Assert.That(await passcodes.VerifyAsync("instance", Email, replacement.Code)).IsNotNull();
    }

    [Test]
    public async Task A_failed_session_write_propagates_and_leaves_the_code_consumed()
    {
        var (tenant, database) = await fixture.CreateDatabaseAsync();
        var (users, _, passcodes) = this.Stack(tenant, TimeProvider.System, AuthOptions.Load(AuthTestServer.DefaultSettings()));
        await users.CreateAsync("instance", Email);
        var issued = (await passcodes.RequestAsync("instance", Email))!;
        await database.DropCollectionAsync("sessions");

        await Assert.ThrowsAsync<TerraBaseNotFoundException>(() => passcodes.VerifyAsync("instance", Email, issued.Code));
        var stored = (await database.ListDocumentsAsync("passcodes")).Items.Single().Document;
        await Assert.That(stored.GetProperty("consumed").GetBoolean()).IsTrue();

        await database.CreateCollectionAsync("sessions");
        await Assert.That(await passcodes.VerifyAsync("instance", Email, issued.Code)).IsNull();
        await Assert.That((await database.ListDocumentsAsync("sessions")).Items.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(0)]
    [Arguments(4)]
    [Arguments(5)]
    public async Task Concurrent_requests_count_every_issued_code_and_never_exceed_the_issue_limit(int alreadyIssued)
    {
        var (tenant, database) = await fixture.CreateDatabaseAsync();
        var options = AuthOptions.Load(AuthTestServer.DefaultSettings());
        var clock = new StoreTestClock();
        var (users, _, passcodes) = this.Stack(tenant, clock, options);
        await users.CreateAsync("instance", Email);
        for (var index = 0; index < alreadyIssued; index++)
        {
            await passcodes.RequestAsync("instance", Email);
        }

        // With no document the racers take the create-only path; otherwise they race a versioned replace.
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = Enumerable.Range(0, 16).Select(async _ =>
        {
            var independent = this.Stack(tenant, clock, options).Passcodes;
            await start.Task;
            return await independent.RequestAsync("instance", Email);
        }).ToArray();
        start.SetResult();
        var issued = (await Task.WhenAll(requests)).Where(result => result is not null).ToArray();

        // Racers that read the same version have one winner; a racer that reads after a win may issue again, but
        // every code handed out is counted (no lost update) and the window cap holds.
        var counted = (await database.ListDocumentsAsync("passcodes")).Items.Single().Document.GetProperty("issuedInWindow").GetInt32();
        await Assert.That(counted).IsLessThanOrEqualTo(Passcodes.MaxIssuesPerWindow);
        await Assert.That(issued.Length).IsEqualTo(counted - alreadyIssued);
        await Assert.That(issued.Length > 0).IsEqualTo(alreadyIssued < Passcodes.MaxIssuesPerWindow);

        // Only the last winner's code matches the stored digest; fewer than five wrong tries leave it redeemable.
        var verified = 0;
        foreach (var code in issued)
        {
            verified += await passcodes.VerifyAsync("instance", Email, code!.Code) is null ? 0 : 1;
        }

        await Assert.That(verified).IsEqualTo(issued.Length > 0 ? 1 : 0);
    }

    [Test]
    [Arguments(PasscodePurpose.SignIn)]
    [Arguments(PasscodePurpose.PasswordReset)]
    [Arguments(PasscodePurpose.SignUp)]
    public async Task Issuing_is_limited_per_user_or_pending_signup_per_UTC_clock_hour(PasscodePurpose purpose)
    {
        var (tenant, database) = await fixture.CreateDatabaseAsync();
        var clock = new StoreTestClock();
        var (users, _, passcodes) = this.Stack(tenant, clock, AuthOptions.Load(AuthTestServer.DefaultSettings()));
        if (purpose != PasscodePurpose.SignUp)
        {
            await users.CreateAsync("instance", Email);
        }

        await users.CreateAsync("instance", "bob@example.test");
        var windowStart = clock.UtcNow;

        Task<IssuedPasscode?> IssueAsync() => purpose == PasscodePurpose.SignUp
            ? passcodes.RequestSignUpAsync("instance", Email, "argon2-hash", "pending-token")
            : passcodes.RequestAsync("instance", Email, purpose, CancellationToken.None);

        for (var request = 0; request < Passcodes.MaxIssuesPerWindow; request++)
        {
            clock.UtcNow = windowStart.AddMinutes(50 + request);
            await Assert.That(await IssueAsync()).IsNotNull();
        }

        var limited = (await database.ListDocumentsAsync("passcodes")).Items.Single();
        await Assert.That(limited.Document.GetProperty("windowStartedAt").GetDateTimeOffset()).IsEqualTo(windowStart);
        await Assert.That(limited.Document.GetProperty("issuedInWindow").GetInt32()).IsEqualTo(Passcodes.MaxIssuesPerWindow);
        clock.UtcNow = windowStart + Passcodes.IssueWindow - TimeSpan.FromTicks(1);
        await Assert.That(await IssueAsync()).IsNull();
        await Assert.That((await database.ListDocumentsAsync("passcodes")).Items.Single().Version).IsEqualTo(limited.Version);
        await Assert.That(await passcodes.RequestAsync("instance", "bob@example.test")).IsNotNull();

        clock.UtcNow = windowStart + Passcodes.IssueWindow;
        var renewed = await IssueAsync();
        await Assert.That(renewed).IsNotNull();
        var stored = (await database.ListDocumentsAsync("passcodes")).Items.Single(item => item.Id == limited.Id).Document;
        await Assert.That(stored.GetProperty("windowStartedAt").GetDateTimeOffset()).IsEqualTo(clock.UtcNow);
        await Assert.That(stored.GetProperty("issuedInWindow").GetInt32()).IsEqualTo(1);
        await Assert.That(purpose == PasscodePurpose.SignUp
            ? await passcodes.RedeemSignUpAsync("instance", Email, renewed!.Code, "pending-token")
            : await passcodes.RedeemAsync("instance", Email, renewed!.Code, purpose)).IsNotNull();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Existing_issue_timestamps_identify_their_UTC_hour_and_slower_clocks_preserve_newer_windows(bool resend)
    {
        var (tenant, database) = await fixture.CreateDatabaseAsync();
        var clock = new StoreTestClock();
        var windowStart = clock.UtcNow;
        clock.UtcNow = windowStart.AddMinutes(50);
        var (_, _, passcodes) = this.Stack(tenant, clock, AuthOptions.Load(AuthTestServer.DefaultSettings()));
        await Assert.That(await passcodes.RequestSignUpAsync("instance", Email, "argon2-hash", "pending-token")).IsNotNull();
        var store = new AuthStore(database, clock);

        async Task StoreTimestampAsync()
        {
            var document = (await database.ListDocumentsAsync("passcodes")).Items.Single();
            var record = document.Document.Deserialize(IdentityJson.Default.PasscodeRecord)! with
            {
                WindowStartedAt = windowStart.AddMinutes(50).ToOffset(TimeSpan.FromHours(3)),
            };
            await Assert.That(await store.CompareAndPutAsync(AuthStore.PasscodeCollection, "instance", "sign-up:" + Email,
                JsonSerializer.SerializeToElement(record, IdentityJson.Default.PasscodeRecord), document.Version)).IsTrue();
        }

        Task<IssuedPasscode?> IssueAsync() => resend
            ? passcodes.ResendSignUpAsync("instance", Email, "pending-token")
            : passcodes.RequestSignUpAsync("instance", Email, "argon2-hash", "pending-token");

        // Existing non-rounded timestamps belong to their containing UTC hour, even ahead of this replica's clock.
        await StoreTimestampAsync();
        clock.UtcNow = windowStart.AddMinutes(-10);
        for (var issued = 1; issued < Passcodes.MaxIssuesPerWindow; issued++)
        {
            await Assert.That(await IssueAsync()).IsNotNull();
        }

        var full = (await database.ListDocumentsAsync("passcodes")).Items.Single().Document;
        await Assert.That(full.GetProperty("windowStartedAt").GetDateTimeOffset()).IsEqualTo(windowStart);
        await Assert.That(full.GetProperty("issuedInWindow").GetInt32()).IsEqualTo(Passcodes.MaxIssuesPerWindow);
        await Assert.That(await IssueAsync()).IsNull();

        // Also roll an existing non-rounded, exhausted record over at the exact next clock-hour boundary.
        await StoreTimestampAsync();
        var limited = (await database.ListDocumentsAsync("passcodes")).Items.Single();
        clock.UtcNow = windowStart + Passcodes.IssueWindow - TimeSpan.FromTicks(1);
        await Assert.That(await IssueAsync()).IsNull();
        await Assert.That((await database.ListDocumentsAsync("passcodes")).Items.Single().Version).IsEqualTo(limited.Version);
        clock.UtcNow += TimeSpan.FromTicks(1);
        await Assert.That(await IssueAsync()).IsNotNull();
        var renewed = (await database.ListDocumentsAsync("passcodes")).Items.Single().Document;
        await Assert.That(renewed.GetProperty("windowStartedAt").GetDateTimeOffset()).IsEqualTo(clock.UtcNow);
        await Assert.That(renewed.GetProperty("issuedInWindow").GetInt32()).IsEqualTo(1);
        foreach (var field in new[] { "passwordHash", "pendingTokenHash" })
        {
            await Assert.That(renewed.GetProperty(field).GetRawText()).IsEqualTo(full.GetProperty(field).GetRawText());
        }
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

    [Test]
    public async Task Concurrent_redemptions_from_independent_stacks_open_exactly_one_session()
    {
        var (tenant, database) = await fixture.CreateDatabaseAsync();
        var options = AuthOptions.Load(AuthTestServer.DefaultSettings());
        var (users, _, passcodes) = this.Stack(tenant, TimeProvider.System, options);
        await users.CreateAsync("instance", Email);
        var issued = (await passcodes.RequestAsync("instance", Email))!;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 16).Select(async _ =>
        {
            var independent = this.Stack(tenant, TimeProvider.System, options).Passcodes;
            await start.Task;
            return await independent.VerifyAsync("instance", Email, issued.Code);
        }).ToArray();
        start.SetResult();
        var results = await Task.WhenAll(attempts);

        await Assert.That(results.Count(result => result is not null)).IsEqualTo(1);
        await Assert.That((await database.ListDocumentsAsync("sessions")).Items.Count).IsEqualTo(1);
        var stored = (await database.ListDocumentsAsync("passcodes")).Items.Single().Document;
        await Assert.That(stored.GetProperty("consumed").GetBoolean()).IsTrue();
        await Assert.That(stored.GetProperty("failedAttempts").GetInt32()).IsEqualTo(0);
    }

    [Test]
    public async Task Concurrent_wrong_guesses_never_exceed_the_attempt_limit()
    {
        var (tenant, database) = await fixture.CreateDatabaseAsync();
        var options = AuthOptions.Load(AuthTestServer.DefaultSettings());
        var (users, _, passcodes) = this.Stack(tenant, TimeProvider.System, options);
        await users.CreateAsync("instance", Email);
        var issued = (await passcodes.RequestAsync("instance", Email))!;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(1, 24).Select(async guess =>
        {
            var independent = this.Stack(tenant, TimeProvider.System, options).Passcodes;
            await start.Task;
            return await independent.VerifyAsync("instance", Email, Wrong(issued.Code, guess));
        }).ToArray();
        start.SetResult();
        var results = await Task.WhenAll(attempts);

        await Assert.That(results.All(result => result is null)).IsTrue();
        var stored = (await database.ListDocumentsAsync("passcodes")).Items.Single().Document;
        await Assert.That(stored.GetProperty("failedAttempts").GetInt32()).IsEqualTo(Passcodes.MaxAttempts);
        await Assert.That(await passcodes.VerifyAsync("instance", Email, issued.Code)).IsNull();
        await Assert.That((await database.ListDocumentsAsync("sessions")).Items.Count).IsEqualTo(0);
    }

    private static string Wrong(string code, int offset) =>
        ((int.Parse(code, CultureInfo.InvariantCulture) + offset) % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);

    private sealed class ExpiringClock(DateTimeOffset live, DateTimeOffset expired) : TimeProvider
    {
        private int observations;

        // Only the first observation (eligibility) sees the live instant.
        public override DateTimeOffset GetUtcNow() => Interlocked.Increment(ref this.observations) == 1 ? live : expired;
    }

    private sealed class PausingClock(DateTimeOffset now) : TimeProvider, IDisposable
    {
        private readonly ManualResetEventSlim released = new();
        private int observations;

        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => this.released.Set();

        // The first observation is the eligibility check right after the passcode read; hold it there.
        public override DateTimeOffset GetUtcNow()
        {
            if (Interlocked.Increment(ref this.observations) == 1)
            {
                this.Reached.SetResult();
                this.released.Wait(TimeSpan.FromSeconds(30));
            }

            return now;
        }

        public void Dispose() => this.released.Dispose();
    }

    [Test]
    public async Task Pruning_deletes_code_records_only_once_their_window_and_every_code_in_it_have_ended()
    {
        var (tenant, database) = await fixture.CreateDatabaseAsync();
        var clock = new StoreTestClock();
        var (users, _, passcodes) = this.Stack(tenant, clock, AuthOptions.Load(AuthTestServer.DefaultSettings()));
        await users.CreateAsync("instance", Email);
        await Assert.That(await passcodes.RequestAsync("instance", Email)).IsNotNull();
        await Assert.That(await passcodes.RequestSignUpAsync("instance", "new@example.test", "argon2-hash", "pending-token")).IsNotNull();

        // At the window's end a code issued just before it is still live.
        clock.UtcNow += Passcodes.IssueWindow;
        var late = (await passcodes.RequestSignUpAsync("instance", "late@example.test", "argon2-hash", "late-token"))!;
        await Assert.That(await passcodes.PruneAsync(batchSize: 1)).IsEqualTo(0);

        clock.UtcNow += Passcodes.Lifetime;
        await Assert.That(await passcodes.PruneAsync(batchSize: 1)).IsEqualTo(2);
        var left = (await database.ListDocumentsAsync("passcodes")).Items.Single().Document;
        await Assert.That(left.GetProperty("email").GetString()).IsEqualTo(late.Email);
    }

    private (Users Users, Sessions Sessions, Passcodes Passcodes) Stack(TerraBaseTestTenant tenant, TimeProvider clock, AuthOptions options)
    {
        var store = new AuthStore(fixture.Server.Documents(tenant.ApiKey).Database(tenant.Project.Id, "auth"), clock);
        var users = new Users(store, clock);
        var sessions = new Sessions(store, users, clock);
        return (users, sessions, new Passcodes(store, users, sessions, clock, options));
    }
}

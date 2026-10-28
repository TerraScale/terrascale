using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Features.Email;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;
using static TerraScale.Authentication.Tests.PlatformCalls;

namespace TerraScale.Authentication.Fast.Email;

/// <summary>Real hosted HTTP requests against a genuinely saturated bounded delivery queue.</summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class EmailQueueAdmissionTests(TerraBaseFixture fixture)
{
    private const string Owner = "queue-owner@example.test";

    [Test]
    [Arguments("sign-in", false)]
    [Arguments("sign-up", false)]
    [Arguments("reset", false)]
    [Arguments("sign-in", true)]
    [Arguments("sign-up", true)]
    [Arguments("reset", true)]
    public async Task Full_queue_refuses_known_and_unknown_mail_starts_and_preserves_abuse_limits(string action, bool projectTenant)
    {
        var clock = new ManualClock(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        var captured = new CapturingEmailSender();
        var sender = new GatedEmailSender(captured);
        await using var oidc = await OidcHarness.StartAsync(fixture, email: captured, clock: clock,
            services: services => services.AddSingleton<IEmailSender>(sender));
        try
        {
            var tenant = OidcIssuer.InstanceId;
            if (projectTenant)
            {
                var (_, token) = await oidc.SignInUserAsync(Owner);
                var organization = await oidc.CreateOrganizationAsync(token);
                tenant = await oidc.CreateProjectAsync(token, organization, "Queue test project");
            }

            await SaturateAsync(oidc, sender, SignInMail.Capacity);
            var addresses = DistinctEmails(oidc, sender.BlockedRecipient!);
            await Assert.That(await oidc.Server.App.Services.GetRequiredService<Users>().CreateAsync(tenant, addresses[0])).IsNotNull();
            var passcodes = oidc.Server.App.Services.GetRequiredService<Passcodes>();
            await Assert.That(await passcodes.RequestAsync(tenant, addresses[0],
                action == "reset" ? PasscodePurpose.PasswordReset : PasscodePurpose.SignIn, CancellationToken.None)).IsNotNull();
            await Assert.That(await passcodes.RequestSignUpAsync(tenant, addresses[1], "synthetic-hash", "synthetic-pending-token")).IsNotNull();
            var before = (await oidc.Database.ListDocumentsAsync(AuthStore.PasscodeCollection)).Items
                .OrderBy(item => item.Id).Select(item => (item.Id, item.Version, item.Document.GetRawText())).ToArray();
            var path = $"/ui/{tenant}/{action}";
            string? failure = null;
            foreach (var address in addresses)
            {
                using var response = await oidc.PostFormAsync(path, Form(address));
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
                await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("application/json");
                var body = await response.Content.ReadAsStringAsync();
                using var json = JsonDocument.Parse(body);
                await Assert.That(json.RootElement.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("email_queue_unavailable");
                await Assert.That(body).DoesNotContain(address);
                failure ??= body;
                await Assert.That(body).IsEqualTo(failure);
            }

            using var limited = await oidc.PostFormAsync(path, Form(addresses[0]));
            await Assert.That(limited.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
            await Assert.That(limited.Headers.RetryAfter).IsNotNull();
            var after = (await oidc.Database.ListDocumentsAsync(AuthStore.PasscodeCollection)).Items
                .OrderBy(item => item.Id).Select(item => (item.Id, item.Version, item.Document.GetRawText())).ToArray();
            await Assert.That(after).IsEquivalentTo(before);

            sender.Release.TrySetResult();
            await oidc.Server.DrainMailAsync();
            await Assert.That((await captured.NextAsync()).Recipient).IsEqualTo(sender.BlockedRecipient);
            await Assert.That(captured.HasPending).IsFalse();
            clock.Advance(HostedAuth.MailCooldown);
            foreach (var address in addresses)
            {
                using var recovered = await oidc.PostFormAsync(path, Form(address));
                await Assert.That(recovered.StatusCode).IsEqualTo(HttpStatusCode.OK);
                if (address == addresses[0] || action == "sign-up")
                {
                    var (recipient, message) = await captured.NextAsync();
                    await Assert.That(recipient).IsEqualTo(address);
                    await Assert.That(message.Tenant).IsEqualTo(tenant);
                    await Assert.That(message.Purpose).IsEqualTo(EmailPurpose.Authentication);
                }
            }

            await oidc.Server.DrainMailAsync();
            await Assert.That(captured.HasPending).IsFalse();
        }
        finally
        {
            sender.Release.TrySetResult();
        }
    }

    [Test]
    public async Task Cancelling_an_invitation_before_commit_releases_the_last_reserved_slot()
    {
        var pause = new PausingTransactions { InnerHandler = TerraBaseFixture.NewHandler() };
        using var http = new HttpClient(pause);
        await using var oidc = await OidcHarness.StartAsync(fixture, services: services => services.AddSingleton(http));
        var (user, token) = await oidc.SignInUserAsync(Owner);
        var organization = await oidc.CreateOrganizationAsync(token);
        var mail = oidc.Server.App.Services.GetRequiredService<SignInMail>();
        var reservations = new List<SignInMail.Reservation>();
        using var cancellation = new CancellationTokenSource();
        try
        {
            for (var index = 0; index < SignInMail.Capacity - 1; index++)
            {
                reservations.Add(mail.Reserve());
            }
            var invitation = await pause.HoldNextAsync(() => oidc.Server.App.Services.GetRequiredService<Invitations>()
                .CreateAsync(user, organization, "cancelled@example.test", "viewer", null, AuthEmails.English, cancellation.Token));
            using var overloaded = await oidc.PostFormAsync($"/ui/{OidcIssuer.Slug}/sign-up", Form(DistinctEmails(oidc, Owner)[0]));
            await Assert.That(overloaded.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            await cancellation.CancelAsync();
            var cancelled = false;
            try
            {
                await invitation.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            await Assert.That(cancelled).IsTrue();
            using var recovered = mail.Reserve();
            var listed = await oidc.OkAsync(HttpMethod.Get, $"{Organizations}/{organization}/invitations", token);
            await Assert.That(listed.GetProperty("items").GetArrayLength()).IsEqualTo(0);
            await Assert.That(oidc.Email.HasPending).IsFalse();
        }
        finally
        {
            await cancellation.CancelAsync();
            foreach (var reservation in reservations)
            {
                reservation.Dispose();
            }
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Shutdown_rejects_new_starts_but_waits_for_an_existing_reservation(bool submit)
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        await oidc.CreateUserAsync(Owner);
        var mail = oidc.Server.App.Services.GetRequiredService<SignInMail>();
        using var reservation = mail.Reserve();
        var stopping = mail.StopAsync(CancellationToken.None);
        try
        {
            await Assert.That(stopping.IsCompleted).IsFalse();
            using var refused = await oidc.PostFormAsync($"/ui/{OidcIssuer.Slug}/sign-up", Form("stopping@example.test"));
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            if (submit)
            {
                await reservation.EnqueueCodeAsync(OidcIssuer.InstanceId, Owner, PasscodePurpose.SignIn);
            }
        }
        finally
        {
            reservation.Dispose();
            await stopping.WaitAsync(TimeSpan.FromSeconds(10));
        }
        if (submit)
        {
            await Assert.That((await oidc.Email.NextAsync()).Recipient).IsEqualTo(Owner);
        }
        await Assert.That(oidc.Email.HasPending).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Invitation_conflict_releases_capacity_and_only_one_concurrent_create_can_take_the_last_slot(bool projectInvitation)
    {
        var captured = new CapturingEmailSender();
        var sender = new GatedEmailSender(captured);
        await using var oidc = await OidcHarness.StartAsync(fixture, email: captured,
            services: services => services.AddSingleton<IEmailSender>(sender));
        try
        {
            var (_, token) = await oidc.SignInUserAsync(Owner);
            var organization = await oidc.CreateOrganizationAsync(token);
            var project = projectInvitation ? await oidc.CreateProjectAsync(token, organization, "Queue invitations") : null;
            var path = $"{Organizations}/{organization}/invitations";
            const string pending = "pending@example.test";
            await oidc.OkAsync(HttpMethod.Post, path, token, new { email = pending, role = "viewer", project_id = project }, HttpStatusCode.Created);
            await captured.NextAsync();

            await SaturateAsync(oidc, sender, SignInMail.Capacity - 1);
            // The transaction fails after reserving the last slot. A later create must still be able to use it.
            await Assert.That(await oidc.FailAsync(HttpMethod.Post, path, token,
                new { email = pending, role = "viewer", project_id = project })).IsEqualTo((409, "invitation_pending"));
            var addresses = DistinctEmails(oidc, sender.BlockedRecipient!);
            var attempts = await Task.WhenAll(addresses.Select(address => oidc.FailAsync(HttpMethod.Post, path, token,
                new { email = address, role = "viewer", project_id = project })));
            await Assert.That(attempts.Select(attempt => attempt.Status).Order().ToArray()).IsEquivalentTo([201, 503]);
            var accepted = addresses[Array.FindIndex(attempts, attempt => attempt.Status == 201)];
            var refused = addresses[Array.FindIndex(attempts, attempt => attempt.Status == 503)];
            await Assert.That(attempts.Single(attempt => attempt.Status == 503).Code).IsEqualTo("email_queue_unavailable");
            var listed = await oidc.OkAsync(HttpMethod.Get, path, token);
            var invitations = listed.GetProperty("items").EnumerateArray().ToArray();
            await Assert.That(invitations.Select(item => item.GetProperty("email").GetString()!).ToArray()).IsEquivalentTo([pending, accepted]);
            await Assert.That(invitations.Single(item => item.GetProperty("email").GetString() == accepted)
                .GetProperty("project_id").GetString()).IsEqualTo(project);

            sender.Release.TrySetResult();
            await oidc.Server.DrainMailAsync();
            await Assert.That((await captured.NextAsync()).Recipient).IsEqualTo(sender.BlockedRecipient);
            var delivered = await captured.NextAsync();
            await Assert.That(delivered.Recipient).IsEqualTo(accepted);
            await Assert.That(delivered.Message.Tenant).IsEqualTo(organization);
            await Assert.That(delivered.Message.Purpose).IsEqualTo(EmailPurpose.Invitation);
            await Assert.That(captured.HasPending).IsFalse();

            // Known-full refusal persisted no pending invitation: the same create now succeeds without revocation.
            await oidc.OkAsync(HttpMethod.Post, path, token, new { email = refused, role = "viewer", project_id = project }, HttpStatusCode.Created);
            await Assert.That((await captured.NextAsync()).Recipient).IsEqualTo(refused);
        }
        finally
        {
            sender.Release.TrySetResult();
        }
    }

    [Test]
    public async Task A_replica_taking_over_delivers_accepted_codes_and_never_resends_an_interrupted_send()
    {
        var captured = new CapturingEmailSender();
        var sender = new GatedEmailSender(captured);
        await using var oidc = await OidcHarness.StartAsync(fixture, email: captured, services: services => services.AddSingleton<IEmailSender>(sender));
        try
        {
            sender.BlockedRecipient = DistinctEmails(oidc, Owner)[0];
            await oidc.CreateUserAsync(sender.BlockedRecipient);
            await oidc.CreateUserAsync(Owner);
            foreach (var address in new[] { sender.BlockedRecipient, Owner })
            {
                using var started = await oidc.PostFormAsync($"/ui/{OidcIssuer.Slug}/sign-in", Form(address));
                await Assert.That(started.StatusCode).IsEqualTo(HttpStatusCode.OK);
                await sender.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }

            // This replica is stuck in a provider call, as if it crashed there, with a second code accepted behind it.
            await Assert.That(await oidc.Server.MailStatesAsync()).IsEquivalentTo(["sending", "queued"]);
            await using var replica = await oidc.StartReplicaAsync(clock: new ManualClock(DateTimeOffset.UtcNow + SignInMail.Lease + TimeSpan.FromSeconds(1)),
                services: services => services.AddSingleton<IEmailSender>(sender));
            var (recipient, message) = await captured.NextAsync();
            await Assert.That(recipient).IsEqualTo(Owner);
            await Assert.That(message.Purpose).IsEqualTo(EmailPurpose.Authentication);
            await replica.DrainMailAsync();
            await Assert.That(await replica.MailStatesAsync()).IsEquivalentTo(["unknown", "sent"]);

            // Only the interrupted call itself ever reaches the provider.
            sender.Release.TrySetResult();
            await oidc.Server.DrainMailAsync();
            await Assert.That((await captured.NextAsync()).Recipient).IsEqualTo(sender.BlockedRecipient);
            await Assert.That(captured.HasPending).IsFalse();
            await Assert.That(await replica.MailStatesAsync()).IsEquivalentTo(["unknown", "sent"]);
        }
        finally
        {
            sender.Release.TrySetResult();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_code_superseded_or_expired_before_its_retry_is_never_delivered(bool expire)
    {
        var clock = new ManualClock(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        var captured = new CapturingEmailSender();
        var sender = new GatedEmailSender(captured) { RefuseOnce = true };
        await using var oidc = await OidcHarness.StartAsync(fixture, email: captured, clock: clock, services: services => services.AddSingleton<IEmailSender>(sender));
        await oidc.CreateUserAsync(Owner);
        using (var reservation = oidc.Server.App.Services.GetRequiredService<SignInMail>().Reserve())
        {
            await reservation.EnqueueCodeAsync(OidcIssuer.InstanceId, Owner, PasscodePurpose.SignIn);
        }

        // The provider refused without sending; wait until the row is deferred for a retry.
        await sender.Refused.Task.WaitAsync(TimeSpan.FromSeconds(10));
        while (await oidc.Server.MailStatesAsync() is not ["ready"])
        {
            await Task.Delay(25);
        }

        if (expire)
        {
            clock.Advance(Passcodes.Lifetime);
        }
        else
        {
            await Assert.That(await oidc.Server.App.Services.GetRequiredService<Passcodes>().RequestAsync(OidcIssuer.InstanceId, Owner)).IsNotNull();
            clock.Advance(SignInMail.RetryDelay);
        }

        await oidc.Server.DrainMailAsync();
        await Assert.That(captured.HasPending).IsFalse();
        await Assert.That(await oidc.Server.MailStatesAsync()).IsEquivalentTo(["suppressed"]);
    }

    private static Dictionary<string, string> Form(string email) => new() { ["email"] = email, ["password"] = "Synthetic-password-123!" };

    private static string[] DistinctEmails(OidcHarness oidc, string excluded)
    {
        var limiter = oidc.Server.App.Services.GetRequiredService<AttemptLimiter>();
        var folds = new HashSet<string>(StringComparer.Ordinal) { limiter.Fold(Owner), limiter.Fold(excluded) };
        var faker = new Faker();
        return Enumerable.Range(0, 20).Select(_ => faker.Internet.Email(provider: "example.test").ToLowerInvariant())
            .Where(email => folds.Add(limiter.Fold(email))).Take(2).ToArray();
    }

    private static async Task SaturateAsync(OidcHarness oidc, GatedEmailSender sender, int queued)
    {
        sender.BlockedRecipient = DistinctEmails(oidc, Owner)[0];
        await oidc.CreateUserAsync(sender.BlockedRecipient);
        using var started = await oidc.PostFormAsync($"/ui/{OidcIssuer.Slug}/sign-in", Form(sender.BlockedRecipient));
        await Assert.That(started.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await sender.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var mail = oidc.Server.App.Services.GetRequiredService<SignInMail>();
        // Unknown-account work really occupies queue slots too. A blocked real sender keeps these rows waiting.
        for (var index = 0; index < queued; index++)
        {
            using var reservation = mail.Reserve();
            await reservation.EnqueueCodeAsync(OidcIssuer.InstanceId, $"absent-{index}@example.test", PasscodePurpose.SignIn);
        }
    }

    private sealed class GatedEmailSender(CapturingEmailSender captured) : IEmailSender
    {
        public string? BlockedRecipient { get; set; }

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool RefuseOnce { get; init; }

        public TaskCompletionSource Refused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<EmailDeliveryOutcome> DeliverAsync(string recipient, EmailMessage message, CancellationToken cancellationToken)
        {
            if (this.RefuseOnce && this.Refused.TrySetResult())
            {
                return EmailDeliveryOutcome.Retryable;
            }

            await this.SendAsync(recipient, message, cancellationToken);
            return EmailDeliveryOutcome.Accepted;
        }

        public async Task SendAsync(string recipient, EmailMessage message, CancellationToken cancellationToken)
        {
            if (recipient == this.BlockedRecipient)
            {
                this.Entered.TrySetResult();
                await this.Release.Task.WaitAsync(cancellationToken);
            }

            await captured.SendAsync(recipient, message, cancellationToken);
        }
    }
}

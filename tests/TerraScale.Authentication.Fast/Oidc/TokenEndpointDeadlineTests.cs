using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using static OpenIddict.Abstractions.OpenIddictConstants;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Oidc;

[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class TokenEndpointDeadlineTests(TerraBaseFixture fixture)
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_stalled_refresh_answers_503_at_the_deadline_and_a_late_commit_never_rotates_twice(bool retryBeforeCommit)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var stall = new StalledRedemption { InnerHandler = TerraBaseFixture.NewHandler() };
        using var http = new HttpClient(stall);
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock, services: services => services.AddSingleton(http));
        var refresh = (await oidc.SignInAndRedeemAsync()).Tokens.GetProperty("refresh_token").GetString()!;

        stall.Arm();
        var stalled = oidc.RefreshAsync(refresh);
        await stall.Held.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(stalled.IsCompleted).IsFalse();
        clock.Advance(TokenEndpointDeadline.Deadline);
        using var unavailable = await stalled.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(unavailable.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(unavailable.Headers.RetryAfter?.Delta).IsEqualTo(TimeSpan.FromSeconds(2));
        await Assert.That(unavailable.Headers.CacheControl?.NoStore).IsTrue();
        await Assert.That(await OidcHarness.ErrorAsync(unavailable)).IsEqualTo(Errors.TemporarilyUnavailable);

        if (retryBeforeCommit)
        {
            using var retried = await oidc.RefreshAsync(refresh);
            await Assert.That(retried.StatusCode).IsEqualTo(HttpStatusCode.OK);
            stall.Release.TrySetResult();
            await Assert.That(await stall.Committed.Task.WaitAsync(TimeSpan.FromSeconds(10))).IsEqualTo(HttpStatusCode.PreconditionFailed);
            // The late write lost, so the retry's grant goes on.
            using var next = await oidc.RefreshAsync((await OidcHarness.JsonAsync(retried)).GetProperty("refresh_token").GetString()!);
            await Assert.That(next.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
        else
        {
            stall.Release.TrySetResult();
            await Assert.That(await stall.Committed.Task.WaitAsync(TimeSpan.FromSeconds(10))).IsEqualTo(HttpStatusCode.OK);
            using var retried = await oidc.RefreshAsync(refresh);
            await Assert.That(retried.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(await OidcHarness.ErrorAsync(retried)).IsEqualTo(Errors.InvalidGrant);
        }
    }

    // Holds the next refresh-token redemption until its caller gives up, then sends it once the test releases it: a write
    // TerraBase received before the deadline and applied after it.
    private sealed class StalledRedemption : DelegatingHandler
    {
        private int armed;

        public TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<HttpStatusCode> Committed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Arm() => Volatile.Write(ref this.armed, 1);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Post || request.Content is null
                || !request.RequestUri!.AbsolutePath.EndsWith("/transactions", StringComparison.Ordinal))
            {
                return await base.SendAsync(request, cancellationToken);
            }

            var body = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            if (!Encoding.UTF8.GetString(body).Contains("\"redeemed\"", StringComparison.Ordinal) || Interlocked.Exchange(ref this.armed, 0) == 0)
            {
                return await base.SendAsync(request, cancellationToken);
            }

            var late = new HttpRequestMessage(request.Method, request.RequestUri) { Version = request.Version, VersionPolicy = request.VersionPolicy, Content = new ByteArrayContent(body) };
            foreach (var header in request.Headers)
            {
                late.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            foreach (var header in request.Content.Headers)
            {
                late.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            _ = Task.Run(async () =>
            {
                await this.Release.Task;
                using (late)
                {
                    try
                    {
                        using var response = await base.SendAsync(late, CancellationToken.None);
                        this.Committed.TrySetResult(response.StatusCode);
                    }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    {
                        this.Committed.TrySetException(error);
                    }
                }
            }, CancellationToken.None);
            this.Held.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("An infinite delay ended without cancellation.");
        }
    }
}

using System.Net;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase;

public sealed class TerraBaseTokenRefreshTests
{
    [Test]
    public async Task Valid_token_never_waits_for_shared_background_renewal()
    {
        var time = new Clock();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var handler = new TerraBaseTestHandler(async (_, token) =>
        {
            var call = Interlocked.Increment(ref calls);
            if (call == 2)
            {
                entered.SetResult();
                await release.Task.WaitAsync(token);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"access_token":"token-{{call}}","expires_in":60,"token_type":"Bearer"}"""),
            };
        });
        using var http = new HttpClient(handler);
        var get = new TerraScaleApiKeyTokens(http, "01ARZ3NDEKTSV4RRFFQ69G5FAX_" + new string('a', 52), "key", time)
            .For(TerraBaseIntegrationFixture.ProjectId);
        try
        {
            await Assert.That(await get(CancellationToken.None)).IsEqualTo("token-1");
            time.Seconds = 30;
            await Assert.That(await get(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(1))).IsEqualTo("token-1");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            time.Seconds = 59;
            await Assert.That(await get(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(1))).IsEqualTo("token-1");
            time.Seconds = 60;
            var waiting = get(CancellationToken.None).AsTask();
            await Assert.That(waiting.IsCompleted).IsFalse();
            release.SetResult();
            await Assert.That(await waiting.WaitAsync(TimeSpan.FromSeconds(1))).IsEqualTo("token-2");
            await Assert.That(calls).IsEqualTo(2);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    private sealed class Clock : TimeProvider
    {
        public int Seconds { get; set; }
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddSeconds(Seconds);
    }
}

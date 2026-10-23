using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace TerraScale.Authentication.Tests;

/// <summary>Waits across the readiness loop's passes, which run five seconds after the previous one ends.</summary>
public static class ReadinessWait
{
    /// <summary>Long enough for one full pass after a change to finish.</summary>
    public static readonly TimeSpan NextPass = TimeSpan.FromSeconds(5.5);

    /// <summary>Polls <c>/readyz</c> until it answers 200 or the limit passes.</summary>
    public static async Task<bool> ReadyWithinAsync(HttpClient client, TimeSpan limit)
    {
        var deadline = DateTimeOffset.UtcNow + limit;
        do
        {
            using var response = await client.GetAsync("/readyz");
            if (response.StatusCode == HttpStatusCode.OK)
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }
        while (DateTimeOffset.UtcNow < deadline);
        return false;
    }

    /// <summary>Polls <c>/readyz</c> until its body reports <paramref name="status"/> ("ready" or "not_ready") or the limit passes.</summary>
    public static Task<bool> StatusWithinAsync(HttpClient client, string status, TimeSpan limit) => UntilAsync(async () =>
    {
        using var response = await client.GetAsync("/readyz");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("status").GetString() == status;
    }, limit);

    /// <summary>Polls <paramref name="condition"/> until it holds or the limit passes. Use it for state the test can observe.</summary>
    public static async Task<bool> UntilAsync(Func<Task<bool>> condition, TimeSpan limit)
    {
        var deadline = DateTimeOffset.UtcNow + limit;
        do
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }
        while (DateTimeOffset.UtcNow < deadline);
        return false;
    }
}

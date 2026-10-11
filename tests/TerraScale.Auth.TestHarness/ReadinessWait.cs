using System.Net;

namespace TerraScale.Auth.Tests;

/// <summary>Waits across the readiness loop's passes, which run every five seconds.</summary>
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
}

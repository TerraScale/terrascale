using System.Net;
using System.Text.Json;
using TerraBase.Primitives;
using TerraBase.Testing;
using static TerraBase.Testing.ManagementApi;

namespace TerraBase.Fast.Management;

/// <summary>
/// API key IP allowlists (<c>key_cidrs</c>, <c>project_cidrs</c> on ts-auth's token): a request from outside any list the
/// token carries answers 403 ip_not_allowed, over HTTP and on the Postgres wire, where the edge names the client with a
/// PROXY protocol v2 header.
/// </summary>
public sealed class IpAllowlistTests
{
    [Test]
    public async Task Http_requests_must_come_from_every_allowlist_the_token_carries()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync("main");
        var collections = Uri(database.Data + "/collections");

        async Task<(HttpStatusCode Status, string? Code)> GetAsync(string? keyCidrs, string? projectCidrs, string? forwardedFor = null)
        {
            using var client = api.WithToken(api.Issuer.Mint(EntityIds.New().ToString(), token =>
            {
                token.Claims["project_id"] = database.Project;
                token.Claims["scope"] = "databases:read documents:read";
                if (keyCidrs is not null)
                {
                    token.Claims["key_cidrs"] = keyCidrs;
                }

                if (projectCidrs is not null)
                {
                    token.Claims["project_cidrs"] = projectCidrs;
                }
            }));
            using var request = new HttpRequestMessage(HttpMethod.Get, collections);
            if (forwardedFor is not null)
            {
                request.Headers.Add("X-Forwarded-For", forwardedFor);
            }

            using var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            return (response.StatusCode, body.Length > 0 && JsonElement.Parse(body).TryGetProperty("error", out var error) ? error.GetProperty("code").GetString() : null);
        }

        await Assert.That((await GetAsync(null, null)).Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await GetAsync("127.0.0.0/8", "0.0.0.0/0 ::/0")).Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await GetAsync("203.0.113.0/24", null)).IsEqualTo((HttpStatusCode.Forbidden, "ip_not_allowed"));
        await Assert.That(await GetAsync(null, "198.51.100.7/32")).IsEqualTo((HttpStatusCode.Forbidden, "ip_not_allowed"));
        // The edge (a loopback peer here) names the client in the rightmost X-Forwarded-For hop.
        await Assert.That((await GetAsync("203.0.113.0/24", null, "198.51.100.1, 203.0.113.9")).Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await GetAsync("203.0.113.0/24", "198.51.100.0/24", "203.0.113.9")).IsEqualTo((HttpStatusCode.Forbidden, "ip_not_allowed"));
        // A malformed list fails authentication instead of allowing every address.
        await Assert.That((await GetAsync("not-a-cidr", null)).Status).IsEqualTo(HttpStatusCode.Unauthorized);
    }
}

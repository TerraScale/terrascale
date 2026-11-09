using System.Net;
using System.Text.Json;
using TerraBase.Primitives;
using TerraBase.Testing;
using static TerraBase.Testing.ManagementApi;

namespace TerraBase.Fast.Management;

public sealed class IpAllowlistTests
{
    [Test]
    public async Task Http_requests_must_come_from_every_allowlist_the_token_carries()
    {
        await using var api = await ManagementApi.StartSharedAsync();
        var database = await api.CreateManagedDatabaseAsync("main");
        var sql = Uri(database.Data + "/sql/execute");

        async Task<(HttpStatusCode Status, string? Code)> GetAsync(string? keyCidrs, string? projectCidrs, string? forwardedFor = null)
        {
            using var client = api.WithToken(api.Issuer.Mint(EntityIds.New().ToString(), token =>
            {
                token.Claims["project_id"] = database.Project;
                token.Claims["scope"] = "databases:read sql:read";
                if (keyCidrs is not null)
                {
                    token.Claims["key_cidrs"] = keyCidrs;
                }

                if (projectCidrs is not null)
                {
                    token.Claims["project_cidrs"] = projectCidrs;
                }
            }));
            using var request = new HttpRequestMessage(HttpMethod.Post, sql) { Content = JsonContent.Create(new { statements = new[] { new { sql = "SELECT 1" } } }) };
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
        await Assert.That((await GetAsync("203.0.113.0/24", null, "198.51.100.1, 203.0.113.9")).Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await GetAsync("203.0.113.0/24", "198.51.100.0/24", "203.0.113.9")).IsEqualTo((HttpStatusCode.Forbidden, "ip_not_allowed"));
        await Assert.That((await GetAsync("not-a-cidr", null)).Status).IsEqualTo(HttpStatusCode.Unauthorized);
    }
}

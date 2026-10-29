using System.Net;
using System.Text.Json;
using Bogus;
using Npgsql;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Slow.Management;

public sealed class KeyRevocationEdgeTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Unavailable_status_and_a_key_id_that_differs_from_sub_fail_closed(bool mismatched)
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        var (_, token) = Grant(api, database, mismatched);
        api.Issuer.StatusAvailable = false;
        using var client = api.WithToken(token);
        using var response = await client.GetAsync(ManagementApi.Uri(database.Data + "/collections"));
        await Assert.That(response.StatusCode).IsEqualTo(mismatched ? HttpStatusCode.Unauthorized : HttpStatusCode.ServiceUnavailable);
        if (!mismatched)
        {
            await Assert.That((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetString()).IsEqualTo("auth_unavailable");
            await Assert.That(response.Headers.RetryAfter!.Delta).IsEqualTo(TimeSpan.FromSeconds(1));
        }

        await Assert.That(api.Issuer.StatusRequests).IsEqualTo(mismatched ? 0 : 1);
    }

    private static (string Key, string Token) Grant(ManagementApi api, ManagedDatabaseRef database, bool mismatched = false, string scope = "documents:read databases:read")
    {
        var id = EntityIds.New().ToString();
        var key = id + "_" + new Faker().Random.String2(52, "0123456789abcdefghjkmnpqrstvwxyz");
        var token = api.Issuer.Mint(id, descriptor =>
        {
            descriptor.Claims["project_id"] = database.Project;
            descriptor.Claims["scope"] = scope;
            descriptor.Claims["key_id"] = mismatched ? EntityIds.New().ToString() : id;
            descriptor.Claims["key_gen"] = TestIssuer.KeyGeneration(key);
        });
        api.Issuer.GrantClient(key, () => token);
        return (key, token);
    }
}

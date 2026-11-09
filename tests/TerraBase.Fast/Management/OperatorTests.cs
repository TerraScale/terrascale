using System.Net;
using TerraBase.Primitives;

namespace TerraBase.Fast.Management;

public sealed class OperatorTests
{
    private const string UserHeader = "X-TerraBase-User-Id";
    private const string Organizations = "/v1/management/organizations";

    [Test]
    public async Task The_operator_is_refused_on_the_public_listener_and_tokens_on_the_internal_listener()
    {
        await using var api = await ManagementApi.StartSharedAsync();
        using var client = api.WithToken(null);
        var user = EntityIds.New().ToString();

        using var onAdmin = await SendAsync(api.Server.Client, HttpMethod.Get, Organizations, null, user);
        using var onClient = await SendAsync(client, HttpMethod.Get, Organizations, null, user);
        using var tokenOnAdmin = await SendAsync(api.Server.Client, HttpMethod.Get, Organizations, api.Issuer.Mint(user), user: null);
        using var tokenOnClient = await SendAsync(client, HttpMethod.Get, Organizations, api.Issuer.Mint(user), user: null);

        await Assert.That(onAdmin.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(onClient.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(tokenOnAdmin.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(tokenOnClient.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    private static Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string path, string? bearer, string? user, object? body = null) =>
        SendAsync(client, method, path, bearer, user is null ? [] : [user], body);

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string path, string? bearer, string[] users, object? body = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (bearer is not null)
        {
            request.Headers.Authorization = new("Bearer", bearer);
        }

        if (users.Length > 0)
        {
            if (!request.Headers.TryAddWithoutValidation(UserHeader, users))
            {
                throw new InvalidOperationException("the user header could not be added");
            }
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request);
    }
}

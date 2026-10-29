using System.Net;
using TerraBase.Engine;

namespace TerraBase.Testing;

internal static class CollectionAccessFixture
{
    internal static async Task<DocumentApi> StartAsync()
    {
        var api = await DocumentApi.StartAsync();
        try
        {
            using var owner = api.Management.As(api.Tenant.User);
            return api;
        }
        catch
        {
            await api.DisposeAsync();
            throw;
        }
    }

    internal static CollectionAccessRules OwnerRules() => new("/owner", new CollectionAccessPolicy(true, false, []), new CollectionAccessPolicy(true, false, []));
    internal static string Rules(DocumentApi api) => $"{api.Collections}/items/rules";
    internal static async Task DeclareAsync(DocumentApi api, CollectionAccessRules rules) =>
        (await api.SendAsync(HttpMethod.Put, Rules(api), rules)).Status.ShouldBe(HttpStatusCode.OK);
    internal static HttpClient Reader(DocumentApi api, string actor, params string[] roles) =>
        api.Management.WithToken(api.Management.Issuer.Mint(actor, token =>
        {
            token.Claims["token_kind"] = "end_user";
            token.Claims["project_id"] = api.Tenant.ProjectId;
            token.Claims["instance_id"] = api.Tenant.ProjectId;
            token.Claims["roles"] = roles;
        }));
    internal static Task<ApiResponse> SendAsync(HttpClient client, HttpMethod method, string uri, object? body = null) =>
        DocumentApi.SendAsync(client, method, uri, body is null ? null : JsonContent.Create(body));
}

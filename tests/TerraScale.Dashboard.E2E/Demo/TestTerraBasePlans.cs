using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using TerraScale.Dashboard.Features.Databases;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet;

namespace TerraScale.Dashboard.E2E
{
    // The private plan endpoint sees a project only after its first database enrolls it.
    internal sealed class TestTerraBasePlans(IHttpClientFactory fallback, TestDatabases databases, Uri origin) : HttpMessageHandler, IHttpClientFactory
    {
        public ConcurrentQueue<(string Project, string Plan, HttpStatusCode Status)> Requests { get; } = new();

        public HttpClient CreateClient(string name)
        {
            return name == TerraBaseDashboardState.ClientName
            ? new(this, disposeHandler: false)
            : fallback.CreateClient(name);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            // A TerraBase without the organization plan route: the publisher falls back to each enrolled project.
            if (uri.AbsolutePath.Split('/') is ["", "admin", "organizations", var organization, "plan"] && TerraScaleIds.IsValid(organization) &&
                request.Method == HttpMethod.Put && uri.Query.Length == 0)
            {
                return new(HttpStatusCode.NotFound);
            }

            if (uri.GetLeftPart(UriPartial.Authority) != origin.GetLeftPart(UriPartial.Authority) || uri.Query.Length != 0 ||
                uri.AbsolutePath.Split('/') is not ["", "admin", "projects", var project, "plan"] || !TerraScaleIds.IsValid(project) ||
                request.Method != HttpMethod.Put || request.Headers.Authorization is not null ||
                request.Content?.Headers.ContentType?.MediaType != "application/json")
            {
                throw new InvalidOperationException("Unexpected private TerraBase plan request.");
            }

            using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
            if (body.RootElement.ValueKind != JsonValueKind.Object || body.RootElement.EnumerateObject().Count() != 1 ||
                !body.RootElement.TryGetProperty("plan", out var value) || value.GetString() is not ("hobby" or "starter" or "pro" or "team" or "enterprise"))
            {
                throw new InvalidOperationException("Unexpected TerraBase plan body.");
            }

            var scope = new DatabaseScope("", "", "", "", project, "");
            var home = (await databases.RegionsAsync(cancellationToken))[0];
            var enrolled = (await databases.ListAsync(scope, home, cancellationToken)).Count > 0;
            var status = enrolled ? HttpStatusCode.OK : HttpStatusCode.NotFound;
            Requests.Enqueue((project, value.GetString()!, status));
            return new(status);
        }
    }
}

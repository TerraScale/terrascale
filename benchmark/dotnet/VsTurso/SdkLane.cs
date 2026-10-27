using System.Net.Http.Headers;
using System.Text.Json;
using TerraScale.DotNet.TerraBase;

namespace TerraBase.E2E
{
    /// <summary>The documents SDK, with the same server, token and route as the raw HTTP lane.</summary>
    internal sealed class SdkLane : IVsTursoLane
    {
        public string Name => "sdk";

        public Task<ITarget> StartAsync(VsTursoNode node, CancellationToken cancellationToken)
        {
            var http = new HttpClient(TerraBaseHttp.CreateHandler());
            var client = new TerraBaseDocumentsClient(http, new TerraBaseDocumentsClientOptions
            {
                BaseAddress = node.Api.Server.ClientAddress!,
                AccessTokenProvider = _ => ValueTask.FromResult(node.Token),
            });
            return Task.FromResult<ITarget>(new SdkTarget(http,
                client.Database(node.Database.ProjectId!, WireLanes.DatabaseName), node.Collection, WireLanes.Engine(node)));
        }
    }

    /// <summary>Raw pooled HTTP with JSON materialization, alongside the bytes-only api control.</summary>
    internal sealed class ApiJsonLane : IVsTursoLane
    {
        public string Name => "api-json";

        public Task<ITarget> StartAsync(VsTursoNode node, CancellationToken cancellationToken)
        {
            var http = new HttpClient(TerraBaseHttp.CreateHandler()) { BaseAddress = node.Api.Server.ClientAddress! };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", node.Token);
            var path = $"/v1/projects/{node.Database.ProjectId}/databases/{WireLanes.DatabaseName}/collections/{node.Collection}/documents/by-key?id=";
            return Task.FromResult<ITarget>(new ApiJsonTarget(http, path, WireLanes.Engine(node)));
        }
    }

    internal sealed class SdkTarget(HttpClient http, TerraBaseDatabase database, string collection, ITarget preload) : ITarget
    {
        public async Task GetAsync(string id)
        {
            if (await database.GetDocumentAsync(collection, id) is not { Id: var returned } || returned != id)
            {
                throw new InvalidOperationException("sdk: the requested document was not returned");
            }
        }

        public async Task PutAsync(string id, byte[] json)
        {
            using var document = JsonDocument.Parse(json);
            _ = await database.PutDocumentAsync(collection, id, document.RootElement);
        }

        public Task PreloadAsync(IReadOnlyList<(string Id, byte[] Json)> documents)
        {
            return preload.PreloadAsync(documents);
        }

        public ValueTask DisposeAsync()
        {
            http.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    internal sealed class ApiJsonTarget(HttpClient http, string path, ITarget preload) : ITarget
    {
        public async Task GetAsync(string id)
        {
            using var response = await http.GetAsync(new Uri(path + id, UriKind.Relative));
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
            if (document.RootElement.GetProperty("id").GetString() != id)
            {
                throw new InvalidOperationException("api-json: the requested document was not returned");
            }
        }

        public async Task PutAsync(string id, byte[] json)
        {
            using var content = new ByteArrayContent(json);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var response = await http.PutAsync(new Uri(path + id, UriKind.Relative), content);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        }

        public Task PreloadAsync(IReadOnlyList<(string Id, byte[] Json)> documents)
        {
            return preload.PreloadAsync(documents);
        }

        public ValueTask DisposeAsync()
        {
            http.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

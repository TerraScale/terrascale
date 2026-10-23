using System.Net.Http.Headers;

namespace TerraBase.E2E
{
    /// <summary>The public API over loopback: HTTP/1.1 keep-alive, one pooled connection per concurrent worker, bodies read in full.</summary>
    internal sealed class ApiTarget : ITarget
    {
        private static readonly MediaTypeHeaderValue Json = new("application/json");
        private readonly HttpClient _client;
        private readonly string _path;

        // The target behind the same listener preloads: HTTP would only add its own cost to a phase nobody measures.
        private readonly ITarget _preload;

        // GET /healthz for every operation: the listener, middleware and client without auth, catalog or storage.
        private readonly bool _floor;

        /// <summary>A client of <paramref name="address"/>; <paramref name="token"/>, when set, goes as a bearer token.</summary>
        public ApiTarget(Uri address, string? token, string path, ITarget preload, bool floor = false)
        {
            _preload = preload;
            _floor = floor;
            _client = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = Timeout.InfiniteTimeSpan, UseCookies = false })
            {
                BaseAddress = address,
            };
            if (token is not null)
            {
                _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            _path = path;
        }

        public async Task PutAsync(string id, byte[] json)
        {
            if (_floor)
            {
                await GetAsync(id);
                return;
            }

            using var content = new ByteArrayContent(json);
            content.Headers.ContentType = Json;
            using var response = await _client.PutAsync(new Uri(_path + id, UriKind.Relative), content);
            _ = await response.Content.ReadAsByteArrayAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"api: PUT {id} answered {(int)response.StatusCode}");
            }
        }

        public Task PreloadAsync(IReadOnlyList<(string Id, byte[] Json)> documents)
        {
            return _floor ? Task.CompletedTask : _preload.PreloadAsync(documents);
        }

        public async Task GetAsync(string id)
        {
            using var response = await _client.GetAsync(new Uri(_path + id, UriKind.Relative));
            _ = await response.Content.ReadAsByteArrayAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"api: GET {id} answered {(int)response.StatusCode}");
            }
        }

        public ValueTask DisposeAsync()
        {
            _client.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

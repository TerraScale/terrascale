#if TERRASCALE_SDK
using System.Net;
using System.Text;
using TerraScale.DotNet;

namespace TerraBase.Benchmarks;

/// <summary>
/// The ts-auth management API lane driven through the official
/// TerraScale.DotNet SDK (<see cref="TerraScaleAuthClient"/>). A minimal
/// ts-auth-shaped ASP.NET stub serves canned user records over in-process
/// TestServer dispatch (no socket), so every measured operation pays the full
/// official client path — typed request, JSON serialization, HttpClient,
/// ASP.NET routing, typed response decode, response verification — against a
/// constant service cost. This isolates client-path overhead the way the
/// embedded lanes isolate engine cost.
/// </summary>
internal static partial class EngineComparisonWorkload
{
    private sealed class TsAuthSdkBackend : IComparisonBackend
    {
        private const string Slug = "bench";

        private readonly StubHandler handler;
        private readonly HttpClient http;
        private readonly TerraScaleAuthClient client;
        private readonly string[] users;
        private readonly int datasetSize;
        private readonly int userBytes;

        private TsAuthSdkBackend(StubHandler handler, HttpClient http, TerraScaleAuthClient client, string[] users, int datasetSize, int userBytes)
        {
            this.handler = handler;
            this.http = http;
            this.client = client;
            this.users = users;
            this.datasetSize = datasetSize;
            this.userBytes = userBytes;
        }

        public string Engine => "tsauth-sdk";

        public string? EngineNotes =>
            "ts-auth management API stub (canned users, constant service cost) driven through the official TerraScaleAuthClient over a raw in-process handler with no socket and no web stack, the same dispatch class the terrabase-sdk embedded lane occupies; read = user get, query = cursor page of 50, write = alternating user create and tri-state patch; every response is verified; ReturnedBytes/LogicalBytes are the precomputed canned response lengths";

        public static ValueTask<TsAuthSdkBackend> OpenAsync(
            string storagePath,
            int datasetSize,
            int payloadSize,
            CancellationToken cancellationToken)
        {
            // The stub is in-memory: storage path and durability do not apply.
            _ = storagePath;
            var payload = new string('x', payloadSize);
            var users = new string[datasetSize];
            var userBytes = 0;
            for (var index = 0; index < datasetSize; index++)
            {
                // Wire shape mirrors the ts-auth user detail view (snake_case).
                users[index] = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "{{\"id\":\"usr_{0}\",\"email\":\"bench-{0}@example.test\",\"email_verified\":true,"
                    + "\"username\":\"bench{0}\",\"name\":\"Bench User {0}\",\"banned\":false,\"ban_reason\":null,"
                    + "\"created_at\":1700000000,\"last_sign_in_at\":null,"
                    + "\"public_metadata\":{{\"index\":{0},\"payload\":\"{1}\"}},"
                    + "\"private_metadata\":{{\"plan\":\"pro\",\"payload\":\"{1}\"}}}}",
                    index,
                    payload);
                userBytes = Encoding.UTF8.GetByteCount(users[index]);
            }

            var handler = new StubHandler(users, datasetSize, "/v1/" + Slug);
            var http = new HttpClient(handler)
            {
                // Loopback keeps the SDK's endpoint validation happy; the
                // handler answers in process and never dials a socket.
                BaseAddress = new Uri("http://127.0.0.1/"),
            };
            var client = new TerraScaleAuthClient(
                http,
                new TerraScaleAuthClientOptions
                {
                    BaseAddress = http.BaseAddress,
                    ApiKey = "sk_benchmark_key",
                    InstanceSlug = Slug,
                });
            return ValueTask.FromResult(new TsAuthSdkBackend(handler, http, client, users, datasetSize, userBytes));
        }

        /// <summary>
        /// In-process ts-auth stub: canned user records behind the exact route
        /// shapes the official client requests, with a constant service cost
        /// (route switch + bearer check) so the lane measures the client path.
        /// </summary>
        private sealed class StubHandler(string[] users, int datasetSize, string instancePrefix) : HttpMessageHandler
        {
            private static readonly byte[] UnauthorizedBody =
                """{"error":{"code":"unauthorized","message":"Missing or invalid credentials."}}"""u8.ToArray();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var path = request.RequestUri?.AbsolutePath ?? string.Empty;
                var method = request.Method;
                if (!path.StartsWith(instancePrefix + "/users", StringComparison.Ordinal) ||
                    !string.Equals(request.Headers.Authorization?.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase))
                {
                    return Json(HttpStatusCode.Unauthorized, UnauthorizedBody);
                }

                var remainder = path[(instancePrefix.Length + "/users".Length)..];
                if (method == HttpMethod.Get && remainder.Length == 0)
                {
                    return ListUsers(request);
                }

                if (remainder.Length > 1 && remainder.StartsWith('/'))
                {
                    var userId = remainder[1..];
                    if (method == HttpMethod.Get && int.TryParse(userId.AsSpan("usr_".Length), out var index) && index >= 0 && index < datasetSize)
                    {
                        return Json(HttpStatusCode.OK, Encoding.UTF8.GetBytes(users[index]));
                    }

                    if (method == HttpMethod.Patch && userId == "usr_0")
                    {
                        var body = await (request.Content ?? throw new InvalidOperationException("patch without a body")).ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                        using var document = System.Text.Json.JsonDocument.Parse(body);
                        var root = document.RootElement;
                        var name = root.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == System.Text.Json.JsonValueKind.String
                            ? nameElement.GetString()
                            : "Bench User 0";
                        var banned = root.TryGetProperty("banned", out var bannedElement) && bannedElement.ValueKind == System.Text.Json.JsonValueKind.True;
                        var patched = string.Format(
                            System.Globalization.CultureInfo.InvariantCulture,
                            "{{\"id\":\"usr_0\",\"email\":\"bench-0@example.test\",\"email_verified\":true,\"username\":\"bench0\",\"name\":\"{0}\",\"banned\":{1},\"ban_reason\":null,\"created_at\":1700000000,\"last_sign_in_at\":null,\"public_metadata\":{{\"index\":0}},\"private_metadata\":{{\"plan\":\"pro\"}}}}",
                            name,
                            banned ? "true" : "false");
                        return Json(HttpStatusCode.OK, Encoding.UTF8.GetBytes(patched));
                    }
                }

                if (method == HttpMethod.Post && remainder.Length == 0)
                {
                    var body = await (request.Content ?? throw new InvalidOperationException("create without a body")).ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    using var document = System.Text.Json.JsonDocument.Parse(body);
                    var email = document.RootElement.TryGetProperty("email", out var emailElement) ? emailElement.GetString() : null;
                    var created = string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "{{\"id\":\"usr_created\",\"email\":\"{0}\",\"email_verified\":false,\"username\":null,\"name\":null,\"banned\":false,\"ban_reason\":null,\"created_at\":1700000001,\"last_sign_in_at\":null,\"public_metadata\":{{}},\"private_metadata\":{{}}}}",
                        email);
                    // The stub answers 200; the official client accepts any 2xx
                    // for create, so the measured path is identical to 201.
                    return Json(HttpStatusCode.OK, Encoding.UTF8.GetBytes(created));
                }

                return Json(HttpStatusCode.NotFound, """{"error":{"code":"user_not_found","message":"no user with this id in this instance"}}"""u8.ToArray());
            }

            private HttpResponseMessage ListUsers(HttpRequestMessage request)
            {
                var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
                if (!int.TryParse(query["limit"], out var limit) || limit is < 1 or > 100)
                {
                    return Json(HttpStatusCode.BadRequest, """{"error":{"code":"invalid_request","message":"limit must be between 1 and 100"}}"""u8.ToArray());
                }

                var after = 0;
                if (query["starting_after"] is { Length: >= 5 } && int.TryParse(query["starting_after"].AsSpan("usr_".Length), out var parsed))
                {
                    after = parsed;
                }

                var rows = new string[limit];
                for (var offset = 0; offset < limit; offset++)
                {
                    // Cursor pages wrap around the dataset so every page is full.
                    rows[offset] = users[(after + 1 + offset) % datasetSize];
                }

                return Json(HttpStatusCode.OK, Encoding.UTF8.GetBytes("{\"data\":[" + string.Join(",", rows) + "]}"));
            }

            private static HttpResponseMessage Json(HttpStatusCode statusCode, byte[] body)
            {
                var content = new ByteArrayContent(body);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                return new HttpResponseMessage(statusCode) { Content = content };
            }
        }

        public ValueTask SeedAsync(int index, CancellationToken cancellationToken) =>
            // The stub precomputes every seeded user at open; nothing to write.
            ValueTask.CompletedTask;

        public async ValueTask<(long ReturnedBytes, long LogicalBytes)> ReadAsync(int index, int slot, CancellationToken cancellationToken)
        {
            var user = await client.Users.GetAsync("usr_" + (index % datasetSize).ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
            if (!user.Id.StartsWith("usr_", StringComparison.Ordinal) || user.EmailVerified != true || user.PublicMetadata is null || user.PublicMetadata.Count == 0)
            {
                throw new InvalidOperationException($"ts-auth SDK read returned an invalid user for slot {slot}: {user.Id}.");
            }

            return (userBytes, userBytes);
        }

        public async ValueTask<(long ReturnedBytes, long LogicalBytes)> QueryAsync(int index, int slot, CancellationToken cancellationToken)
        {
            var after = (index % datasetSize).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var page = await client.Users.ListAsync(limit: 50, startingAfter: "usr_" + after, cancellationToken).ConfigureAwait(false);
            var expectedFirst = ((index % datasetSize) + 1) % datasetSize;
            if (page.Data.Count != 50 || page.Data[0].Id != "usr_" + expectedFirst.ToString(System.Globalization.CultureInfo.InvariantCulture))
            {
                throw new InvalidOperationException($"ts-auth SDK query returned an invalid page for slot {slot}: {page.Data.Count} rows starting at {(page.Data.Count > 0 ? page.Data[0].Id : "(empty)")}.");
            }

            long bytes = 9 + 50 * userBytes;
            return (bytes, bytes);
        }

        public async ValueTask<(long ReturnedBytes, long LogicalBytes)> WriteAsync(long sequence, int slot, CancellationToken cancellationToken)
        {
            if (sequence % 2 == 0)
            {
                var email = $"bench-write-{sequence}@example.test";
                var created = await client.Users.CreateAsync(email: email, name: $"Write User {sequence}", cancellationToken: cancellationToken).ConfigureAwait(false);
                if (created.Id != "usr_created" || created.Email != email)
                {
                    throw new InvalidOperationException($"ts-auth SDK create returned an invalid user for sequence {sequence}.");
                }

                return (320, 320);
            }
            else
            {
                var updated = await client.Users.UpdateAsync(
                    "usr_0",
                    patch => patch.Name($"Renamed {sequence}").Banned(sequence % 4 == 1),
                    cancellationToken).ConfigureAwait(false);
                if (updated.Id != "usr_0" || updated.Name != $"Renamed {sequence}")
                {
                    throw new InvalidOperationException($"ts-auth SDK patch returned an invalid user for sequence {sequence}.");
                }

                return (240, 240);
            }
        }

        public ValueTask<ComparisonQueryPlan?> CaptureQueryPlanAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<ComparisonQueryPlan?>(null);

        public ValueTask DisposeAsync()
        {
            http.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
#endif

using System.Globalization;
using System.Buffers;
using System.Buffers.Text;
using System.Text;
using System.Text.Encodings.Web;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Grpc.Core;
using MagicOnion.Server;
using MessagePack;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using TerraScale.TerraBase.Contracts;

namespace TerraScale.TerraBase.Rpc
{
    // A response feature that runs its start and completion callbacks after the pipeline.
    internal interface IFinishable
    {
        Task FinishAsync();
    }

    /// <summary>A REST answer: its status, headers and complete body.</summary>
    public sealed record RestAnswer(int Status, IHeaderDictionary Headers, ReadOnlyMemory<byte> Body);

    /// <summary>
    /// Serves an RPC through the host's REST pipeline in process. The request carries the call's bearer token or peer
    /// credential and nothing else from the caller, so REST authorization decides exactly as for the public route.
    /// </summary>
    public sealed class RestApi(RestPipeline pipeline, IServiceScopeFactory scopes)
    {
        private const string JsonMediaType = "application/json";

        /// <summary>Sends one REST request for <paramref name="call"/> and returns the complete answer.</summary>
        public async Task<RestAnswer> SendAsync(
            ServiceContext call, string method, string pathAndQuery, ReadOnlyMemory<byte>? body = null, string? contentType = null,
            Action<IHeaderDictionary>? headers = null)
        {
            ArgumentNullException.ThrowIfNull(call);
            var request = Request(call, method, pathAndQuery, JsonMediaType, forwardCredentials: true);
            if (body is { } sent)
            {
                request.Body = new MemoryStream(sent.ToArray(), writable: false);
                request.Headers.ContentType = contentType ?? JsonMediaType;
                request.Headers.ContentLength = sent.Length;
            }

            headers?.Invoke(request.Headers);
            using var response = new RestResponse(TerraBaseRpc.MaxMessageBytes);
            await RunAsync(pipeline, scopes, call, request, response, response).ConfigureAwait(false);
            return new RestAnswer(response.StatusCode, response.Headers, response.Written.ToArray());
        }

        // The in-process request of a call. Only the call's bearer token and peer credential travel from the caller;
        // forwardCredentials: false sends neither, for the anonymous public routes.
        internal static HttpRequestFeature Request(ServiceContext call, string method, string pathAndQuery, string accept, bool forwardCredentials)
        {
            ArgumentNullException.ThrowIfNull(pathAndQuery);
            var outer = call.CallContext.GetHttpContext();
            var split = pathAndQuery.IndexOf('?', StringComparison.Ordinal);
            var request = new HttpRequestFeature
            {
                Method = method,
                Scheme = "http",
                Protocol = "HTTP/2",
                PathBase = string.Empty,
                Path = split < 0 ? pathAndQuery : pathAndQuery[..split],
                QueryString = split < 0 ? string.Empty : pathAndQuery[split..],
                RawTarget = pathAndQuery,
                Body = Stream.Null,
            };
            request.Headers.Host = outer.Request.Host.HasValue ? outer.Request.Host.Value : IPAddress.Loopback.ToString();
            request.Headers.Accept = accept;
            if (forwardCredentials)
            {
                request.Headers[TerraBaseRpc.PeerCredentialHeader] = outer.Request.Headers[TerraBaseRpc.PeerCredentialHeader];
                if (outer.Request.Headers.Authorization.Count > 0)
                {
                    request.Headers.Authorization = outer.Request.Headers.Authorization;
                }
            }

            return request;
        }

        // Runs the request through the captured pipeline: as the client listener when it carries a bearer token (or is
        // anonymous), otherwise as the operator. Start and completion callbacks run once the pipeline finishes.
        internal static async Task RunAsync(
            RestPipeline pipeline, IServiceScopeFactory scopes, ServiceContext call, HttpRequestFeature request, IHttpResponseFeature response, IHttpResponseBodyFeature body)
        {
            var outer = call.CallContext.GetHttpContext();
            await using var scope = scopes.CreateAsyncScope();
            var features = new FeatureCollection();
            features.Set<IHttpRequestFeature>(request);
            features.Set(response);
            features.Set(body);
            features.Set<IHttpRequestLifetimeFeature>(new HttpRequestLifetimeFeature { RequestAborted = call.CallContext.CancellationToken });
            features.Set<IHttpRequestIdentifierFeature>(new HttpRequestIdentifierFeature());
            features.Set<IItemsFeature>(new ItemsFeature());
            features.Set<IServiceProvidersFeature>(new ServiceProvidersFeature { RequestServices = scope.ServiceProvider });
            features.Set<IHttpConnectionFeature>(new HttpConnectionFeature
            {
                ConnectionId = outer.Connection.Id,
                RemoteIpAddress = outer.Connection.RemoteIpAddress,
                RemotePort = outer.Connection.RemotePort,
                LocalIpAddress = outer.Connection.LocalIpAddress,
                LocalPort = outer.Connection.LocalPort,
            });
            pipeline.Connection(features, request.Headers.Authorization.Count > 0 || request.Headers[TerraBaseRpc.PeerCredentialHeader].Count == 0);
            try
            {
                await pipeline.Pipeline(new DefaultHttpContext(features)).ConfigureAwait(false);
            }
            finally
            {
                await ((IFinishable)response).FinishAsync().ConfigureAwait(false);
            }
        }

        /// <summary>The UTF-8 JSON of <paramref name="value"/>.</summary>
        public static ReadOnlyMemory<byte> Json<T>(T value, JsonTypeInfo<T> type)
        {
            var buffer = new ArrayBufferWriter<byte>();
            using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                MaxDepth = 256,
            });
            JsonSerializer.Serialize(writer, value, type);
            writer.Flush();
            return Compact(buffer.WrittenSpan);
        }

        // JSON API payloads keep optional Unicode escapes compact, including C1 characters that the encoder's
        // global blocklist escapes even with UnsafeRelaxedJsonEscaping. Required JSON escapes stay intact.
        private static ReadOnlyMemory<byte> Compact(ReadOnlySpan<byte> json)
        {
            var output = new ArrayBufferWriter<byte>(json.Length);
            for (var index = 0; index < json.Length;)
            {
                if (json[index] == '\\' && index + 1 < json.Length)
                {
                    if (TryRune(json[index..], out var rune, out var consumed))
                    {
                        var bytes = rune.EncodeToUtf8(output.GetSpan(4));
                        output.Advance(bytes);
                        index += consumed;
                        continue;
                    }
                    // Consume escaped backslashes together, so literal \\u text cannot become a Unicode escape.
                    json.Slice(index, 2).CopyTo(output.GetSpan(2));
                    output.Advance(2);
                    index += 2;
                    continue;
                }
                output.GetSpan(1)[0] = json[index++];
                output.Advance(1);
            }
            return output.WrittenMemory;
        }

        private static bool TryRune(ReadOnlySpan<byte> json, out Rune rune, out int consumed)
        {
            rune = default;
            consumed = 0;
            if (json.Length < 6 || json[1] != 'u' || !Utf8Parser.TryParse(json.Slice(2, 4), out ushort value, out var digits, 'X') ||
                digits != 4 || value < 0x20 || value is '"' or '\\')
            {
                return false;
            }
            consumed = 6;
            if (Rune.TryCreate((char)value, out rune))
            {
                return true;
            }
            if (json.Length >= 12 && json[6] == '\\' && json[7] == 'u' &&
                Utf8Parser.TryParse(json.Slice(8, 4), out ushort low, out digits, 'X') && digits == 4 &&
                Rune.TryCreate((char)value, (char)low, out rune))
            {
                consumed = 12;
                return true;
            }
            return false;
        }

        /// <summary>The answer's JSON body; a refusal throws its fault.</summary>
        public static T Read<T>(RestAnswer answer, JsonTypeInfo<T> type)
        {
            Ensure(answer);
            try
            {
                return JsonSerializer.Deserialize(answer.Body.Span, type)
                    ?? throw new JsonException("The REST response is null.");
            }
            catch (JsonException)
            {
                throw Fault(answer with
                {
                    Status = StatusCodes.Status502BadGateway,
                    Body = "{\"error\":{\"code\":\"invalid_response\",\"message\":\"TerraBase returned an invalid response.\"}}"u8.ToArray(),
                });
            }
        }

        /// <summary>Throws the fault of a refusal.</summary>
        public static void Ensure(RestAnswer answer)
        {
            ArgumentNullException.ThrowIfNull(answer);
            if (answer.Status is < 200 or > 299)
            {
                throw Fault(answer);
            }
        }

        /// <summary>The fault of a refusal: TerraBase's status, error envelope, region headers and Retry-After.</summary>
        public static TerraBaseFault FaultOf(RestAnswer answer)
        {
            ArgumentNullException.ThrowIfNull(answer);
            string? code = null, message = null, region = null, sqliteCode = null;
            int? op = null;
            List<TerraBaseFaultDetail>? details = null;
            try
            {
                if (!answer.Body.IsEmpty)
                {
                    using var json = JsonDocument.Parse(answer.Body);
                    if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                    {
                        code = Text(error, "code");
                        message = Text(error, "message");
                        region = Text(error, "region");
                        sqliteCode = Text(error, "sqlite_code");
                        op = error.TryGetProperty("op", out var index) && index.ValueKind == JsonValueKind.Number && index.TryGetInt32(out var value) ? value : null;
                        if (error.TryGetProperty("details", out var issues) && issues.ValueKind == JsonValueKind.Array)
                        {
                            details = [.. issues.EnumerateArray().Take(256)
                                .Where(issue => issue.ValueKind == JsonValueKind.Object && Text(issue, "path") is not null && Text(issue, "keyword") is not null)
                                .Select(issue => new TerraBaseFaultDetail(Text(issue, "path")!, Text(issue, "keyword")!))];
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // An unreadable refusal still reports its status.
            }

            var home = Header(answer, "X-TerraBase-Database-Home");
            var regions = Header(answer, "X-TerraBase-Database-Regions")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var retry = Header(answer, HeaderNames.RetryAfter) is { } seconds && int.TryParse(seconds, NumberStyles.None, CultureInfo.InvariantCulture, out var wait) ? wait : (int?)null;
            return new TerraBaseFault(answer.Status, code ?? "http_" + answer.Status.ToString(CultureInfo.InvariantCulture),
                message ?? $"TerraBase answered HTTP {answer.Status}", region ?? Header(answer, "X-TerraBase-Region"), home, regions, op, retry, details, sqliteCode);
        }

        /// <summary>The gRPC refusal that carries the answer's fault in <see cref="TerraBaseRpc.FaultTrailer"/>.</summary>
        public static RpcException Fault(RestAnswer answer)
        {
            var fault = FaultOf(answer);
            var trailers = new Metadata { { TerraBaseRpc.FaultTrailer, MessagePackSerializer.Serialize(fault, TerraBaseRpc.SerializerOptions) } };
            var status = fault.Status switch
            {
                400 or 413 or 415 => StatusCode.InvalidArgument,
                401 => StatusCode.Unauthenticated,
                403 => StatusCode.PermissionDenied,
                404 or 410 => StatusCode.NotFound,
                409 => StatusCode.Aborted,
                412 or 421 => StatusCode.FailedPrecondition,
                429 => StatusCode.ResourceExhausted,
                503 => StatusCode.Unavailable,
                408 or 504 => StatusCode.DeadlineExceeded,
                _ => StatusCode.Unknown,
            };
            return new RpcException(new Status(status, fault.Message), trailers);
        }

        private static string? Text(JsonElement element, string name)
        {
            return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }

        private static string? Header(RestAnswer answer, string name)
        {
            return answer.Headers.TryGetValue(name, out var values) && values.Count == 1 ? values[0] : null;
        }
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;
using TerraScale.Contracts.Http;

namespace TerraBase.Features.Sql;

public sealed record SocketFrame(
    long? Id,
    string? Type,
    IReadOnlyList<SqlHttpStatement>? Statements,
    string? Token = null,
    string? Consistency = null,
    string? Bookmark = null);

public sealed record LegacySocketResult(long? Id, string Type, IReadOnlyList<LegacyResultBody> Results, bool Transaction);

public sealed record LegacySocketErrorBody(string Code, string Message, int Statement, string Sqlstate);

public sealed record LegacySocketError(long? Id, string Type, LegacySocketErrorBody Error, bool Transaction);

public sealed record SocketServerFrame(string Type, long Id, IReadOnlyList<ResultBody>? Results, string? Bookmark, bool Transaction, ApiErrorBody? Error);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SocketFrame))]
[JsonSerializable(typeof(LegacySocketResult))]
[JsonSerializable(typeof(LegacySocketError))]
[JsonSerializable(typeof(SocketServerFrame))]
internal sealed partial class SqlSocketJsonContext : JsonSerializerContext;

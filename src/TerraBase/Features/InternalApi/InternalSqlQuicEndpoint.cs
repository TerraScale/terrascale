using System.Diagnostics;
using System.Text.Json;
using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Features.Sql;
using TerraBase.Hosting;
using TerraScale.Contracts;
using TerraScale.Contracts.Http;
using TerraScale.Contracts.Quic;
using TerraScale.QuicRpc;
using TerraScale.QuicRpc.Messages;

namespace TerraBase.Features.InternalApi;

internal sealed class InternalSqlQuicEndpoint(DatabaseAccess access, SqlRunner runner) : IQuicEndpoints
{
    private static readonly PeerCaller Platform = new();

    public void Map(QuicRpcHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _ = host.MapUnary<InternalSqlRequest, InternalSqlReply>(QuicMessageType.InternalSqlExecute, HandleAsync);
    }

    private async ValueTask<QuicHandlerOutcome<InternalSqlReply>> HandleAsync(QuicCallContext context, InternalSqlRequest request, CancellationToken cancellationToken) =>
        Causality(request) switch
        {
            SqlCausality causality => Statements(request.Statements) switch
            {
                IReadOnlyList<SqlStatement> statements => await RunAsync(request.DatabaseId, statements, causality, cancellationToken),
                SqlRefusal refusal => Refused(refusal.Status, refusal.Body),
            },
            SqlRefusal refusal => Refused(refusal.Status, refusal.Body),
        };

    private async ValueTask<InternalSqlReply> RunAsync(Ulid databaseId, IReadOnlyList<SqlStatement> statements, SqlCausality causality, CancellationToken cancellationToken)
    {
        var outcome = await access.DataByIdAsync(Platform, databaseId, cancellationToken);
        return outcome switch
        {
            ResolvedDatabase target => await runner.RunAsync(target, statements, causality, cancellationToken) switch
            {
                SqlRun run => Answer(run),
                SqlRefusal refusal => Refused(refusal.Status, refusal.Body),
            },
            InvalidInput invalid => Refused(StatusCodes.Status400BadRequest, invalid.Code, invalid.Message),
            CredentialRejected unauthorized => Refused(StatusCodes.Status401Unauthorized, unauthorized.Code, unauthorized.Message),
            CallerForbidden forbidden => Refused(StatusCodes.Status403Forbidden, forbidden.Code, forbidden.Message, forbidden.Details),
            ResourceNotFound missing => Refused(StatusCodes.Status404NotFound, missing.Code, missing.Message),
            BackendUnavailable unavailable => Refused(StatusCodes.Status503ServiceUnavailable, unavailable.Code, unavailable.Message),
        };
    }

    private static SqlCausalityOutcome Causality(InternalSqlRequest request)
    {
        if (!CausalBookmark.TryDecode(request.Bookmark, out var after))
        {
            return new SqlRefusal("invalid_bookmark", "the bookmark is not one returned by TerraScale", "22023", 0, StatusCodes.Status400BadRequest);
        }

        ConsistencyMode? mode = request.Consistency switch
        {
            SqlConsistencyHint.DatabaseDefault => null,
            SqlConsistencyHint.Fast => ConsistencyMode.Fast,
            SqlConsistencyHint.Strong => ConsistencyMode.Strong,
            _ => throw new UnreachableException(),
        };
        return new SqlCausality(mode, after);
    }

    private static SqlStatementsParse Statements(ReadOnlyMemory<byte> payload)
    {
        SqlExecuteRequest? request;
        try
        {
            request = JsonSerializer.Deserialize(payload.Span, TerraScaleContractsJsonContext.Default.SqlExecuteRequest);
        }
        catch (JsonException)
        {
            request = null;
        }

        return request is null ? SqlRefusal.Invalid("the statements payload is not a SqlExecuteRequest", 0) : SqlWire.Statements(request.Statements);
    }

    private static InternalSqlReply Answer(SqlRun run)
    {
        var bookmark = run.Bookmark.Encode();
        var body = new ExecuteBody(SqlWire.Results(run.Results), bookmark, ConsistencyHeaders.Format(run.Mode));
        return new InternalSqlReply(StatusCodes.Status200OK, bookmark, JsonSerializer.SerializeToUtf8Bytes(body, SqlJsonContext.Default.ExecuteBody));
    }

    private static InternalSqlReply Refused(int status, string code, string? message, IReadOnlyDictionary<string, string>? details = null) =>
        Refused(status, ErrorResults.Body(status, code, message, details));

    private static InternalSqlReply Refused(int status, ApiErrorBody error) =>
        new((ushort)status, "", JsonSerializer.SerializeToUtf8Bytes(new ApiErrorEnvelope(error), TerraScaleContractsJsonContext.Default.ApiErrorEnvelope));
}

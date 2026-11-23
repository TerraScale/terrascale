using System.Diagnostics;
using System.Globalization;
using System.Text;
using TerraBase.Engine;
using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Sql;

internal readonly union ExportDatabaseResponse(SqlDump, InvalidInput, CredentialRejected, CallerForbidden, ResourceNotFound, BackendUnavailable);

public sealed record ExportInput(string? Project, string? Database);

internal sealed class ExportDatabaseEndpoint(DatabaseAccess access)
    : Endpoint<ExportInput, ExportDatabaseResponse>
{
    public override void Configure()
    {
        Get(SqlRoute.Database + "/export");
        Name("exportDatabase");
        Summary("Streams the database as a SQL dump");
        Description("Answers text/plain SQL: the schema followed by one INSERT per row, readable by sqlite3 and the SQL API. Needs sql:read; end-user tokens are refused.");
        Tags("Queries");
        AllowAnonymous();
        Metadata(ListenerMetadata.Client);
    }

    public override async ValueTask<ExportDatabaseResponse> HandleAsync(
        ExportInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var outcome = await access.DataAsync(HttpContext.Caller, request.Project, request.Database, cancellationToken);
        return outcome switch
        {
            ResolvedDatabase { EndUser: not null } => new CallerForbidden("end users cannot export a database", "forbidden_scope"),
            ResolvedDatabase target => await access.OpenAsync(target.Database, cancellationToken) switch
            {
                ISqlDatabaseSession session => new SqlDump(session, target.Database.Name),
                BackendUnavailable unavailable => unavailable,
            },
            InvalidInput invalid => invalid,
            CredentialRejected unauthorized => unauthorized,
            CallerForbidden forbidden => forbidden,
            ResourceNotFound missing => missing,
            BackendUnavailable unavailable => unavailable,
        };
    }
}

public sealed class SqlDump(ISqlDatabaseSession session, string name) : IResult
{
    private const int Page = 1000;

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        var cancellationToken = httpContext.RequestAborted;
        httpContext.Response.ContentType = "application/sql; charset=utf-8";
        httpContext.Response.Headers.ContentDisposition = string.Concat("attachment; filename=\"", name, ".sql\"");
        httpContext.Response.Headers.CacheControl = "no-store";
        if (await ReadAsync("SELECT type, name, sql FROM sqlite_schema WHERE sql IS NOT NULL AND substr(name, 1, 4) <> '_tb_' AND substr(tbl_name, 1, 4) <> '_tb_' "
            + "AND substr(name, 1, 7) <> 'sqlite_' ORDER BY CASE type WHEN 'table' THEN 0 WHEN 'index' THEN 1 ELSE 2 END, name",
            cancellationToken) is not { } schema)
        {
            httpContext.Abort();
            return;
        }

        var writer = httpContext.Response.BodyWriter;
        await writer.WriteAsync("BEGIN;\n"u8.ToArray(), cancellationToken);
        foreach (var row in schema.Where(static row => row.TextAt(0) == "table"))
        {
            await writer.WriteAsync(Encoding.UTF8.GetBytes(string.Concat(row.TextAt(2), ";\n")), cancellationToken);
            for (var offset = 0L; ; offset += Page)
            {
                if (await ReadAsync(string.Create(CultureInfo.InvariantCulture, $"SELECT * FROM {SqlBuild.Quote(row.TextAt(1))} LIMIT {Page} OFFSET {offset}"),
                    cancellationToken) is not { } rows)
                {
                    httpContext.Abort();
                    return;
                }

                var text = new StringBuilder();
                foreach (var data in rows)
                {
                    text.Append("INSERT INTO ").Append(SqlBuild.Quote(row.TextAt(1))).Append(" VALUES(")
                        .AppendJoin(',', data.Select(Literal)).Append(");\n");
                }

                await writer.WriteAsync(Encoding.UTF8.GetBytes(text.ToString()), cancellationToken);
                if (rows.Count < Page)
                {
                    break;
                }
            }
        }

        foreach (var row in schema.Where(static row => row.TextAt(0) != "table"))
        {
            await writer.WriteAsync(Encoding.UTF8.GetBytes(string.Concat(row.TextAt(2), ";\n")), cancellationToken);
        }

        await writer.WriteAsync("COMMIT;\n"u8.ToArray(), cancellationToken);
    }

    private async Task<IReadOnlyList<IReadOnlyList<SqlValue>>?> ReadAsync(string sql, CancellationToken cancellationToken)
    {
        var request = new SqlStatementRequest(session.DatabaseId, [SqlBuild.Statement(sql)], SqlAccess.Read, ConsistencyMode.Fast, CausalBookmark.Empty);
        var outcome = await session.ExecuteReadAsync(request, cancellationToken);
        return outcome switch
        {
            SqlReadResult { ResultSets: [.., var last] } => last.Rows,
            SqlReadResult => [],
            SqlError or StorageBusy or StorageTimeout => null,
        };
    }

    private static string Literal(SqlValue value) => value.Kind switch
    {
        SqlValueKind.Null => "NULL",
        SqlValueKind.Integral or SqlValueKind.Real => SqlBuild.Format(value),
        SqlValueKind.Text => string.Concat("'", SqlBuild.Format(value).Replace("'", "''", StringComparison.Ordinal), "'"),
        SqlValueKind.Blob => value.TryGetBlob(out var blob) ? string.Concat("X'", Convert.ToHexString(blob.Span), "'") : "NULL",
        _ => throw new UnreachableException(),
    };
}

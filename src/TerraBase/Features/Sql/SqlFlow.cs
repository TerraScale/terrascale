using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipelines;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Features.Management.Logs;
using TerraBase.Hosting;
using TerraScale.Contracts.Http;
using TerraScale.Endpoints.Binding;
using BoundGlobalSqlRequest = TerraScale.Endpoints.Binding.Bound<TerraBase.Features.Sql.SqlRequestInput>;
using BoundSqlRequest = TerraScale.Endpoints.Binding.Bound<TerraBase.Features.Sql.SqlExecuteInput>;

namespace TerraBase.Features.Sql;

internal readonly union SqlRunResolution(SqlRun, SqlRefusal, InvalidInput, CredentialRejected, CallerForbidden, ResourceNotFound, BackendUnavailable);

internal sealed class SqlFlow(DatabaseAccess access, SqlRunner runner)
{
    public const int MaxRequestBytes = 1024 * 1024;

    public static SqlExecuteInput OversizedRequest { get; } = new(null, null, null);

    public static SqlRequestInput OversizedRequestInput { get; } = new(null);

    public static RequestSizeLimitAttribute RequestSizeLimit { get; } = new(MaxRequestBytes);

    public static async ValueTask<BoundSqlRequest> BindAsync(
        HttpRequest request, Func<CancellationToken, ValueTask<BoundSqlRequest>> bind, CancellationToken cancellationToken)
    {
        if (request.ContentLength > MaxRequestBytes)
        {
            return OversizedRequest;
        }

        var hasBody = HasBody(request);
        if (!hasBody)
        {
            return EmptyExecuteInput(request);
        }

        var isUtf8 = IsUtf8Json(request, out var isJson);
        if (!isJson)
        {
            if (string.IsNullOrEmpty(request.ContentType) && request.ContentLength is null)
            {
                var probe = await ProbeEmptyBodyAsync(request.BodyReader, cancellationToken);
                return probe switch
                {
                    BodyProbe.Empty => EmptyExecuteInput(request),
                    BodyProbe.HasData => new UnsupportedMediaType("Expected a JSON request body."),
                    BodyProbe.Oversized => OversizedRequest,
                    _ => throw new UnreachableException(),
                };
            }

            return new UnsupportedMediaType("Expected a JSON request body.");
        }

        if (!isUtf8)
        {
            try
            {
                return await bind(cancellationToken);
            }
            catch (BadHttpRequestException error) when (error.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                return OversizedRequest;
            }
        }

        var read = await ReadAndParseAsync(request, cancellationToken);
        if (read.Oversized)
        {
            return OversizedRequest;
        }

        if (read.InvalidSyntax)
        {
            return new Validation("The JSON request body is invalid.");
        }

        var durability = read.Durability ?? QueryStringValue(request, "durability");
        return new SqlExecuteInput(RouteString(request, "project"), RouteString(request, "database"), null, durability)
        {
            PreParsed = true,
            ParsedStatements = read.Statements,
            ParseError = read.Error,
        };
    }

    public static async ValueTask<BoundGlobalSqlRequest> BindRequestAsync(
        HttpRequest request, Func<CancellationToken, ValueTask<BoundGlobalSqlRequest>> bind, CancellationToken cancellationToken)
    {
        if (request.ContentLength > MaxRequestBytes)
        {
            return OversizedRequestInput;
        }

        var hasBody = HasBody(request);
        if (!hasBody)
        {
            return EmptyRequestInput();
        }

        var isUtf8 = IsUtf8Json(request, out var isJson);
        if (!isJson)
        {
            if (string.IsNullOrEmpty(request.ContentType) && request.ContentLength is null)
            {
                var probe = await ProbeEmptyBodyAsync(request.BodyReader, cancellationToken);
                return probe switch
                {
                    BodyProbe.Empty => EmptyRequestInput(),
                    BodyProbe.HasData => new UnsupportedMediaType("Expected a JSON request body."),
                    BodyProbe.Oversized => OversizedRequestInput,
                    _ => throw new UnreachableException(),
                };
            }

            return new UnsupportedMediaType("Expected a JSON request body.");
        }

        if (!isUtf8)
        {
            try
            {
                return await bind(cancellationToken);
            }
            catch (BadHttpRequestException error) when (error.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                return OversizedRequestInput;
            }
        }

        var read = await ReadAndParseAsync(request, cancellationToken);
        if (read.Oversized)
        {
            return OversizedRequestInput;
        }

        if (read.InvalidSyntax)
        {
            return new Validation("The JSON request body is invalid.");
        }

        return new SqlRequestInput(null)
        {
            PreParsed = true,
            ParsedStatements = read.Statements,
            ParseError = read.Error,
        };
    }

    public ValueTask<SqlRunResolution> ExecuteAsync(
        HttpContext context, SqlExecuteInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.PreParsed
            ? ExecuteParsedAsync(context, request.Project, request.Database, request.ParsedStatements, request.ParseError, cancellationToken)
            : ExecuteAsync(context, request.Project, request.Database, request.Statements, cancellationToken);
    }

    public ValueTask<SqlRunResolution> ExecuteAsync(
        HttpContext context, string? project, string? database, SqlRequestInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.PreParsed
            ? ExecuteParsedAsync(context, project, database, request.ParsedStatements, request.ParseError, cancellationToken)
            : ExecuteAsync(context, project, database, request.Statements, cancellationToken);
    }

    public ValueTask<SqlRunResolution> ExecuteByIdAsync(
        HttpContext context, Ulid databaseId, SqlRequestInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.PreParsed
            ? ExecuteByIdParsedAsync(context, databaseId, request.ParsedStatements, request.ParseError, cancellationToken)
            : ExecuteByIdAsync(context, databaseId, request.Statements, cancellationToken);
    }

    public async ValueTask<SqlRunResolution> ExecuteAsync(
        HttpContext context, string? project, string? database, IReadOnlyList<SqlHttpStatement>? statements, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var outcome = await access.DataAsync(context.Caller, project, database, cancellationToken);
        return outcome switch
        {
            ResolvedDatabase target => await RunAsync(context, target, statements, cancellationToken),
            InvalidInput invalid => invalid,
            CredentialRejected unauthorized => unauthorized,
            CallerForbidden forbidden => forbidden,
            ResourceNotFound missing => missing,
            BackendUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<SqlRunResolution> ExecuteByIdAsync(
        HttpContext context, Ulid databaseId, IReadOnlyList<SqlHttpStatement>? statements, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var outcome = await access.DataByIdAsync(context.Caller, databaseId, cancellationToken);
        return outcome switch
        {
            ResolvedDatabase target => await RunAsync(context, target, statements, cancellationToken),
            InvalidInput invalid => invalid,
            CredentialRejected unauthorized => unauthorized,
            CallerForbidden forbidden => forbidden,
            ResourceNotFound missing => missing,
            BackendUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<SqlRunResolution> RunAsync(
        HttpContext context, ResolvedDatabase target, IReadOnlyList<SqlHttpStatement>? statements, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(target);
        RequestLogRecorder.Mark(context, target.Database.Id);
        var causalityOutcome = SqlRunner.Causality(context.Request);
        var outcome = causalityOutcome switch
        {
            SqlCausality causality => SqlWire.Statements(statements) switch
            {
                IReadOnlyList<SqlStatement> converted => await runner.RunAsync(target, converted, causality, context.Features.Get<AdmittedCaller>() is not null, cancellationToken),
                SqlRefusal refusal => refusal,
            },
            SqlRefusal refusal => refusal,
        };
        return outcome switch
        {
            SqlRun run => Written(context, run),
            SqlRefusal refusal => refusal,
        };
    }

    private async ValueTask<SqlRunResolution> ExecuteParsedAsync(
        HttpContext context, string? project, string? database, IReadOnlyList<SqlStatement>? statements, SqlRefusal? parseError, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var outcome = await access.DataAsync(context.Caller, project, database, cancellationToken);
        return outcome switch
        {
            ResolvedDatabase target => await RunParsedAsync(context, target, statements, parseError, cancellationToken),
            InvalidInput invalid => invalid,
            CredentialRejected unauthorized => unauthorized,
            CallerForbidden forbidden => forbidden,
            ResourceNotFound missing => missing,
            BackendUnavailable unavailable => unavailable,
        };
    }

    private async ValueTask<SqlRunResolution> ExecuteByIdParsedAsync(
        HttpContext context, Ulid databaseId, IReadOnlyList<SqlStatement>? statements, SqlRefusal? parseError, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var outcome = await access.DataByIdAsync(context.Caller, databaseId, cancellationToken);
        return outcome switch
        {
            ResolvedDatabase target => await RunParsedAsync(context, target, statements, parseError, cancellationToken),
            InvalidInput invalid => invalid,
            CredentialRejected unauthorized => unauthorized,
            CallerForbidden forbidden => forbidden,
            ResourceNotFound missing => missing,
            BackendUnavailable unavailable => unavailable,
        };
    }

    private async ValueTask<SqlRunResolution> RunParsedAsync(
        HttpContext context, ResolvedDatabase target, IReadOnlyList<SqlStatement>? statements, SqlRefusal? parseError, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(target);
        RequestLogRecorder.Mark(context, target.Database.Id);
        var causalityOutcome = SqlRunner.Causality(context.Request);
        var outcome = causalityOutcome switch
        {
            SqlCausality causality => parseError is not null
                ? (SqlRunOutcome)parseError
                : statements is not { Count: > 0 }
                    ? SqlRefusal.Invalid("statements holds 1 to 256 statements", 0)
                    : await runner.RunAsync(target, statements, causality, context.Features.Get<AdmittedCaller>() is not null, cancellationToken),
            SqlRefusal refusal => refusal,
        };
        return outcome switch
        {
            SqlRun run => Written(context, run),
            SqlRefusal refusal => refusal,
        };
    }

    private static SqlRunResolution Written(HttpContext context, SqlRun run)
    {
        SqlRunner.WriteHeaders(context.Response, run);
        return run;
    }

    private static SqlExecuteInput EmptyExecuteInput(HttpRequest request) =>
        new(RouteString(request, "project"), RouteString(request, "database"), null, QueryStringValue(request, "durability"))
        {
            PreParsed = true,
            ParseError = SqlRefusal.Invalid("statements holds 1 to 256 statements", 0),
        };

    private static SqlRequestInput EmptyRequestInput() =>
        new(null)
        {
            PreParsed = true,
            ParseError = SqlRefusal.Invalid("statements holds 1 to 256 statements", 0),
        };

    private static bool HasBody(HttpRequest request) =>
        request.ContentLength > 0
        || request.HttpContext.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true;

    private static bool IsUtf8Json(HttpRequest request, out bool isJson)
    {
        var contentType = request.ContentType;
        if (contentType is "application/json" or "application/json; charset=utf-8")
        {
            isJson = true;
            return true;
        }

        isJson = request.HasJsonContentType();
        if (!isJson)
        {
            return false;
        }

        if (!MediaTypeHeaderValue.TryParse(contentType, out var mediaType) || !mediaType.Charset.HasValue)
        {
            return true;
        }

        return mediaType.Encoding is { CodePage: 65001 };
    }

    private static string? RouteString(HttpRequest request, string key) =>
        request.RouteValues.TryGetValue(key, out var value)
            ? value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture)
            : null;

    private static string? QueryStringValue(HttpRequest request, string key) =>
        request.QueryString.HasValue && request.Query.TryGetValue(key, out var values) && values.Count > 0
            ? values[^1]
            : null;

    private static async ValueTask<BodyProbe> ProbeEmptyBodyAsync(PipeReader reader, CancellationToken cancellationToken)
    {
        while (true)
        {
            ReadResult result;
            try
            {
                result = await reader.ReadAsync(cancellationToken);
            }
            catch (BadHttpRequestException error) when (error.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                return BodyProbe.Oversized;
            }

            var buffer = result.Buffer;
            var hasData = !buffer.IsEmpty;
            reader.AdvanceTo(buffer.End);
            if (hasData)
            {
                return BodyProbe.HasData;
            }

            if (result.IsCompleted || result.IsCanceled)
            {
                return BodyProbe.Empty;
            }
        }
    }

    private static async ValueTask<BodyReadResult> ReadAndParseAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        var reader = request.BodyReader;
        ReadResult first;
        try
        {
            first = await reader.ReadAsync(cancellationToken);
        }
        catch (BadHttpRequestException error) when (error.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return new BodyReadResult(Oversized: true, InvalidSyntax: false, null, null, null);
        }

        var firstBuffer = first.Buffer;
        if (firstBuffer.Length > MaxRequestBytes)
        {
            reader.AdvanceTo(firstBuffer.End);
            return new BodyReadResult(Oversized: true, InvalidSyntax: false, null, null, null);
        }

        if (first.IsCompleted)
        {
            try
            {
                if (firstBuffer.IsEmpty)
                {
                    return new BodyReadResult(Oversized: false, InvalidSyntax: true, null, null, null);
                }

                if (firstBuffer.IsSingleSegment)
                {
                    return ParseSpan(firstBuffer.FirstSpan);
                }

                var length = (int)firstBuffer.Length;
                var singleRented = ArrayPool<byte>.Shared.Rent(length);
                try
                {
                    firstBuffer.CopyTo(singleRented);
                    return ParseSpan(singleRented.AsSpan(0, length));
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(singleRented);
                }
            }
            finally
            {
                reader.AdvanceTo(firstBuffer.End);
            }
        }

        if (first.IsCanceled)
        {
            reader.AdvanceTo(firstBuffer.End);
            return new BodyReadResult(Oversized: false, InvalidSyntax: true, null, null, null);
        }

        var initialCapacity = request.ContentLength is > 0 and <= MaxRequestBytes
            ? (int)request.ContentLength
            : Math.Clamp((int)firstBuffer.Length * 2, 4096, MaxRequestBytes);
        var rented = ArrayPool<byte>.Shared.Rent(initialCapacity);
        var total = (int)firstBuffer.Length;
        try
        {
            firstBuffer.CopyTo(rented);
            reader.AdvanceTo(firstBuffer.End);

            while (true)
            {
                ReadResult next;
                try
                {
                    next = await reader.ReadAsync(cancellationToken);
                }
                catch (BadHttpRequestException error) when (error.StatusCode == StatusCodes.Status413PayloadTooLarge)
                {
                    return new BodyReadResult(Oversized: true, InvalidSyntax: false, null, null, null);
                }

                var buffer = next.Buffer;
                if (total + buffer.Length > MaxRequestBytes)
                {
                    reader.AdvanceTo(buffer.End);
                    return new BodyReadResult(Oversized: true, InvalidSyntax: false, null, null, null);
                }

                var chunkLength = (int)buffer.Length;
                if (chunkLength > 0)
                {
                    if (total + chunkLength > rented.Length)
                    {
                        var nextSize = Math.Min(MaxRequestBytes, Math.Max(rented.Length * 2, total + chunkLength));
                        var grown = ArrayPool<byte>.Shared.Rent(nextSize);
                        rented.AsSpan(0, total).CopyTo(grown);
                        ArrayPool<byte>.Shared.Return(rented);
                        rented = grown;
                    }

                    buffer.CopyTo(rented.AsSpan(total));
                    total += chunkLength;
                }

                reader.AdvanceTo(buffer.End);

                if (next.IsCompleted)
                {
                    return total == 0
                        ? new BodyReadResult(Oversized: false, InvalidSyntax: true, null, null, null)
                        : ParseSpan(rented.AsSpan(0, total));
                }

                if (next.IsCanceled)
                {
                    return new BodyReadResult(Oversized: false, InvalidSyntax: true, null, null, null);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static BodyReadResult ParseSpan(ReadOnlySpan<byte> payload) =>
        SqlWire.TryReadExecuteBody(payload, out var statements, out var error, out var durability)
            ? new BodyReadResult(Oversized: false, InvalidSyntax: false, statements, error, durability)
            : new BodyReadResult(Oversized: false, InvalidSyntax: true, null, null, null);

    private enum BodyProbe
    {
        Empty,
        HasData,
        Oversized,
    }

    private readonly record struct BodyReadResult(bool Oversized, bool InvalidSyntax, SqlStatement[]? Statements, SqlRefusal? Error, string? Durability);
}

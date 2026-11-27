using TerraBaseDbConnection = TerraScale.DotNet.TerraBase.TerraBaseDbConnection;
using System.Data.Common;
using Dapper;
using System.Data;
using System.Globalization;
using System.Text.Json;
using TerraScale.Dashboard.Errors;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.Features.Logs
{

    public sealed record LogDrain(string ProjectId, string OrganizationId, string Revision, string Url, string? HeaderName, DateTimeOffset CreatedAt,
        DateTimeOffset? LastSuccessAt, DateTimeOffset? LastErrorAt, int? LastErrorStatus, DateTimeOffset? LastGapAt);

    public interface ILogDrainSource
    {
        Task<DatabaseLogBatch> ReadAsync(string projectId, string databaseId, string after, CancellationToken cancellationToken);
    }

    public readonly union LogDrainSaved(Done, DashboardValidation, DashboardForbidden, DashboardNotFound);

    public readonly union LogDrainDeleted(Done, DashboardForbidden, DashboardNotFound);

    public sealed partial class LogDrains(DashboardDb storage, OrganizationStore organizations, ILogDrainSource source, ICustomerDelivery delivery, IDashboardIdentity identity,
        TimeProvider clock, ILogger<LogDrains> logger, BackgroundJobs jobs, BillingSchemaGate gate)
    {
        private const string DrainOfProject = DashboardSql.LogDrains.Select + " WHERE project_id = @ProjectId";
        private const string DrainOf = DashboardSql.LogDrains.Select + " WHERE project_id = @ProjectId AND organization_id = @OrganizationId";
        private const string DrainById = DashboardSql.LogDrains.Select + " WHERE id = @Id";
        private const string LiveDrain = DashboardSql.LogDrains.Select + """
             WHERE id = @Id AND revision = @Revision
              AND EXISTS (SELECT 1 FROM project_databases AS member WHERE member.id = @DatabaseId AND member.project_id = log_drains.project_id
                  AND member.organization_id = log_drains.organization_id)
            """;
        private const string DrainIds = "SELECT id FROM log_drains ORDER BY id";
        private const string CursorsOf = DashboardSql.LogDrainCursors.Select + " WHERE drain_id = @DrainId";
        private const string RemoveJobs = "DELETE FROM background_jobs WHERE id LIKE @Prefix || '%'";
        private const string RemoveDrain = """
            DELETE FROM log_drain_cursors WHERE drain_id IN (SELECT id FROM log_drains WHERE project_id = @ProjectId AND organization_id = @OrganizationId);
            DELETE FROM log_drains WHERE project_id = @ProjectId AND organization_id = @OrganizationId;
            DELETE FROM background_jobs WHERE id LIKE @Prefix || '%'
            """;
        private const string JobById = BackgroundJobs.Snapshot + " WHERE id = @Id";

        private const int DrainParallelism = 8;
        private static readonly string[] ReservedHeaders = ["host", "content-length", "content-type", "transfer-encoding", "connection", "te", "upgrade", "expect", "cookie", "idempotency-key", "x-terrascale-delivery"];

        public async Task<LogDrain?> ReadAsync(string projectId, CancellationToken cancellationToken)
        {
            await using var connection = await storage.OpenAsync(cancellationToken);
            return await connection.QuerySingleOrDefaultAsync<LogDrainRow>(DrainOfProject, new { ProjectId = projectId }) is { } row ? Drain(row) : null;
        }

        public async Task<LogDrainSaved> SaveAsync(AccountContext context, string? url, string? headerName, string? headerValue, CancellationToken cancellationToken)
        {
            if (Owner(context) is not { } owner)
            {
                return SaveRefused(context);
            }

            var (organization, project) = owner;
            string target;
            try
            {
                target = CustomerUrls.Validate(url).OriginalString;
            }
            catch (ArgumentException error) when (error.Message == CustomerUrls.Rule)
            {
                return new DashboardValidation(error.Message);
            }

            var name = headerName?.Trim() is { Length: > 0 } text ? text : null;
            var value = headerValue?.Trim() is { Length: > 0 } secret ? secret : null;
            if (name is not null && (name.Length > 64 || !HeaderToken().IsMatch(name) || ReservedHeaders.Contains(name.ToLowerInvariant())) ||
                value is not null && (name is null || value.Length > 4096 || value.Any(char.IsControl)))
            {
                return new DashboardValidation("Use a header name of letters, digits and dashes and a value of up to 4096 characters without line breaks.");
            }

            var key = (await identity.ReadAsync(cancellationToken)).SessionEncryptionKey;
            var header = value is null ? null : SealedSecret.Seal(value, key, "log-drain/" + project.Id);
            await using var connection = await storage.OpenAsync(cancellationToken);
            for (var attempt = 0; ; attempt++)
            {
                var row = await connection.QuerySingleOrDefaultAsync<LogDrainRow>(DrainOf, new { ProjectId = project.Id, OrganizationId = organization.Id });
                var create = row is null;
                var previousRevision = row?.Revision;
                var keep = header is null && name is not null && row?.HeaderName == name;
                if (name is not null && header is null && !keep)
                {
                    return new DashboardValidation("Enter the header value.");
                }

                if (row is null)
                {
                    row = new LogDrainRow
                    {
                        Id = AccountService.NewId(),
                        OrganizationId = organization.Id,
                        ProjectId = project.Id,
                        Revision = AccountService.NewId(),
                        Url = target,
                        CreatedAt = clock.GetUtcNow().UtcDateTime,
                    };
                }

                row.OriginalRevision = row.Revision;
                row.Revision = AccountService.NewId();
                row.Url = target;
                row.HeaderName = name;
                if (!keep)
                {
                    row.HeaderNonce = header?.Nonce;
                    row.HeaderCiphertext = header?.Ciphertext;
                    row.HeaderTag = header?.Tag;
                }

                row.LastSuccessAt = null;
                row.LastErrorAt = null;
                row.LastErrorStatus = null;
                row.LastGapAt = null;
                var saved = row;
                try
                {
                    await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                    if (previousRevision is not null)
                    {
                        await connection.ExecuteAsync(RemoveJobs, new { Prefix = "log-drain/" + project.Id + "/" + previousRevision + "/" }, transaction);
                    }

                    DashboardDb.Expect(create
                        ? await connection.ExecuteAsync(DashboardSql.LogDrains.Insert, saved, transaction)
                        : await connection.ExecuteAsync(DashboardSql.LogDrains.Update, saved, transaction));
                    await transaction.CommitAsync(cancellationToken);
                    return new Done();
                }
                catch (Exception error) when (attempt < 2 && (DashboardDb.IsConflict(error) || DashboardDb.IsDuplicate(error)))
                {
                    await DashboardDb.ConflictBackoffAsync(attempt, clock, cancellationToken);
                }
            }
        }

        public async Task<LogDrainDeleted> DeleteAsync(AccountContext context, CancellationToken cancellationToken)
        {
            if (Owner(context) is not { } owner)
            {
                return DeleteRefused(context);
            }

            var (organization, project) = owner;
            await using var connection = await storage.OpenAsync(cancellationToken);
            await connection.ExecuteAsync(RemoveDrain, new { ProjectId = project.Id, OrganizationId = organization.Id, Prefix = "log-drain/" + project.Id + "/" });
            return new Done();
        }

        public async Task RunOnceAsync(CancellationToken cancellationToken)
        {
            if (!await gate.AllowsAsync(cancellationToken)) { return; }
            List<string> drains;
            await using (var connection = await storage.OpenAsync(cancellationToken))
            {
                drains = (await connection.QueryAsync<string>(DrainIds)).AsList();
            }

            await Parallel.ForEachAsync(drains, new ParallelOptions { MaxDegreeOfParallelism = DrainParallelism, CancellationToken = cancellationToken }, async (id, token) =>
            {
                try { await ShipAsync(id, token); }
                catch (Exception error) when (error is HttpRequestException or IOException or JsonException or InvalidOperationException or DBConcurrencyException or ArgumentException or FormatException ||
                    error is OperationCanceledException && !token.IsCancellationRequested)
                {
                    logger.AProjectLogDrainCouldNot(error.GetType().Name);
                }
            });
        }

        private async Task ShipAsync(string id, CancellationToken cancellationToken)
        {
            await using var db = await storage.OpenAsync(cancellationToken);
            if (await WithCursorsAsync(db, await db.QuerySingleOrDefaultAsync<LogDrainRow>(DrainById, new { Id = id }), null) is not { } drain)
            {
                return;
            }

            if (await organizations.FindAsync(drain.OrganizationId, cancellationToken) is not { } organizationRow ||
                !(organizationRow.Erased ? throw new IOException("Dashboard record was erased.") : organizationRow.Organization).Projects.TryGetValue(drain.ProjectId, out var project))
            {
                return;
            }

            var headers = new List<KeyValuePair<string, string>>();
            if (drain is { HeaderName: { } name, HeaderNonce: { } nonce, HeaderCiphertext: { } ciphertext, HeaderTag: { } tag })
            {
                headers.Add(new(name, new SealedSecret(nonce, ciphertext, tag).Open((await identity.ReadAsync(cancellationToken)).SessionEncryptionKey, "log-drain/" + drain.ProjectId)));
            }

            var stalled = false;
            foreach (var database in project.Databases.Values)
            {
                if (stalled)
                {
                    return;
                }

                var jobId = "log-drain/" + drain.ProjectId + "/" + drain.Revision + "/" + database.Id;
                var snapshot = await db.QuerySingleOrDefaultAsync<BackgroundJobRow>(JobById, new { Id = jobId });
                if (snapshot is { Leased: true })
                {
                    continue;
                }

                var preparedCursor = drain.Cursors.FirstOrDefault(row => row.DatabaseId == database.Id)?.Cursor ?? Start(Utc(drain.CreatedAt));
                var batch = snapshot?.Payload is null ? await source.ReadAsync(drain.ProjectId, database.Id, preparedCursor, cancellationToken) : null;
                if (batch is { Items.Count: 0 } && (!batch.Gap || snapshot?.Cursor == "gap/" + preparedCursor))
                {
                    continue;
                }

                await jobs.RunAsync(jobId, TimeSpan.Zero, async (claim, token) =>
                {
                    var state = claim.State;
                    var connection = claim.Connection;
                    LogDrainRow? current;
                    await using (var prepared = await connection.BeginTransactionAsync(token))
                    {
                        current = await WithCursorsAsync(connection,
                            await connection.QuerySingleOrDefaultAsync<LogDrainRow>(LiveDrain, new { Id = id, drain.Revision, DatabaseId = database.Id }, prepared), prepared);
                        if (current is null)
                        {
                            await claim.CommitForgetAsync(prepared, token);
                            return;
                        }

                        var cursor = current.Cursors.FirstOrDefault(row => row.DatabaseId == database.Id)?.Cursor ?? Start(Utc(current.CreatedAt));
                        if (state.Payload is null)
                        {
                            if (cursor != preparedCursor || batch is null)
                            {
                                return;
                            }

                            if (batch.Gap)
                            {
                                current.LastGapAt = clock.GetUtcNow().UtcDateTime;
                            }

                            if (batch.Items.Count == 0 || batch.NextCursor == cursor)
                            {
                                if (batch.Gap)
                                {
                                    state.Cursor = "gap/" + cursor;
                                    await SaveDrainAsync(connection, current, prepared);
                                    await claim.CommitSaveAsync(prepared, token);
                                }

                                return;
                            }

                            state.NextCursor = batch.NextCursor ?? throw new IOException("Log source returned no next cursor.");
                            state.Payload = Otlp(current.ProjectId, database, batch.Items);
                            state.DeliveryId = AccountService.NewId();
                            await SaveDrainAsync(connection, current, prepared);
                            await claim.CommitSaveAsync(prepared, token);
                        }
                        else
                        {
                            await prepared.CommitAsync(token);
                        }
                    }

                    var saved = current.Cursors.FirstOrDefault(row => row.DatabaseId == database.Id);

                    var payload = state.Payload ?? throw new IOException("The claimed log batch has no payload.");
                    var nextCursor = state.NextCursor ?? throw new IOException("The claimed log batch has no next cursor.");
                    var deliveryId = state.DeliveryId ?? throw new IOException("The claimed log batch has no delivery identity.");
                    var status = 0;
                    try
                    {
                        status = await delivery.PostAsync(new Uri(current.Url), payload,
                            [.. headers, new("Idempotency-Key", deliveryId), new("X-TerraScale-Delivery", deliveryId)], token);
                    }
                    catch (Exception error) when (error is HttpRequestException or IOException or ArgumentException or FormatException ||
                        error is OperationCanceledException && !token.IsCancellationRequested)
                    {
                        logger.AProjectLogDrainCouldNot(error.GetType().Name);
                    }
                    if (status is < 200 or > 299)
                    {
                        stalled = true;
                        current.LastErrorAt = clock.GetUtcNow().UtcDateTime;
                        current.LastErrorStatus = status;
                        await claim.SaveAsync((connection, transaction) => SaveDrainAsync(connection, current, transaction), token);
                        return;
                    }
                    var cursorRow = saved is null
                        ? new LogDrainCursorRow { Id = AccountService.NewId(), DrainId = id, DatabaseId = database.Id, Cursor = nextCursor }
                        : saved with { Cursor = nextCursor };
                    current.LastSuccessAt = clock.GetUtcNow().UtcDateTime;
                    state.Cursor = state.NextCursor;
                    state.Payload = null;
                    state.NextCursor = null;
                    state.DeliveryId = null;
                    await claim.SaveAsync(async (connection, transaction) =>
                    {
                        await connection.ExecuteAsync(saved is null ? DashboardSql.LogDrainCursors.Insert : DashboardSql.LogDrainCursors.Update, cursorRow, transaction);
                        await SaveDrainAsync(connection, current, transaction);
                    }, token);
                }, cancellationToken, fence: snapshot?.Checkpoint ?? "", databaseId: database.Id);
            }
        }

        private static async Task SaveDrainAsync(TerraBaseDbConnection connection, LogDrainRow drain, DbTransaction transaction)
        {
            drain.OriginalRevision = drain.Revision;
            DashboardDb.Expect(await connection.ExecuteAsync(DashboardSql.LogDrains.Update, drain, transaction));
        }

        private static async Task<LogDrainRow?> WithCursorsAsync(TerraBaseDbConnection connection, LogDrainRow? drain, DbTransaction? transaction)
        {
            if (drain is null)
            {
                return null;
            }

            drain.Cursors.AddRange(await connection.QueryAsync<LogDrainCursorRow>(CursorsOf, new { DrainId = drain.Id }, transaction));
            return drain;
        }

        public static byte[] Otlp(string projectId, DatabaseRecord database, IReadOnlyList<DatabaseLogEntry> entries)
        {
            return JsonSerializer.SerializeToUtf8Bytes(new
            {
                resourceLogs = new[]
            {
                new
                {
                    resource = new { attributes = new[] { Text("service.name", "terrascale"), Text("terrascale.project_id", projectId), Text("terrascale.database_id", database.Id), Text("terrascale.database", database.Name) } },
                    scopeLogs = new[]
                    {
                        new
                        {
                            scope = new { name = "terrascale" },
                            logRecords = entries.Select(entry => new
                            {
                                timeUnixNano = ((entry.Time.ToUnixTimeMilliseconds() * 1_000_000) + (entry.Time.Ticks % TimeSpan.TicksPerMillisecond * 100)).ToString(CultureInfo.InvariantCulture),
                                severityNumber = entry.Status >= 500 ? 17 : entry.Status >= 400 ? 13 : 9,
                                severityText = entry.Status >= 500 ? "ERROR" : entry.Status >= 400 ? "WARN" : "INFO",
                                body = new { stringValue = string.Create(CultureInfo.InvariantCulture, $"{entry.Method} {entry.Route} {entry.Status} {entry.LatencyMs:0.##}ms") },
                                attributes = new object[]
                                {
                                    Text("terrascale.log_id", entry.Id), Text("http.request.method", entry.Method), Text("http.route", entry.Route), Integer("http.response.status_code", entry.Status),
                                    new { key = "terrascale.latency_ms", value = new { doubleValue = entry.LatencyMs } }, Integer("terrascale.operations", entry.Operations),
                                    Text("terrascale.request_id", entry.RequestId ?? ""), Text("terrascale.region", entry.Region), new { key = "terrascale.is_query", value = new { boolValue = entry.IsQuery } }
                                }
                            }).ToArray()
                        }
                    }
                }
            }
            });
        }

        private static string Start(DateTimeOffset time)
        {
            Span<byte> bytes = stackalloc byte[16];
            bytes.Clear();
            var milliseconds = time.ToUnixTimeMilliseconds();
            for (var i = 0; i < 6; i++)
            {
                bytes[i] = (byte)(milliseconds >> (8 * (5 - i)));
            }

            return new Ulid(bytes).ToString();
        }

        private static object Text(string key, string value)
        {
            return new { key, value = new { stringValue = value } };
        }

        private static object Integer(string key, long value)
        {
            return new { key, value = new { intValue = value.ToString(CultureInfo.InvariantCulture) } };
        }

        private static (OrganizationRecord Organization, ProjectRecord Project)? Owner(AccountContext context)
        {
            return context.Organization is not { } organization || context.Project is not { } project || context.Role != "owner" ? null : (organization, project);
        }

        private static LogDrainSaved SaveRefused(AccountContext context)
        {
            return context.Organization is null || context.Project is null
                ? new LogDrainSaved(new DashboardNotFound("Select a project."))
                : new LogDrainSaved(new DashboardForbidden("Only organization owners manage the log drain."));
        }

        private static LogDrainDeleted DeleteRefused(AccountContext context)
        {
            return context.Organization is null || context.Project is null
                ? new LogDrainDeleted(new DashboardNotFound("Select a project."))
                : new LogDrainDeleted(new DashboardForbidden("Only organization owners manage the log drain."));
        }

        private static LogDrain Drain(LogDrainRow row)
        {
            return new(row.ProjectId, row.OrganizationId, row.Revision, row.Url, row.HeaderName, Utc(row.CreatedAt), Utc(row.LastSuccessAt), Utc(row.LastErrorAt),
                row.LastErrorStatus, Utc(row.LastGapAt));
        }

        private static DateTimeOffset Utc(DateTime value)
        {
            return new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
        }

        private static DateTimeOffset? Utc(DateTime? value)
        {
            return value is { } time ? Utc(time) : null;
        }

        [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z0-9!#$%&'*+.^_`|~-]+$")]
        private static partial System.Text.RegularExpressions.Regex HeaderToken();
    }
}

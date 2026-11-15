using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Features.Logs
{
    public sealed record LogDrain(string ProjectId, string OrganizationId, string Revision, string Url, string? HeaderName, DateTimeOffset CreatedAt,
        DateTimeOffset? LastSuccessAt, DateTimeOffset? LastErrorAt, int? LastErrorStatus, DateTimeOffset? LastGapAt);

    public interface ILogDrainSource
    {
        Task<DatabaseLogBatch> ReadAsync(string projectId, string databaseId, string after, CancellationToken cancellationToken);
    }

    public sealed partial class LogDrains(IDbContextFactory<DashboardDbContext> storage, OrganizationStore organizations, ILogDrainSource source, ICustomerDelivery delivery, IDashboardIdentity identity,
        TimeProvider clock, ILogger<LogDrains> logger, BackgroundJobs jobs, BillingSchemaGate gate)
    {
        private static readonly string[] ReservedHeaders = ["host", "content-length", "content-type", "transfer-encoding", "connection", "te", "upgrade", "expect", "cookie", "idempotency-key", "x-terrascale-delivery"];

        public async Task<LogDrain?> ReadAsync(string projectId, CancellationToken cancellationToken)
        {
            await using var db = await storage.CreateDbContextAsync(cancellationToken);
            return await db.LogDrains.AsNoTracking().SingleOrDefaultAsync(row => row.ProjectId == projectId, cancellationToken) is { } row ? Drain(row) : null;
        }

        public async Task SaveAsync(AccountContext context, string? url, string? headerName, string? headerValue, CancellationToken cancellationToken)
        {
            var (organization, project) = Owner(context);
            var target = CustomerUrls.Validate(url).OriginalString;
            var name = headerName?.Trim() is { Length: > 0 } text ? text : null;
            var value = headerValue?.Trim() is { Length: > 0 } secret ? secret : null;
            if (name is not null && (name.Length > 64 || !HeaderToken().IsMatch(name) || ReservedHeaders.Contains(name.ToLowerInvariant())) ||
                value is not null && (name is null || value.Length > 4096 || value.Any(char.IsControl)))
            {
                throw new ArgumentException("Use a header name of letters, digits and dashes and a value of up to 4096 characters without line breaks.");
            }

            var key = (await identity.ReadAsync(cancellationToken)).SessionEncryptionKey;
            var header = value is null ? null : SealedSecret.Seal(value, key, "log-drain/" + project.Id);
            await using var db = await storage.CreateDbContextAsync(cancellationToken);
            for (var attempt = 0; ; attempt++)
            {
                var row = await db.LogDrains.SingleOrDefaultAsync(drain => drain.ProjectId == project.Id && drain.OrganizationId == organization.Id, cancellationToken);
                var previousRevision = row?.Revision;
                var keep = header is null && name is not null && row?.HeaderName == name;
                if (name is not null && header is null && !keep)
                {
                    throw new ArgumentException("Enter the header value.");
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
                    db.LogDrains.Add(row);
                }

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
                if (previousRevision is not null)
                {
                    var prefix = "log-drain/" + project.Id + "/" + previousRevision + "/";
                    db.BackgroundJobs.RemoveRange(await db.BackgroundJobs.Where(job => job.Id.StartsWith(prefix)).ToListAsync(cancellationToken));
                }
                try
                {
                    await db.SaveChangesAsync(cancellationToken);
                    return;
                }
                catch (DbUpdateException error) when (attempt < 2 && (error is DbUpdateConcurrencyException || error.InnerException is TerraBaseSqlException { Code: TerraBaseSqlErrorCodes.UniqueViolation }))
                {
                    db.ChangeTracker.Clear();
                }
            }
        }

        public async Task DeleteAsync(AccountContext context, CancellationToken cancellationToken)
        {
            var (organization, project) = Owner(context);
            await using var db = await storage.CreateDbContextAsync(cancellationToken);
            await db.LogDrains.Where(row => row.ProjectId == project.Id && row.OrganizationId == organization.Id).ExecuteDeleteAsync(cancellationToken);
            var prefix = "log-drain/" + project.Id + "/";
            await db.BackgroundJobs.Where(row => row.Id.StartsWith(prefix)).ExecuteDeleteAsync(cancellationToken);
        }

        public async Task RunOnceAsync(CancellationToken cancellationToken)
        {
            if (!await gate.AllowsAsync(cancellationToken)) { return; }
            List<string> drains;
            await using (var db = await storage.CreateDbContextAsync(cancellationToken))
            {
                drains = await db.LogDrains.AsNoTracking().OrderBy(row => row.Id).Select(row => row.Id).ToListAsync(cancellationToken);
            }

            foreach (var id in drains)
            {
                try { await ShipAsync(id, cancellationToken); }
                catch (Exception error) when (error is HttpRequestException or IOException or JsonException or InvalidOperationException or DbUpdateException or ArgumentException or FormatException ||
                    error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    logger.AProjectLogDrainCouldNot(error.GetType().Name);
                }
            }
        }

        private async Task ShipAsync(string id, CancellationToken cancellationToken)
        {
            await using var db = await storage.CreateDbContextAsync(cancellationToken);
            if (await db.LogDrains.Include(row => row.Cursors).SingleOrDefaultAsync(row => row.Id == id, cancellationToken) is not { } drain)
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

            foreach (var database in project.Databases.Values)
            {
                var jobId = "log-drain/" + drain.ProjectId + "/" + drain.Revision + "/" + database.Id;
                var snapshot = await db.BackgroundJobs.AsNoTracking().Where(row => row.Id == jobId)
                    .Select(row => new { State = row, Now = DateTime.UtcNow }).SingleOrDefaultAsync(cancellationToken);
                if (snapshot is not null && snapshot.State.LeaseUntil > snapshot.Now) { continue; }
                var preparedCursor = drain.Cursors.FirstOrDefault(row => row.DatabaseId == database.Id)?.Cursor ?? Start(Utc(drain.CreatedAt));
                var batch = snapshot?.State.Payload is null ? await source.ReadAsync(drain.ProjectId, database.Id, preparedCursor, cancellationToken) : null;
                if (batch is { Items.Count: 0 } && (!batch.Gap || snapshot?.State.Cursor == "gap/" + preparedCursor)) { continue; }
                await jobs.RunAsync(jobId, TimeSpan.Zero, async (claim, token) =>
                {
                    var current = await claim.Database.LogDrains.Include(row => row.Cursors)
                        .SingleOrDefaultAsync(row => row.Id == id && row.Revision == drain.Revision, token);
                    if (current is null)
                    {
                        claim.Database.BackgroundJobs.Remove(claim.State);
                        await claim.Database.SaveChangesAsync(token);
                        return;
                    }
                    var saved = current.Cursors.FirstOrDefault(row => row.DatabaseId == database.Id);
                    var cursor = saved?.Cursor ?? Start(Utc(current.CreatedAt));
                    var state = claim.State;
                    if (state.Payload is null)
                    {
                        if (cursor != preparedCursor || batch is null) { return; }
                        if (batch.Gap) { current.LastGapAt = clock.GetUtcNow().UtcDateTime; }
                        if (batch.Items.Count == 0 || batch.NextCursor == cursor)
                        {
                            if (batch.Gap)
                            {
                                state.Cursor = "gap/" + cursor;
                                await claim.SaveAsync(token);
                            }
                            return;
                        }
                        state.NextCursor = batch.NextCursor ?? throw new IOException("Log source returned no next cursor.");
                        state.Payload = Otlp(current.ProjectId, database, batch.Items);
                        state.DeliveryId = AccountService.NewId();
                        await claim.SaveAsync(token);
                    }

                    var nextCursor = state.NextCursor ?? throw new IOException("The claimed log batch has no next cursor.");
                    var deliveryId = state.DeliveryId ?? throw new IOException("The claimed log batch has no delivery identity.");
                    var status = 0;
                    try
                    {
                        status = await delivery.PostAsync(new Uri(current.Url), state.Payload,
                            [.. headers, new("Idempotency-Key", deliveryId), new("X-TerraScale-Delivery", deliveryId)], token);
                    }
                    catch (Exception error) when (error is HttpRequestException or IOException or ArgumentException or FormatException ||
                        error is OperationCanceledException && !token.IsCancellationRequested)
                    {
                        logger.AProjectLogDrainCouldNot(error.GetType().Name);
                    }
                    if (status is < 200 or > 299)
                    {
                        current.LastErrorAt = clock.GetUtcNow().UtcDateTime;
                        current.LastErrorStatus = status;
                        await claim.SaveAsync(token);
                        return;
                    }
                    if (saved is null)
                    {
                        claim.Database.LogDrainCursors.Add(new LogDrainCursorRow
                        {
                            Id = AccountService.NewId(),
                            DrainId = id,
                            DatabaseId = database.Id,
                            Cursor = nextCursor
                        });
                    }
                    else { saved.Cursor = nextCursor; }
                    current.LastSuccessAt = clock.GetUtcNow().UtcDateTime;
                    state.Cursor = state.NextCursor;
                    state.Payload = null;
                    state.NextCursor = null;
                    state.DeliveryId = null;
                    await claim.SaveAsync(token);
                }, cancellationToken, fence: snapshot?.State.Checkpoint ?? "");
            }
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
            var bytes = new byte[16];
            var milliseconds = time.ToUnixTimeMilliseconds();
            for (var i = 0; i < 6; i++)
            {
                bytes[i] = (byte)(milliseconds >> (8 * (5 - i)));
            }

            return new NUlid.Ulid(bytes).ToString();
        }

        private static object Text(string key, string value)
        {
            return new { key, value = new { stringValue = value } };
        }

        private static object Integer(string key, long value)
        {
            return new { key, value = new { intValue = value.ToString(CultureInfo.InvariantCulture) } };
        }

        private static (OrganizationRecord Organization, ProjectRecord Project) Owner(AccountContext context)
        {
            if (context.Organization is not { } organization || context.Project is not { } project)
            {
                throw new AccountAccessException("Select a project.", 404);
            }

            if (context.Role != "owner")
            {
                throw new AccountAccessException("Only organization owners manage the log drain.");
            }

            return (organization, project);
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

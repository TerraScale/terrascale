using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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

    public sealed partial class LogDrains(IDbContextFactory<DashboardDbContext> storage, IDashboardState state, ILogDrainSource source, ICustomerDelivery delivery, IDashboardIdentity identity,
        TimeProvider clock, ILogger<LogDrains> logger)
    {
        private static readonly string[] ReservedHeaders = ["host", "content-length", "content-type", "transfer-encoding", "connection", "te", "upgrade", "expect", "cookie"];

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
        }

        public async Task RunOnceAsync(CancellationToken cancellationToken)
        {
            List<string> drains;
            await using (var db = await storage.CreateDbContextAsync(cancellationToken))
            {
                drains = await db.LogDrains.AsNoTracking().OrderBy(row => row.Id).Select(row => row.Id).ToListAsync(cancellationToken);
            }

            foreach (var id in drains)
            {
                try { await ShipAsync(id, cancellationToken); }
                catch (Exception error) when (error is HttpRequestException or IOException or JsonException or InvalidOperationException or DbUpdateException ||
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

            if (await state.GetAsync(AccountService.OrganizationsTable, drain.OrganizationId, cancellationToken) is not { } organizationRow ||
                !AccountService.Read<OrganizationRecord>(organizationRow).Projects.TryGetValue(drain.ProjectId, out var project))
            {
                return;
            }

            var headers = new List<KeyValuePair<string, string>>();
            if (drain is { HeaderName: { } name, HeaderNonce: { } nonce, HeaderCiphertext: { } ciphertext, HeaderTag: { } tag })
            {
                headers.Add(new(name, new SealedSecret(nonce, ciphertext, tag).Open((await identity.ReadAsync(cancellationToken)).SessionEncryptionKey, "log-drain/" + drain.ProjectId)));
            }

            var advanced = false;
            int? failure = null;
            var gap = false;
            foreach (var database in project.Databases.Values)
            {
                var saved = drain.Cursors.FirstOrDefault(row => row.DatabaseId == database.Id);
                var cursor = saved?.Cursor ?? Start(Utc(drain.CreatedAt));
                var batch = await source.ReadAsync(drain.ProjectId, database.Id, cursor, cancellationToken);
                gap |= batch.Gap;
                if (batch.Items.Count > 0)
                {
                    int status;
                    try { status = await delivery.PostAsync(new Uri(drain.Url), Otlp(drain.ProjectId, database, batch.Items), headers, cancellationToken); }
                    catch (Exception error) when (error is HttpRequestException or ArgumentException or IOException || error is OperationCanceledException && !cancellationToken.IsCancellationRequested) { status = 0; }
                    if (status is < 200 or > 299)
                    {
                        failure = status;
                        continue;
                    }
                }
                else if (batch.NextCursor == cursor)
                {
                    continue;
                }

                advanced = true;
                if (saved is null)
                {
                    db.LogDrainCursors.Add(new LogDrainCursorRow { Id = AccountService.NewId(), DrainId = drain.Id, DatabaseId = database.Id, Cursor = batch.NextCursor! });
                }
                else
                {
                    saved.Cursor = batch.NextCursor!;
                }
            }

            if (!advanced && failure is null && !gap)
            {
                return;
            }

            var now = clock.GetUtcNow().UtcDateTime;
            if (advanced)
            {
                drain.LastSuccessAt = now;
            }

            if (failure is { } code)
            {
                drain.LastErrorAt = now;
                drain.LastErrorStatus = code;
            }

            if (gap)
            {
                drain.LastGapAt = now;
            }

            try { await db.SaveChangesAsync(cancellationToken); }
            catch (DbUpdateConcurrencyException) { }
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
                                    Text("http.request.method", entry.Method), Text("http.route", entry.Route), Integer("http.response.status_code", entry.Status),
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

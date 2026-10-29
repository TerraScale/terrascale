using System.Globalization;
using System.Text.Json;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Features.Logs
{
    // One OTLP/HTTP log drain per project. Cursors hold TerraBase's request-log cursor per database; LastGapAt records when
    // TerraBase reported requests discarded before the drain read them.
    public sealed record LogDrain(string ProjectId, string OrganizationId, string Revision, string Url, string? HeaderName, SealedSecret? HeaderValue, DateTimeOffset CreatedAt)
    {
        public Dictionary<string, string> Cursors { get; init; } = [];
        public DateTimeOffset? LastSuccessAt { get; init; }
        public DateTimeOffset? LastErrorAt { get; init; }
        public int? LastErrorStatus { get; init; }
        public DateTimeOffset? LastGapAt { get; init; }
    }
    public sealed record LogDrainIndex(Dictionary<string, string> Projects);

    public interface ILogDrainSource
    {
        Task<DatabaseLogBatch> ReadAsync(string projectId, string databaseId, string after, CancellationToken cancellationToken);
    }

    public sealed partial class LogDrains(IDashboardState state, ILogDrainSource source, ICustomerDelivery delivery, IDashboardIdentity identity, TimeProvider clock, ILogger<LogDrains> logger)
    {
        public const string Collection = "dashboard_log_drains";
        public const string IndexCollection = "dashboard_log_drain_projects";
        private static readonly string[] ReservedHeaders = ["host", "content-length", "content-type", "transfer-encoding", "connection", "te", "upgrade", "expect", "cookie"];

        public async Task<LogDrain?> ReadAsync(string projectId, CancellationToken cancellationToken)
        {
            return await state.GetAsync(Collection, projectId, cancellationToken) is { } document ? Read(document, projectId) : null;
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
            var drain = new LogDrain(project.Id, organization.Id, AccountService.NewId(), target, name, null, clock.GetUtcNow());
            await state.UpdateAsync(Collection, project.Id, () => drain, document => Read(document, project.Id), current =>
            {
                // A blank value keeps the stored one while the header name stays the same; cursors survive edits, so nothing is re-sent.
                var header = value is not null ? SealedSecret.Seal(value, key, "log-drain/" + project.Id) : name is not null && current.HeaderName == name ? current.HeaderValue : null;
                if (name is not null && header is null)
                {
                    throw new ArgumentException("Enter the header value.");
                }

                return drain with { HeaderValue = header, Cursors = current.Cursors, CreatedAt = current.CreatedAt };
            }, cancellationToken);
            await state.UpdateAsync(IndexCollection, "all", () => new LogDrainIndex([]), AccountService.Read<LogDrainIndex>,
                index => index.Projects.TryAdd(project.Id, organization.Id) ? index : null, cancellationToken);
        }

        public async Task DeleteAsync(AccountContext context, CancellationToken cancellationToken)
        {
            var (_, project) = Owner(context);
            if (await state.GetAsync(Collection, project.Id, cancellationToken) is { } document)
            {
                await state.DeleteAsync(Collection, project.Id, document.Version, cancellationToken);
            }
        }

        public async Task RunOnceAsync(CancellationToken cancellationToken)
        {
            if (await state.GetAsync(IndexCollection, "all", cancellationToken) is not { } index)
            {
                return;
            }

            foreach (var (projectId, organizationId) in AccountService.Read<LogDrainIndex>(index).Projects)
            {
                try { await ShipAsync(projectId, organizationId, cancellationToken); }
                catch (Exception error) when (error is HttpRequestException or IOException or JsonException or InvalidOperationException ||
                    error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    logger.AProjectLogDrainCouldNot(error.GetType().Name);
                }
            }
        }

        private async Task ShipAsync(string projectId, string organizationId, CancellationToken cancellationToken)
        {
            if (await ReadAsync(projectId, cancellationToken) is not { } drain)
            {
                return;
            }

            if (await state.GetAsync(AccountService.OrganizationsCollection, organizationId, cancellationToken) is not { } organizationDocument ||
                !AccountService.Read<OrganizationRecord>(organizationDocument).Projects.TryGetValue(projectId, out var project))
            {
                return;
            }

            var headers = new List<KeyValuePair<string, string>>();
            if (drain.HeaderName is { } name && drain.HeaderValue is { } value)
            {
                headers.Add(new(name, value.Open((await identity.ReadAsync(cancellationToken)).SessionEncryptionKey, "log-drain/" + projectId)));
            }

            var cursors = new Dictionary<string, string>(StringComparer.Ordinal);
            int? failure = null;
            var gap = false;
            foreach (var database in project.Databases.Values)
            {
                // Without a cursor the drain starts from its creation.
                var cursor = drain.Cursors.GetValueOrDefault(database.Id) ?? Start(drain.CreatedAt);
                var batch = await source.ReadAsync(projectId, database.Id, cursor, cancellationToken);
                gap |= batch.Gap;
                if (batch.Items.Count == 0)
                {
                    if (batch.NextCursor != cursor)
                    {
                        cursors[database.Id] = batch.NextCursor!;
                    }

                    continue;
                }
                int status;
                try { status = await delivery.PostAsync(new Uri(drain.Url), Otlp(projectId, database, batch.Items), headers, cancellationToken); }
                catch (Exception error) when (error is HttpRequestException or ArgumentException or IOException || error is OperationCanceledException && !cancellationToken.IsCancellationRequested) { status = 0; }
                if (status is >= 200 and <= 299)
                {
                    cursors[database.Id] = batch.NextCursor!;
                }
                else
                {
                    failure = status;
                }
            }
            if (cursors.Count == 0 && failure is null && !gap)
            {
                return;
            }

            var now = clock.GetUtcNow();
            await state.UpdateAsync(Collection, projectId, () => throw new IOException("Log drain disappeared."), document => Read(document, projectId), current =>
            {
                if (current.Revision != drain.Revision)
                {
                    return null;
                }

                var merged = new Dictionary<string, string>(current.Cursors, StringComparer.Ordinal);
                foreach (var (database, id) in cursors)
                {
                    merged[database] = id;
                }

                return current with
                {
                    Cursors = merged,
                    LastSuccessAt = cursors.Count > 0 ? now : current.LastSuccessAt,
                    LastErrorAt = failure is null ? current.LastErrorAt : now,
                    LastErrorStatus = failure ?? current.LastErrorStatus,
                    LastGapAt = gap ? now : current.LastGapAt
                };
            }, cancellationToken);
        }

        // OTLP/HTTP JSON (opentelemetry-proto logs v1): 64-bit integers are strings.
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

        // The smallest ULID at the drain's creation time: every request recorded since then sorts after it.
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

        public static LogDrain Read(TerraBaseDocument document, string projectId)
        {
            var saved = AccountService.Read<LogDrain>(document);
            if (document.Id != projectId || saved.ProjectId != projectId || saved.Cursors is null || !Uri.TryCreate(saved.Url, UriKind.Absolute, out _))
            {
                throw new IOException("Log drain identity is invalid.");
            }

            return saved;
        }

        [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z0-9!#$%&'*+.^_`|~-]+$")]
        private static partial System.Text.RegularExpressions.Regex HeaderToken();
    }
}

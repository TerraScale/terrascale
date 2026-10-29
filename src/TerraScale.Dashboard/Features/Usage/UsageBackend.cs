using TerraScale.Dashboard.Platform.Observability;
using System.Net;
using System.Text.Json;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Features.Usage;

// TerraBase owns counters; dashboard billing remains separate from these best-effort observations.
public sealed class UsageBackend(IHttpClientFactory factory, TimeProvider time, IProjectAccessTokens tokens) : IUsageMeasurements
{
    public async Task<UsageMeasurements> ReadAsync(UsageSelection selection, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var client = factory.CreateClient("platform");
        Uri origin;
        try { origin = await new TerraBaseDiscovery().ResolveAsync(deadline.Token); }
        catch (Exception error) when (Failure(error, cancellationToken))
        { ServiceSloTelemetry.MarkFailure(error); return new(new(UsageSourceState.Unavailable), selection.Context.Organization!.Projects.Keys.ToDictionary(id => id, _ => new UsageObservation(UsageSourceState.Unavailable))); }
        var now = time.GetUtcNow();
        var utc = now.UtcDateTime;
        var month = new DateTimeOffset(utc.Year, utc.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var (granularity, from) = UsageSelection.Buckets(selection.Range, now);
        var context = selection.Context;
        using var concurrency = new SemaphoreSlim(4);
        var monthly = context.Organization!.Projects.Values.Select(async project =>
        {
            var acquired = false;
            try
            {
                await concurrency.WaitAsync(deadline.Token); acquired = true;
                return (project.Id, Value: await ReadAsync(project, "", "day", month));
            }
            catch (Exception error) when (Failure(error, cancellationToken)) { ServiceSloTelemetry.MarkFailure(error); return (project.Id, Value: new UsageObservation(UsageSourceState.Unavailable)); }
            finally { if (acquired) concurrency.Release(); }
        }).ToArray();
        var scoped = context.Project is null ? Task.FromResult(new UsageObservation(UsageSourceState.NotMetered)) : ReadAsync(context.Project, selection.DatabaseId, granularity, from);
        var allMonthly = Task.WhenAll(monthly);
        await Task.WhenAll(scoped, allMonthly);
        return new(await scoped, (await allMonthly).ToDictionary(item => item.Id, item => item.Value, StringComparer.Ordinal)) { MonthStart = month };

        async Task<UsageObservation> ReadAsync(ProjectRecord project, string databaseId, string bucket, DateTimeOffset first)
        {
            var authorized = false;
            try
            {
                if (project.OrganizationId != context.Organization.Id) throw new JsonException("Usage project scope is invalid.");
                var token = await tokens.GetAsync(context.Session, context.Organization.Id, project.Id, ["databases:read"], deadline.Token);
                authorized = true;
                var tap = new MonthTap(client);
                using var tapped = new HttpClient(tap);
                var sdk = new TerraBaseDocumentsClient(tapped, new TerraBaseDocumentsClientOptions
                {
                    BaseAddress = origin,
                    AccessTokenProvider = _ => ValueTask.FromResult(token)
                });
                if (databaseId.Length > 0)
                {
                    if (!project.Databases.TryGetValue(databaseId, out var configured) || configured.ProjectId != project.Id)
                        throw new JsonException("Usage database scope is invalid.");
                    var row = await sdk.Database(project.Id, configured.Name).GetUsageAsync(bucket, first, now, deadline.Token);
                    if (row.DatabaseId != databaseId) throw new JsonException("Usage returned a different database.");
                    return new(UsageSourceState.Measured, DatabaseReading(row, project, first, now, bucket));
                }
                var totals = await sdk.Project(project.Id).GetUsageAsync(bucket, first, now, deadline.Token);
                if (totals.ProjectId != project.Id || Count(totals.Databases) != totals.Items.Count ||
                    totals.Items.Select(item => item.DatabaseId).Distinct(StringComparer.Ordinal).Count() != totals.Items.Count)
                    throw new JsonException("Usage returned a different project or repeated database.");
                if (Count(totals.StorageBytes) != checked(totals.Items.Sum(item => Count(item.StorageBytes)) + Count(totals.FileBytes)) ||
                    Count(totals.LogicalBytes) != checked(totals.Items.Sum(item => Count(item.LogicalBytes)) + Count(totals.FileBytes)) ||
                    Count(totals.Documents) != totals.Items.Sum(item => Count(item.Documents)) ||
                    Count(totals.Reads) != totals.Items.Sum(item => Count(item.Reads)) ||
                    Count(totals.Writes) != totals.Items.Sum(item => Count(item.Writes)) ||
                    totals.Period.Reads != totals.Items.Sum(item => Count(item.Period.Reads)) ||
                    totals.Period.Writes != totals.Items.Sum(item => Count(item.Period.Writes)))
                    throw new JsonException("Project usage totals disagree with database usage.");
                return new(UsageSourceState.Measured, new(project.Id, "", Count(totals.LogicalBytes), Count(totals.Documents),
                    Period(totals.Period, first, now, bucket), Points(totals.Series, totals.Period, bucket),
                    [.. totals.Items.Select(item => DatabaseReading(item, project, first, now, bucket, true))], tap.Month));
            }
            catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            { return new(UsageSourceState.Unauthorized); }
            // Only a producer 404 after authorization can describe an unregistered empty project.
            // It provides no authoritative monthly organization ledger, including deleted-project usage.
            catch (HttpRequestException error) when (authorized && error.StatusCode == HttpStatusCode.NotFound && databaseId.Length == 0 && project.Databases.Count == 0)
            { return new(UsageSourceState.Measured, new(project.Id, "", 0, 0, new(first, End(now, bucket), bucket, 0, 0), [], [])); }
            catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound)
            { return new(UsageSourceState.Restricted); }
            catch (TerraBaseException error) when (error.StatusCode == HttpStatusCode.BadRequest && error.ErrorCode == "invalid_query")
            { return new(UsageSourceState.QueryTooExpensive); }
            catch (Exception error) when (Failure(error, cancellationToken)) { ServiceSloTelemetry.MarkFailure(error); return new(UsageSourceState.Unavailable); }
        }
    }
    private static UsageReading DatabaseReading(TerraBaseDatabaseUsage row, ProjectRecord project, DateTimeOffset from, DateTimeOffset now, string bucket, bool child = false)
    {
        _ = Count(row.StorageBytes); _ = Count(row.Collections); _ = Count(row.Reads); _ = Count(row.Writes);
        if (!project.Databases.TryGetValue(row.DatabaseId, out var database) || database.ProjectId != project.Id)
            throw new JsonException("Usage returned a foreign database.");
        return new(project.Id, row.DatabaseId, Count(row.LogicalBytes), Count(row.Documents), Period(row.Period, from, now, bucket),
            Points(row.Series, row.Period, bucket, child), []);
    }

    private static UsagePeriod Period(TerraBaseUsagePeriod period, DateTimeOffset from, DateTimeOffset now, string bucket)
    {
        var roundedEnd = End(now, bucket);
        if (period.From != from || period.To != roundedEnd || period.To <= period.From || period.Granularity != bucket)
            throw new JsonException("Usage returned an invalid UTC period.");
        return new(period.From, period.To, bucket, Count(period.Reads), Count(period.Writes));
    }

    private static DateTimeOffset End(DateTimeOffset now, string bucket)
    {
        var ticks = bucket == "day" ? TimeSpan.TicksPerDay : TimeSpan.TicksPerHour;
        return new DateTimeOffset(checked((now.UtcTicks + ticks - 1) / ticks * ticks), TimeSpan.Zero);
    }

    private static IReadOnlyList<ActivityPoint> Points(IReadOnlyList<TerraBaseUsagePoint> series, TerraBaseUsagePeriod period, string bucket, bool child = false)
    {
        if (child && series.Count == 0) return [];
        var ticks = bucket == "day" ? TimeSpan.TicksPerDay : TimeSpan.TicksPerHour;
        if (series.Any(point => point.Start < period.From || point.Start >= period.To || point.Start.Offset != TimeSpan.Zero ||
                point.Start.TimeOfDay.Ticks % ticks != 0) || series.Select(point => point.Start).Distinct().Count() != series.Count)
            throw new JsonException("Usage returned invalid or repeated UTC buckets.");
        var points = series.Select(point => new ActivityPoint(point.Start, Count(point.Reads), Count(point.Writes))).OrderBy(point => point.Start).ToArray();
        if (points.Length != (period.To - period.From).Ticks / ticks ||
            points.Sum(point => point.Reads) != period.Reads || points.Sum(point => point.Writes) != period.Writes)
            throw new JsonException("Usage totals disagree with its UTC buckets.");
        return points;
    }

    // The pinned SDK drops TerraBase's `month` operation units, so read them from the same usage response.
    private sealed class MonthTap(HttpClient inner) : HttpMessageHandler
    {
        public UsageMonth? Month { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null) throw new InvalidOperationException("Usage reads carry no body.");
            var copy = new HttpRequestMessage(request.Method, request.RequestUri) { Version = request.Version, VersionPolicy = request.VersionPolicy };
            foreach (var header in request.Headers) copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
            var response = await inner.SendAsync(copy, cancellationToken);
            if (!response.IsSuccessStatusCode) return response;
            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            using (var json = JsonDocument.Parse(body))
                Month = json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("month", out var month) && month.ValueKind == JsonValueKind.Object
                    ? new(month.GetProperty("month").GetString() is { Length: 7 } name ? name : throw new JsonException("Usage month is invalid."),
                        Count(month.GetProperty("readUnits").GetInt64()), Count(month.GetProperty("writeUnits").GetInt64()), Count(month.GetProperty("units").GetInt64()),
                        month.TryGetProperty("organizationUnits", out var units) ? Count(units.GetInt64()) : null)
                    : null;
            var content = new ByteArrayContent(body);
            foreach (var header in response.Content.Headers) content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            response.Content.Dispose();
            response.Content = content;
            return response;
        }
    }

    private static long Count(long count) => count >= 0 ? count : throw new JsonException("Usage counters must be nonnegative.");
    private static bool Failure(Exception error, CancellationToken cancellationToken) => error is HttpRequestException or IOException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException or TimeoutException || error is OperationCanceledException && !cancellationToken.IsCancellationRequested;
}

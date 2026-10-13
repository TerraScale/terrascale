using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.Features.Usage;

// The pinned official SDK has no usage client. This is the existing ts-go
// producer contract, preserving native provider IDs and the authenticated JWT.
public sealed class UsageBackend(IHttpClientFactory factory, TimeProvider time) : IUsageMeasurements
{
    public async Task<UsageMeasurements> ReadAsync(UsageSelection selection, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var client = factory.CreateClient("platform");
        Uri origin;
        try { origin = await new TerraBaseDiscovery().ResolveAsync(deadline.Token); }
        catch (Exception error) when (Failure(error, cancellationToken))
        { return new(new(UsageSourceState.Unavailable), selection.Context.Organization!.Projects.Keys.ToDictionary(id => id, _ => new UsageObservation(UsageSourceState.Unavailable))); }
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
            catch (Exception error) when (Failure(error, cancellationToken)) { return (project.Id, Value: new UsageObservation(UsageSourceState.Unavailable)); }
            finally { if (acquired) concurrency.Release(); }
        }).ToArray();
        var scoped = context.Project is null ? Task.FromResult(new UsageObservation(UsageSourceState.NotMetered)) : ReadAsync(context.Project, selection.DatabaseId, granularity, from);
        var allMonthly = Task.WhenAll(monthly);
        await Task.WhenAll(scoped, allMonthly);
        return new(await scoped, (await allMonthly).ToDictionary(item => item.Id, item => item.Value, StringComparer.Ordinal)) { MonthStart = month };

        async Task<UsageObservation> ReadAsync(ProjectRecord project, string databaseId, string bucket, DateTimeOffset first)
        {
            try
            {
                if (project.OrganizationId != context.Organization.Id) throw new JsonException("Usage project scope is invalid.");
                var path = $"/v1/management/organizations/{Uri.EscapeDataString(context.Organization.Id)}/projects/{Uri.EscapeDataString(project.Id)}" +
                    (databaseId.Length > 0 ? "/databases/" + Uri.EscapeDataString(databaseId) : "") +
                    "/usage?granularity=" + bucket + "&from=" + Uri.EscapeDataString(first.UtcDateTime.ToString("O"));
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(origin, path));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.Session.AccessToken);
                request.Headers.Add("X-TerraBase-User-Id", context.UserId);
                request.Headers.Add("X-TerraBase-Organization-Id", context.Organization.Id);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                if (response.StatusCode == HttpStatusCode.NotFound) return new(UsageSourceState.NotMetered);
                if (response.StatusCode == HttpStatusCode.Unauthorized) return new(UsageSourceState.Unauthorized);
                response.EnsureSuccessStatusCode();
                using var json = await PlatformJson.ReadAsync(response.Content, deadline.Token);
                return new(UsageSourceState.Measured, Parse(json.RootElement, project, databaseId, first, time.GetUtcNow(), bucket, true));
            }
            catch (Exception error) when (Failure(error, cancellationToken)) { return new(UsageSourceState.Unavailable); }
        }
    }
    private static UsageReading Parse(JsonElement row, ProjectRecord project, string databaseId, DateTimeOffset from, DateTimeOffset now, string granularity, bool includeItems)
    {
        if (row.GetProperty("projectId").GetString() != project.Id || row.TryGetProperty("databaseId", out var database) && (database.GetString() ?? "") != databaseId)
            throw new JsonException("Usage returned a different resource.");
        if (databaseId.Length > 0 && (!project.Databases.TryGetValue(databaseId, out var configured) || configured.ProjectId != project.Id)) throw new JsonException("Usage returned a foreign database.");
        var period = row.GetProperty("period");
        if (period.ValueKind != JsonValueKind.Object) throw new JsonException("Period usage is unavailable; lifetime counters cannot describe this range.");
        var start = period.GetProperty("from").GetDateTimeOffset(); var end = period.GetProperty("to").GetDateTimeOffset();
        if (start != from || end <= start || end > now || period.GetProperty("granularity").GetString() != granularity) throw new JsonException("Usage returned an invalid period.");
        var points = new List<ActivityPoint>();
        if (row.TryGetProperty("series", out var series))
            foreach (var point in series.EnumerateArray())
            {
                var timestamp = point.GetProperty("start").GetDateTimeOffset();
                if (timestamp < start || timestamp >= end || timestamp.Offset != TimeSpan.Zero ||
                    timestamp.TimeOfDay.Ticks % (granularity == "day" ? TimeSpan.TicksPerDay : TimeSpan.TicksPerHour) != 0)
                    throw new JsonException("Usage returned an invalid UTC bucket.");
                points.Add(new(timestamp, Count(point.GetProperty("reads")), Count(point.GetProperty("writes"))));
            }
        if (points.Select(point => point.Start).Distinct().Count() != points.Count) throw new JsonException("Usage repeated a UTC bucket.");
        var items = new List<UsageReading>();
        if (includeItems && row.TryGetProperty("items", out var children))
            foreach (var child in children.EnumerateArray())
            {
                var id = child.GetProperty("databaseId").GetString() ?? "";
                if (id.Length == 0 || items.Any(item => item.DatabaseId == id)) throw new JsonException("Usage returned an invalid database row.");
                items.Add(Parse(child, project, id, from, now, granularity, false));
            }
        return new(project.Id, databaseId, Count(row.GetProperty("logicalBytes")), Count(row.GetProperty("documents")),
            new(start, end, granularity, Count(period.GetProperty("reads")), Count(period.GetProperty("writes"))), points.OrderBy(point => point.Start).ToArray(), items);
    }
    private static long Count(JsonElement value) => value.GetInt64() is >= 0 and var count ? count : throw new JsonException("Usage counters must be nonnegative.");
    private static bool Failure(Exception error, CancellationToken cancellationToken) => error is HttpRequestException or IOException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException || error is OperationCanceledException && !cancellationToken.IsCancellationRequested;
}

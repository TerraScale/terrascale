using System.Text.Json;
using Dapper;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Sso;
using TerraScale.Authentication.Storage;

namespace TerraScale.Authentication.Features.ProjectOrganizations;

public sealed partial class ProjectOrganizationDirectory
{
    public async Task RecheckDomainsAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var cutoff = now.AddHours(-24);
        var domains = Versioned.All(await data.ReadAsync(connection => connection.QueryAsync<ProjectOrganizationDomain>(
            """
            SELECT * FROM project_organization_domains WHERE verified_at IS NOT NULL AND (checked_at IS NULL OR checked_at <= @cutoff)
                AND (lease_until IS NULL OR lease_until <= @now) ORDER BY checked_at LIMIT 100
            """, new { cutoff, now }), cancellationToken));
        await Parallel.ForEachAsync(domains, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            async (row, token) =>
            {
                var leased = row.Value with { LeaseUntil = clock.GetUtcNow().AddMinutes(1) };
                var claim = Change.Update(row, leased);
                if (!await data.SaveAsync(claim, token))
                {
                    return;
                }

                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20), clock);
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
                    var verified = await this.PublishedAsync(leased, linked.Token);
                    var failures = verified ? 0 : leased.FailedChecks + 1;
                    var updated = leased with
                    {
                        CheckedAt = clock.GetUtcNow(),
                        FailedChecks = failures,
                        VerifiedAt = failures >= 3 ? null : leased.VerifiedAt,
                        LeaseUntil = null,
                    };
                    List<Change> changes = [Change.Update(updated, claim.Version)];
                    if (updated.VerifiedAt is null)
                    {
                        changes.Add(Change.Insert(outbox.NewEvent(leased.ProjectId, "organizationDomain.updated", leased.Domain,
                            organizationId: leased.OrganizationId)));
                    }

                    await data.SaveAsync(changes, token);
                }
                catch (ApiException failure) when (failure.Code == "dns_unavailable")
                {
                    DomainCheckFailed(logger, failure, leased.ProjectId, leased.OrganizationId);
                }
                catch (OperationCanceledException failure) when (!token.IsCancellationRequested)
                {
                    DomainCheckFailed(logger, failure, leased.ProjectId, leased.OrganizationId);
                }
            });
    }

    private async Task<bool> PublishedAsync(ProjectOrganizationDomain domain, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{SsoConnections.Resolver}?name=_terrascale-org.{Uri.EscapeDataString(domain.Domain)}&type=TXT");
            request.Headers.Accept.ParseAdd("application/dns-json");
            using var response = await clients.CreateClient(SsoConnections.HttpClientName).SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var answer = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            if (answer.RootElement.ValueKind != JsonValueKind.Object || !answer.RootElement.TryGetProperty("Status", out var status) || status.ValueKind != JsonValueKind.Number || !status.TryGetInt32(out var code) || code is not (0 or 3))
            {
                throw new ApiException(503, "dns_unavailable", "The DNS resolver is unavailable.");
            }

            var expected = "terrascale-org=" + domain.VerificationToken;
            return answer.RootElement.TryGetProperty("Answer", out var records) && records.ValueKind == JsonValueKind.Array
                && records.EnumerateArray().Any(record => record.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.Number && type.TryGetInt32(out var number) && number == 16
                    && record.TryGetProperty("data", out var value) && value.ValueKind == JsonValueKind.String
                    && value.GetString() is { } text && text.Replace("\"", "", StringComparison.Ordinal) == expected);
        }
        catch (Exception failure) when (!cancellationToken.IsCancellationRequested && failure is HttpRequestException or JsonException or TaskCanceledException)
        {
            DomainCheckFailed(logger, failure, domain.ProjectId, domain.OrganizationId);
            throw new ApiException(503, "dns_unavailable", "The DNS resolver is unavailable.");
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Organization domain check failed for project {ProjectId}, organization {OrganizationId}.")]
    private static partial void DomainCheckFailed(ILogger logger, Exception exception, string projectId, string organizationId);
}

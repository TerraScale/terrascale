using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TerraScale.Authentication.Features.Sso;
using TerraScale.Authentication.Storage;

namespace TerraScale.Authentication.Features.ProjectOrganizations;

public sealed partial class ProjectOrganizationDirectory
{
    public async Task RecheckDomainsAsync(CancellationToken cancellationToken)
    {
        var cutoff = clock.GetUtcNow().AddHours(-24);
        var domains = await data.ReadAsync((db, token) => db.ProjectOrganizationDomains
            .Where(row => row.VerifiedAt != null && (row.CheckedAt == null || row.CheckedAt <= cutoff))
            .OrderBy(row => row.CheckedAt).Take(100).WithVersion().ToListAsync(token), cancellationToken).ConfigureAwait(false);
        foreach (var row in domains)
        {
            var verified = await this.PublishedAsync(row.Value, cancellationToken).ConfigureAwait(false);
            var failures = verified ? 0 : row.Value.FailedChecks + 1;
            var updated = row.Value with { CheckedAt = clock.GetUtcNow(), FailedChecks = failures, VerifiedAt = failures >= 3 ? null : row.Value.VerifiedAt };
            List<Change> changes = [Change.Update(row, updated)];
            if (updated.VerifiedAt is null)
            {
                changes.Add(Change.Insert(outbox.NewEvent(row.Value.ProjectId, "organizationDomain.updated", row.Value.Domain,
                    organizationId: row.Value.OrganizationId)));
            }

            await data.SaveAsync(changes, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> PublishedAsync(ProjectOrganizationDomain domain, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{SsoConnections.Resolver}?name=_terrascale-org.{Uri.EscapeDataString(domain.Domain)}&type=TXT");
            request.Headers.Accept.ParseAdd("application/dns-json");
            using var response = await clients.CreateClient(SsoConnections.HttpClientName).SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var answer = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var expected = "terrascale-org=" + domain.VerificationToken;
            return answer.RootElement.TryGetProperty("Answer", out var records) && records.ValueKind == JsonValueKind.Array
                && records.EnumerateArray().Any(record => record.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.Number && type.TryGetInt32(out var number) && number == 16
                    && record.TryGetProperty("data", out var value) && value.ValueKind == JsonValueKind.String
                    && value.GetString() is { } text && text.Replace("\"", "", StringComparison.Ordinal) == expected);
        }
        catch (Exception failure) when (!cancellationToken.IsCancellationRequested && failure is HttpRequestException or JsonException or TaskCanceledException)
        {
            DomainCheckFailed(logger, failure, domain.ProjectId, domain.OrganizationId);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Organization domain check failed for project {ProjectId}, organization {OrganizationId}.")]
    private static partial void DomainCheckFailed(ILogger logger, Exception exception, string projectId, string organizationId);
}

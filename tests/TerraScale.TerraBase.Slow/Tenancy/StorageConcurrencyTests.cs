using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using TerraScale.TerraBase.Testing;

namespace TerraScale.TerraBase.Slow.Tenancy;

public sealed class StorageConcurrencyTests
{
    [Test]
    public async Task Concurrent_creates_writes_and_deletes_in_one_project_keep_catalog_and_disk_in_step()
    {
        await using var api = await ManagementApi.StartAsync();
        using var tenant = await api.CreateTenantAsync("documents:read", "documents:write");
        using var owner = api.As(tenant.User);
        var databases = $"/v1/management/organizations/{tenant.OrganizationId}/projects/{tenant.ProjectId}/databases";
        var unexpected = new ConcurrentBag<string>();

        // Workers create, write and delete databases in the same project directory at once, so creations interleave with
        // deletes of the directory's last database.
        await Task.WhenAll(Enumerable.Range(0, 8).Select(async worker =>
        {
            for (var round = 0; round < 10; round++)
            {
                var name = $"w{worker}-{round}";
                using var created = await tenant.Data.PutAsync(tenant.Database(name), null);
                if (created.StatusCode != HttpStatusCode.Created)
                {
                    unexpected.Add($"create {name}: {(int)created.StatusCode}");
                    continue;
                }

                var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
                using var collection = await tenant.Data.PostAsJsonAsync(tenant.Database(name, "/collections"), new { name = "c" });
                using var written = await tenant.Data.PutAsJsonAsync(tenant.Database(name, $"/collections/c/documents/{Guid.CreateVersion7()}"), new { round });
                using var deleted = await owner.DeleteAsync(new Uri($"{databases}/{id}", UriKind.Relative));
                if (collection.StatusCode != HttpStatusCode.Created || written.StatusCode != HttpStatusCode.Created
                    || deleted.StatusCode != HttpStatusCode.NoContent)
                {
                    unexpected.Add($"{name}: collection {(int)collection.StatusCode}, write {(int)written.StatusCode}, delete {(int)deleted.StatusCode}");
                }
            }
        }));
        using var listed = JsonDocument.Parse(await owner.GetStringAsync(new Uri(databases, UriKind.Relative)));
        var directory = Path.Combine(api.Server.DataDirectory, tenant.OrganizationId, tenant.ProjectId);

        await Assert.That(unexpected).IsEmpty();
        await Assert.That(listed.RootElement.GetProperty("items").GetArrayLength()).IsEqualTo(0);
        await Assert.That(Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*.db").Count() : 0).IsEqualTo(0);
    }
}

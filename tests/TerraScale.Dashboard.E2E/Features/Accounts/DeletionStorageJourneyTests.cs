using Microsoft.EntityFrameworkCore;
using TerraScale.Dashboard.Features.Profile;
using TerraScale.Dashboard.Platform.TsAuth;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.E2E.Features.Accounts;

public sealed class DeletionStorageJourneyTests
{
    [Test]
    public async Task TheJournalKeepsOneEntryPerProofWithItsProjectsAndRefusesAChangedProof()
    {
        var environment = new DemoEnvironment();
        await environment.InitializeAsync();
        var organization = (await environment.Organizations.ListAsync(default)).Single();
        var journal = new DeletionCleanupJournal(new TestStorageFactory(environment.StorageAsync), environment.Organizations, TimeProvider.System);
        var proof = new TsAuthDeletionProof(AccountService.NewId(), DeletionCleanupRow.Organization, organization.Id, null);

        var remembered = await journal.RememberAsync(proof, default);
        var replayed = await journal.RememberAsync(proof, default);
        var listed = await journal.ListAsync(default);

        await Assert.That(remembered.ProjectIds).IsEquivalentTo(organization.Projects.Keys.Order(StringComparer.Ordinal).ToArray());
        await Assert.That(replayed.ProjectIds).IsEquivalentTo(remembered.ProjectIds);
        await Assert.That(listed.Single().Proof).IsEqualTo(proof);
        await Assert.That(listed.Single().ProjectIds).IsEquivalentTo(remembered.ProjectIds);
        await Assert.ThrowsAsync<IOException>(() => journal.RememberAsync(proof with { Scope = DeletionCleanupRow.Account, SubjectId = AccountService.NewId() }, default));
    }

    [Test]
    public async Task DeletionTablesRefuseRowsThatBreakTheirInvariants()
    {
        var environment = new DemoEnvironment();
        var cleanup = AccountService.NewId();
        var key = Key();
        await SaveAsync(environment, new DeletionCleanupRow { Id = cleanup, Scope = DeletionCleanupRow.Organization, SubjectId = AccountService.NewId(), CreatedAt = DateTime.UtcNow });
        await SaveAsync(environment, Pending(key));

        await Assert.That(await RefusedAsync(environment, new DeletionCleanupRow { Id = AccountService.NewId(), Scope = DeletionCleanupRow.Account, SubjectId = AccountService.NewId(), OrganizationId = AccountService.NewId(), CreatedAt = DateTime.UtcNow }))
            .IsEqualTo(TerraBaseSqlErrorCodes.CheckViolation);
        await Assert.That(await RefusedAsync(environment, new DeletionCleanupTargetRow { CleanupId = cleanup, Kind = DeletionCleanupTargetRow.Project, TargetId = "not-a-project" }))
            .IsEqualTo(TerraBaseSqlErrorCodes.CheckViolation);
        await Assert.That(await RefusedAsync(environment, new DeletionCleanupTargetRow { CleanupId = AccountService.NewId(), Kind = DeletionCleanupTargetRow.Customer, TargetId = "cus_orphan" }))
            .IsEqualTo(TerraBaseSqlErrorCodes.ForeignKeyViolation);
        await Assert.That(await RefusedAsync(environment, Pending(key))).IsEqualTo(TerraBaseSqlErrorCodes.UniqueViolation);
        var completed = Pending(Key());
        completed.CompletedUntil = DateTime.UtcNow;
        await Assert.That(await RefusedAsync(environment, completed)).IsEqualTo(TerraBaseSqlErrorCodes.CheckViolation);
    }

    private static string Key()
    {
        return Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()) + Convert.ToHexStringLower(Guid.NewGuid().ToByteArray());
    }

    private static AccountDeletionRow Pending(string key)
    {
        return new()
        {
            Id = AccountService.NewId(),
            KeyHash = key,
            UserId = "test-user",
            Nonce = new byte[12],
            Ciphertext = [1],
            Tag = new byte[16],
            CreatedAt = DateTime.UtcNow,
            Version = AccountService.NewId(),
        };
    }

    private static async Task SaveAsync(DemoEnvironment environment, object row)
    {
        await using var storage = await environment.StorageAsync();
        storage.Add(row);
        await storage.SaveChangesAsync();
    }

    private static async Task<string?> RefusedAsync(DemoEnvironment environment, object row)
    {
        try
        {
            await SaveAsync(environment, row);
            return null;
        }
        catch (DbUpdateException error) when (error.InnerException is TerraBaseSqlException refused)
        {
            return refused.Code;
        }
    }
}

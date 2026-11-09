using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.Common.Accounts;

public sealed record StoredAccount(AccountRecord Account, string Version, bool Erased)
{
    internal AccountRow Row { get; init; } = null!;

    internal IReadOnlyList<NotificationRow> Notifications { get; init; } = [];
}

public sealed class AccountStore(IDbContextFactory<DashboardDbContext> storage, TimeProvider time)
{
    public async Task<StoredAccount?> FindAsync(string subject, CancellationToken cancellationToken)
    {
        var hash = AccountService.AccountStorageKey(subject);
        var row = (await RowSync.QueryAsync(storage, db => db.Accounts.Where(account => account.SubjectHash == hash), cancellationToken)).SingleOrDefault();
        if (row is null)
        {
            return null;
        }

        if (row.ErasureId is not null)
        {
            return new(new AccountRecord { Id = row.Id }, row.Version, true) { Row = row };
        }

        if (row.UserId != subject)
        {
            throw new IOException("Account identity is invalid.");
        }

        var notifications = await RowSync.QueryAsync(storage, db => db.Notifications.Where(note => note.AccountId == row.Id)
            .OrderBy(note => note.CreatedAt).ThenBy(note => note.Id), cancellationToken);
        return new(Read(row, notifications), row.Version, false) { Row = row, Notifications = notifications };
    }

    public async Task CreateAsync(AccountRecord account, CancellationToken cancellationToken)
    {
        await using var db = await storage.CreateDbContextAsync(cancellationToken);
        db.Add(Write(account, AccountService.AccountStorageKey(account.UserId), account.UserId, AccountService.NewId()));
        db.AddRange(account.Notifications.Select(note => Write(account.Id, note)));
        await RowSync.CreateAsync(db, cancellationToken);
    }

    public async Task ReplaceAsync(StoredAccount stored, CancellationToken cancellationToken)
    {
        if (stored.Erased || stored.Account.Id != stored.Row.Id)
        {
            throw new StateConflictException();
        }

        await using var db = await storage.CreateDbContextAsync(cancellationToken);
        db.Attach(stored.Row);
        db.AttachRange(stored.Notifications);
        db.Entry(stored.Row).CurrentValues.SetValues(Write(stored.Account, stored.Row.SubjectHash, stored.Row.UserId, stored.Row.Version));
        RowSync.Sync(db, stored.Notifications, stored.Account.Notifications.Select(note => Write(stored.Row.Id, note)), note => note.Id);
        if (!db.ChangeTracker.HasChanges())
        {
            return;
        }

        stored.Row.Version = AccountService.NewId();
        await RowSync.ReplaceAsync(db, cancellationToken);
    }

    public async Task FenceAsync(string subject, string erasureId, CancellationToken cancellationToken)
    {
        if (!TerraScaleIds.IsValid(erasureId))
        {
            throw new IOException("Erasure identity is invalid.");
        }

        for (var attempt = 0; ; attempt++)
        {
            var stored = await FindAsync(subject, cancellationToken);
            if (stored is { Erased: true })
            {
                return;
            }

            await using var db = await storage.CreateDbContextAsync(cancellationToken);
            var erased = new AccountRecord { Locale = "", OnboardingStep = "", CreatedAt = time.GetUtcNow() };
            if (stored is null)
            {
                erased.Id = AccountService.NewId();
                db.Add(Write(erased, AccountService.AccountStorageKey(subject), null, AccountService.NewId(), erasureId));
            }
            else
            {
                erased.Id = stored.Row.Id;
                erased.CreatedAt = RowSync.At(stored.Row.CreatedAt);
                db.Attach(stored.Row);
                db.AttachRange(stored.Notifications);
                db.Entry(stored.Row).CurrentValues.SetValues(Write(erased, stored.Row.SubjectHash, null, AccountService.NewId(), erasureId));
                db.RemoveRange(stored.Notifications);
            }

            try
            {
                await (stored is null ? RowSync.CreateAsync(db, cancellationToken) : RowSync.ReplaceAsync(db, cancellationToken));
                return;
            }
            catch (StateConflictException) when (attempt < 3) { }
        }
    }

    public async Task RemoveNotificationsAsync(string organizationId, string? projectId, CancellationToken cancellationToken)
    {
        await using var db = await storage.CreateDbContextAsync(cancellationToken);
        await db.Notifications.Where(note => note.OrganizationId == organizationId && (projectId == null || note.ProjectId == projectId)).ExecuteDeleteAsync(cancellationToken);
    }

    private static AccountRecord Read(AccountRow row, IEnumerable<NotificationRow> notifications)
    {
        return new()
        {
            Id = row.Id,
            UserId = row.UserId!,
            Name = row.Name,
            Email = row.Email,
            Phone = row.Phone,
            Locale = row.Locale,
            OnboardingCompleted = row.OnboardingCompleted,
            OnboardingStep = row.OnboardingStep,
            OrganizationIds = [.. JsonSerializer.Deserialize(row.OrganizationIds, AccountRowJson.Default.StringArray)!],
            PersonalOrganizationId = row.PersonalOrganizationId,
            PersonalClaimUntil = row.PersonalClaimUntil is { } claim ? RowSync.At(claim) : null,
            ActiveOrganizationId = row.ActiveOrganizationId,
            ActiveProjectId = row.ActiveProjectId,
            CreatedAt = RowSync.At(row.CreatedAt),
            Notifications = [.. notifications.Select(note => new NotificationRecord
            {
                Id = note.Id,
                OrganizationId = note.OrganizationId,
                ProjectId = note.ProjectId,
                Type = note.Type,
                BillingMonth = note.BillingMonth,
                Title = note.Title,
                Body = note.Body,
                Severity = note.Severity,
                Status = note.Status,
                Url = note.Url,
                CreatedAt = RowSync.At(note.CreatedAt)
            })]
        };
    }

    private static AccountRow Write(AccountRecord account, string subjectHash, string? userId, string version, string? erasureId = null)
    {
        return new()
        {
            Id = account.Id,
            SubjectHash = subjectHash,
            UserId = userId,
            Name = account.Name,
            Email = account.Email,
            Phone = account.Phone,
            Locale = account.Locale,
            OnboardingCompleted = account.OnboardingCompleted,
            OnboardingStep = account.OnboardingStep,
            OrganizationIds = JsonSerializer.Serialize(account.OrganizationIds.ToArray(), AccountRowJson.Default.StringArray),
            PersonalOrganizationId = account.PersonalOrganizationId,
            PersonalClaimUntil = account.PersonalClaimUntil?.UtcDateTime,
            ActiveOrganizationId = account.ActiveOrganizationId,
            ActiveProjectId = account.ActiveProjectId,
            CreatedAt = account.CreatedAt.UtcDateTime,
            ErasureId = erasureId,
            Version = version
        };
    }

    private static NotificationRow Write(string accountId, NotificationRecord note)
    {
        return new()
        {
            AccountId = accountId,
            Id = note.Id,
            OrganizationId = note.OrganizationId,
            ProjectId = note.ProjectId,
            Type = note.Type,
            BillingMonth = note.BillingMonth,
            Title = note.Title,
            Body = note.Body,
            Severity = note.Severity,
            Status = note.Status,
            Url = note.Url,
            CreatedAt = note.CreatedAt.UtcDateTime
        };
    }
}

using System.Data;
using System.Data.Common;
using System.Text.Json;
using Dapper;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Common.Accounts;

public sealed record StoredAccount(AccountRecord Account, string Version, bool Erased)
{
    public required AccountRow Row { get; init; }

    public IReadOnlyList<NotificationRow> Notifications { get; init; } = [];
}

public sealed class AccountStore(DashboardDb storage, TimeProvider time)
{
    private const string BySubject = DashboardSql.Accounts.Select + " WHERE subject_hash = @Hash";
    private const string NotificationsOf = DashboardSql.Notifications.Select + " WHERE account_id = @AccountId ORDER BY created_at, id";
    private const string RemoveNotifications = "DELETE FROM notifications WHERE organization_id = @OrganizationId AND (@ProjectId IS NULL OR project_id = @ProjectId)";
    private const string RemoveAccountNotifications = "DELETE FROM notifications WHERE account_id = @AccountId";

    public async Task<StoredAccount?> FindAsync(string subject, CancellationToken cancellationToken)
    {
        var hash = AccountService.AccountStorageKey(subject);
        await using var connection = await storage.OpenAsync(cancellationToken);
        if (await connection.QuerySingleOrDefaultAsync<AccountRow>(BySubject, new { Hash = hash }) is not { } row)
        {
            return null;
        }

        if (row.ErasureId is not null)
        {
            return new(new AccountRecord { Id = row.Id }, row.Version, true) { Row = row };
        }

        var notifications = (await connection.QueryAsync<NotificationRow>(NotificationsOf, new { AccountId = row.Id })).AsList();
        return row.UserId != subject ? throw new IOException("Account identity is invalid.") :
            new(Read(row, notifications), row.Version, false) { Row = row, Notifications = notifications };
    }

    public async Task CreateAsync(AccountRecord account, CancellationToken cancellationToken)
    {
        var row = Write(account, AccountService.AccountStorageKey(account.UserId), account.UserId, AccountService.NewId());
        var notes = account.Notifications.Select(note => Write(account.Id, note)).ToArray();
        try
        {
            await storage.WriteAsync(async (connection, transaction) =>
            {
                await connection.ExecuteAsync(DashboardSql.Accounts.Insert, row, transaction);
                await InsertAsync(connection, transaction, notes);
            }, cancellationToken);
        }
        catch (Exception error) when (DashboardDb.IsDuplicate(error))
        {
            throw new DBConcurrencyException("Dashboard storage changed concurrently.", error);
        }
    }

    public async Task ReplaceAsync(StoredAccount stored, CancellationToken cancellationToken)
    {
        if (stored.Erased || stored.Account.Id != stored.Row.Id)
        {
            throw new DBConcurrencyException("Dashboard storage changed concurrently.");
        }

        var next = Write(stored.Account, stored.Row.SubjectHash, stored.Row.UserId, stored.Row.Version);
        var notes = RowSync.Diff(stored.Notifications, stored.Account.Notifications.Select(note => Write(stored.Row.Id, note)), static note => note.Id);
        if (next == stored.Row && !notes.Any)
        {
            return;
        }

        next.OriginalVersion = stored.Row.Version;
        next.Version = AccountService.NewId();
        try
        {
            await storage.WriteAsync(async (connection, transaction) =>
            {
                DashboardDb.Expect(await connection.ExecuteAsync(DashboardSql.Accounts.Update, next, transaction));
                await ApplyAsync(connection, transaction, notes);
            }, cancellationToken);
        }
        catch (Exception error) when (DashboardDb.IsDuplicate(error))
        {
            throw new AccountAccessException("The change conflicts with an existing record.", StatusCodes.Status409Conflict);
        }

        stored.Row.Version = next.Version;
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

            var erased = new AccountRecord { Locale = "", OnboardingStep = "", CreatedAt = time.GetUtcNow() };
            try
            {
                if (stored is null)
                {
                    erased.Id = AccountService.NewId();
                    var row = Write(erased, AccountService.AccountStorageKey(subject), null, AccountService.NewId(), erasureId);
                    await storage.WriteAsync((connection, transaction) => connection.ExecuteAsync(DashboardSql.Accounts.Insert, row, transaction), cancellationToken);
                }
                else
                {
                    erased.Id = stored.Row.Id;
                    erased.CreatedAt = RowSync.At(stored.Row.CreatedAt);
                    var row = Write(erased, stored.Row.SubjectHash, null, AccountService.NewId(), erasureId);
                    row.OriginalVersion = stored.Row.Version;
                    await storage.WriteAsync(async (connection, transaction) =>
                    {
                        await connection.ExecuteAsync(RemoveAccountNotifications, new { AccountId = row.Id }, transaction);
                        DashboardDb.Expect(await connection.ExecuteAsync(DashboardSql.Accounts.Update, row, transaction));
                    }, cancellationToken);
                }

                return;
            }
            catch (Exception error) when (attempt < 3 && (error is DBConcurrencyException || DashboardDb.IsDuplicate(error)))
            {
                await DashboardDb.ConflictBackoffAsync(attempt, time, cancellationToken);
            }
        }
    }

    public async Task RemoveNotificationsAsync(string organizationId, string? projectId, CancellationToken cancellationToken)
    {
        await using var connection = await storage.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(RemoveNotifications, new { OrganizationId = organizationId, ProjectId = projectId });
    }

    private static async Task InsertAsync(TerraBaseDbConnection connection, DbTransaction transaction, IEnumerable<NotificationRow> notes)
    {
        foreach (var note in notes)
        {
            await connection.ExecuteAsync(DashboardSql.Notifications.Insert, note, transaction);
        }
    }

    private static async Task ApplyAsync(TerraBaseDbConnection connection, DbTransaction transaction, RowChanges<NotificationRow> notes)
    {
        foreach (var note in notes.Removed)
        {
            await connection.ExecuteAsync(DashboardSql.Notifications.Delete, note, transaction);
        }

        foreach (var note in notes.Changed)
        {
            await connection.ExecuteAsync(DashboardSql.Notifications.Update, note, transaction);
        }

        await InsertAsync(connection, transaction, notes.Added);
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

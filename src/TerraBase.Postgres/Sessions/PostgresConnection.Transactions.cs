using TerraBase.Postgres.Protocol;
using TerraBase.Postgres.Sql;
using TerraScale.Contracts.Cluster;
using TerraScale.Contracts.Consistency;

namespace TerraBase.Postgres.Sessions;

internal sealed partial class PostgresConnection
{
    private PgOutcome Begin(PgBeginCommand begin)
    {
        if (session.Transaction is not null)
        {
            return new PgResult(null, [], "BEGIN", new PgNotice("25001", "there is already a transaction in progress"));
        }

        var access = session.Grant.MaxAccess == SqlAccess.Read ? SqlAccess.Read : begin.Access ?? session.Settings.DefaultAccess;
        session.Transaction = new PostgresTransactionBlock(begin.Mode ?? session.Settings.DefaultMode, access, isImplicit: false);
        return PgResult.Command("BEGIN");
    }

    private async ValueTask<Result<ISqlTransaction, PgError>> EnsureTransactionAsync(PostgresTransactionBlock block, CancellationToken cancellationToken)
    {
        if (block.Active is { } active)
        {
            return Result(active);
        }

        var options = new SqlTransactionOptions(block.Mode, block.Access, session.Bookmark);
        return await session.Executor.BeginTransactionAsync(options, cancellationToken).ConfigureAwait(false) switch
        {
            ISqlTransaction transaction => Activate(block, transaction),
            StorageBusy busy => PgErrors.From(busy),
            RequestTimeout timeout => PgErrors.From(timeout),
        };
    }

    private static Result<ISqlTransaction, PgError> Result(ISqlTransaction transaction) => new(transaction);

    private static Result<ISqlTransaction, PgError> Activate(PostgresTransactionBlock block, ISqlTransaction transaction)
    {
        block.Active = transaction;
        return Result(transaction);
    }

    private async ValueTask<PgOutcome> CommitAsync(CancellationToken cancellationToken)
    {
        if (session.Transaction is not { } block)
        {
            return new PgResult(null, [], "COMMIT", new PgNotice("25P01", "there is no transaction in progress"));
        }

        if (block.Failed)
        {
            await EndTransactionAsync(block, rollback: true).ConfigureAwait(false);
            return PgResult.Command("ROLLBACK");
        }

        return await FinishAsync(block, cancellationToken).ConfigureAwait(false) switch
        {
            PgError error => error,
            null => PgResult.Command("COMMIT"),
        };
    }

    private async ValueTask<PgError?> FinishAsync(PostgresTransactionBlock block, CancellationToken cancellationToken)
    {
        if (block.Active is not { } active)
        {
            await EndTransactionAsync(block, rollback: false).ConfigureAwait(false);
            return null;
        }

        var committed = await session.Executor.CommitAsync(active, cancellationToken).ConfigureAwait(false);
        var failure = committed switch
        {
            CommitMetadata metadata => Committed(metadata),
            SerializationConflict conflict => PgErrors.From(conflict),
            SchemaMismatch mismatch => PgErrors.From(mismatch),
            Unavailable unavailable => PgErrors.From(unavailable),
            RequestTimeout timeout => PgErrors.From(timeout),
            StorageBusy busy => PgErrors.From(busy),
        };
        await EndTransactionAsync(block, rollback: failure is not null).ConfigureAwait(false);
        return failure;
    }

    private PgError? Committed(CommitMetadata metadata)
    {
        session.Observe(metadata.Bookmark);
        return null;
    }

    private async ValueTask<PgOutcome> RollbackAsync()
    {
        if (session.Transaction is not { } block)
        {
            return new PgResult(null, [], "ROLLBACK", new PgNotice("25P01", "there is no transaction in progress"));
        }

        await EndTransactionAsync(block, rollback: true).ConfigureAwait(false);
        return PgResult.Command("ROLLBACK");
    }

    private async ValueTask<PgError?> EndImplicitAsync(bool commit)
    {
        if (session.Transaction is not { Implicit: true } block)
        {
            return null;
        }

        if (commit)
        {
            return await FinishAsync(block, closed).ConfigureAwait(false);
        }

        await EndTransactionAsync(block, rollback: true).ConfigureAwait(false);
        return null;
    }

    private async ValueTask EndTransactionAsync(PostgresTransactionBlock block, bool rollback)
    {
        session.Transaction = null;
        foreach (var change in session.Settings.EndTransaction())
        {
            session.Report(change);
        }

        if (block.Active is not { } active)
        {
            return;
        }

        block.Active = null;
        if (rollback)
        {
            await active.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }

        await active.DisposeAsync().ConfigureAwait(false);
    }

    private async ValueTask<PgOutcome> SavepointAsync(string sql, string tag, CancellationToken cancellationToken)
    {
        if (session.Transaction is not { } block)
        {
            return new PgError("25P01", $"{tag} can only be used in transaction blocks");
        }

        return await ExecuteStatementAsync(new SqlStatement(sql, [], NoNamed), block.Access, cancellationToken).ConfigureAwait(false) switch
        {
            SqlResultSet => PgResult.Command(tag),
            PgError error => error,
        };
    }

    private async ValueTask<PgOutcome> RollbackToAsync(PgRollbackToCommand command, CancellationToken cancellationToken)
    {
        if (session.Transaction is not { } block)
        {
            return new PgError("25P01", "ROLLBACK TO SAVEPOINT can only be used in transaction blocks");
        }

        if (block.Active is null)
        {
            return new PgError("3B001", $"savepoint \"{command.Name}\" does not exist");
        }

        var failed = block.Failed;
        block.Failed = false;
        var outcome = await ExecuteStatementAsync(new SqlStatement($"ROLLBACK TO SAVEPOINT {command.Name}", [], NoNamed), block.Access, cancellationToken).ConfigureAwait(false);
        return outcome switch
        {
            SqlResultSet => PgResult.Command("ROLLBACK"),
            PgError error => Restore(block, failed, error),
        };
    }

    private static PgError Restore(PostgresTransactionBlock block, bool failed, PgError error)
    {
        block.Failed = failed;
        return error;
    }

    private PgOutcome SetTransaction(PgSetTransactionCommand command)
    {
        if (session.Transaction is not { } block)
        {
            return new PgResult(null, [], "SET", new PgNotice("25P01", "SET TRANSACTION can only be used in transaction blocks"));
        }

        if (block.Active is not null && command.Mode is { } mode && mode != block.Mode)
        {
            return new PgError("25001", "SET TRANSACTION ISOLATION LEVEL must be called before any query");
        }

        block.Mode = command.Mode ?? block.Mode;
        block.Access = session.Grant.MaxAccess == SqlAccess.Read ? SqlAccess.Read : command.Access ?? block.Access;
        return PgResult.Command("SET");
    }

    private PgOutcome Set(PgSetCommand command)
    {
        var name = PgSettings.CanonicalName(command.Name);
        if (name.Equals("transaction_isolation", StringComparison.OrdinalIgnoreCase))
        {
            return SetTransaction(new PgSetTransactionCommand(
                command.Value?.Equals("serializable", StringComparison.OrdinalIgnoreCase) == true ? ConsistencyMode.Strong : ConsistencyMode.Fast, null));
        }

        if (name.Equals("transaction_read_only", StringComparison.OrdinalIgnoreCase))
        {
            return SetTransaction(new PgSetTransactionCommand(null, command.Value is "on" or "true" ? SqlAccess.Read : SqlAccess.Write));
        }

        if (command.Local && session.Transaction is null)
        {
            return new PgResult(null, [], "SET", new PgNotice("25P01", "SET LOCAL can only be used in transaction blocks"));
        }

        return Apply(name, command.Value, command.Local, PgResult.Command(command.Value is null ? "RESET" : "SET"));
    }

    private PgOutcome Apply(string name, string? value, bool local, PgResult success)
    {
        return session.Settings.Set(name, value, local) switch
        {
            PgSettingChange change => Applied(change, success),
            PgError error => error,
        };
    }

    private PgResult Applied(PgSettingChange change, PgResult success)
    {
        if (change.Name == PgSettings.Bookmark)
        {
            if (CausalBookmark.TryDecode(change.Value, out var bookmark))
            {
                session.Observe(bookmark);
            }

            return success;
        }

        session.Report(change);
        return success;
    }

    private PgOutcome ResetAll()
    {
        foreach (var change in session.Settings.ResetAll())
        {
            session.Report(change);
        }

        return PgResult.Command("RESET");
    }

    private PgOutcome SetConfig(PgSetConfigCommand command)
    {
        var applied = Apply(command.Name, command.Value, command.Local && session.Transaction is not null, PgResult.Command("SELECT 1"));
        return applied switch
        {
            PgResult => new PgResult([new PgColumn("set_config", PgOid.Text)], [[SqlValue.FromText(command.Value)]], "SELECT 1"),
            PgError error => error,
        };
    }

    private PgOutcome Show(PgShowCommand command)
    {
        if (command.Name.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            IReadOnlyList<IReadOnlyList<SqlValue>> rows = [.. session.Settings.Values.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(static pair => (IReadOnlyList<SqlValue>)[SqlValue.FromText(pair.Key), SqlValue.FromText(pair.Value), SqlValue.FromText(string.Empty)])];
            return new PgResult([new PgColumn("name", PgOid.Text), new PgColumn("setting", PgOid.Text), new PgColumn("description", PgOid.Text)], rows, "SHOW");
        }

        var name = PgSettings.CanonicalName(command.Name);
        string value;
        if (name.Equals("transaction_isolation", StringComparison.OrdinalIgnoreCase))
        {
            value = (session.Transaction?.Mode ?? session.Settings.DefaultMode) == ConsistencyMode.Strong ? "serializable" : "read committed";
        }
        else if (name.Equals("transaction_read_only", StringComparison.OrdinalIgnoreCase))
        {
            value = (session.Transaction?.Access ?? session.Settings.DefaultAccess) == SqlAccess.Read ? "on" : "off";
        }
        else if (!session.Settings.TryGet(name, out value))
        {
            return new PgError("42704", $"unrecognized configuration parameter \"{command.Name}\"");
        }

        return new PgResult([new PgColumn(command.Name.ToLowerInvariant(), PgOid.Text)], [[SqlValue.FromText(value)]], "SHOW");
    }

    private PgOutcome Deallocate(PgDeallocateCommand command)
    {
        if (command.Name is null)
        {
            session.Statements.Clear();
            return PgResult.Command("DEALLOCATE ALL");
        }

        return session.Statements.Remove(command.Name)
            ? PgResult.Command("DEALLOCATE")
            : new PgError("26000", $"prepared statement \"{command.Name}\" does not exist");
    }

    private async ValueTask<PgOutcome> DiscardAsync()
    {
        if (session.Transaction is not null)
        {
            return new PgError("25001", "DISCARD ALL cannot run inside a transaction block");
        }

        session.Statements.Clear();
        session.Portals.Clear();
        await StopListeningAsync(session).ConfigureAwait(false);
        foreach (var change in session.Settings.ResetAll())
        {
            session.Report(change);
        }

        return PgResult.Command("DISCARD ALL");
    }
}

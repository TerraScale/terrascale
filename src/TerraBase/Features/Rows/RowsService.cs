using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Features.Rules;
using TerraBase.Features.Shapes;
using TerraBase.Features.Sql;
using TerraBase.Features.UserAuth;
using TerraBase.Primitives;
using TerraBase.Tenancy;

namespace TerraBase.Features.Rows;

internal sealed class RowsService(ISqlService sql, TimeProvider time)
{
    private static readonly RowRefused Moving = new(StatusCodes.Status503ServiceUnavailable, "unavailable", "the database is not written in this region right now; retry");

    public async ValueTask<RowsOutcome> WriteAsync(TenantContext database, RulesAuth? auth, IReadOnlyList<RowOperation> operations, bool batch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operations);
        var session = sql.Open(database, canWrite: true);
        await using (session.ConfigureAwait(false))
        {
            if (session is not SqlSession home)
            {
                return Moving;
            }

            RowRefused? refused = null;
            List<RowAnswer> answers = [];
            home.BeginRequest();
            var failed = await home.WriteAsync(async (transaction, timeout, token) =>
            {
                var rules = transaction as IRulesWriter ?? throw new NotSupportedException("the storage transaction does not support rules");
                var stored = auth is null ? null : await rules.ReadRulesAsync(null, token).ConfigureAwait(false);
                var run = new Run(transaction, rules, timeout, auth, stored, time.GetUtcNow().ToUnixTimeMilliseconds(), token);
                List<string> kinds = [];
                for (var index = 0; index < operations.Count; index++)
                {
                    switch (await run.ApplyAsync(operations[index]).ConfigureAwait(false))
                    {
                        case RowAnswer answer:
                            answers.Add(answer);
                            kinds.Add(operations[index] switch
                            {
                                CreateRow => "put",
                                UpdateRow => "patch",
                                DeleteRow => "delete",
                            });
                            break;
                        case RowRefused denied:
                            refused = batch ? denied with { Details = (denied.Details ?? new RowsErrorDetails()) with { Index = index } } : denied;
                            return null;
                    }
                }

                return kinds;
            }, cancellationToken).ConfigureAwait(false);
            await home.EndRequestAsync().ConfigureAwait(false);
            return failed is not null ? Refusal(failed) : refused is not null ? refused : answers;
        }
    }

    private static RowRefused Refusal(SqlError error) => error.Code switch
    {
        SqlErrors.UniqueViolation or SqlErrors.PrimaryKeyViolation => new(StatusCodes.Status409Conflict, "row_exists", error.Message),
        SqlErrors.NotNullViolation or SqlErrors.CheckViolation or SqlErrors.ForeignKeyViolation or SqlErrors.ConstraintViolation =>
            new(StatusCodes.Status400BadRequest, "constraint_failed", error.Message),
        var code => new(SqlErrors.Status(code), code, error.Message),
    };

    private sealed record Checks(
        bool Owner, CompiledCondition? Read, CompiledCondition? Rule, IReadOnlyList<CompiledCondition> Validate, IReadOnlyList<string> Columns,
        IReadOnlyList<string> Stored)
    {
        public string ReadSql => Owner ? "1" : Read?.Sql ?? "0";

        public string RuleSql => Owner ? "1" : Rule!.Sql;
    }

    private sealed record Assignment(string Column, string Sql, object? Value, bool Bound);

    private sealed class Run(IStorageTransaction transaction, IRulesWriter rules, TimeSpan timeout, RulesAuth? auth, StoredRules? stored, long now,
        CancellationToken cancellationToken)
    {
        public async ValueTask<object> ApplyAsync(RowOperation operation)
        {
            var (kind, name) = operation switch
            {
                CreateRow => (RulesOperation.Create, "create"),
                UpdateRow => (RulesOperation.Update, "update"),
                DeleteRow => (RulesOperation.Delete, "delete"),
            };
            RulesTable? rule = null;
            if (auth is not null)
            {
                rule = stored?.Rules.Table(operation.Table);
                if (rule is null || Condition(rule, kind) is null)
                {
                    return Denied(name, operation.Table);
                }
            }

            var schema = await rules.ReadRulesTableAsync(operation.Table, cancellationToken).ConfigureAwait(false);
            switch (schema.Status)
            {
                case ShapeTableStatus.Missing:
                    return new RowRefused(StatusCodes.Status404NotFound, "unknown_table", $"the database has no table {operation.Table}");
                case ShapeTableStatus.Unsupported:
                    return new RowRefused(StatusCodes.Status400BadRequest, "invalid_request", "rows are written to ordinary rowid tables");
            }

            if (Compile(schema, rule, kind) is not Checks checks)
            {
                return RulesInvalid;
            }

            return operation switch
            {
                CreateRow create => await CreateAsync(create, schema, rule, checks).ConfigureAwait(false),
                UpdateRow update => await UpdateAsync(update, schema, rule, checks).ConfigureAwait(false),
                DeleteRow delete => await DeleteAsync(delete, checks, name).ConfigureAwait(false),
            };
        }

        private async ValueTask<object> CreateAsync(CreateRow operation, RulesTableSchema schema, RulesTable? rule, Checks checks)
        {
            var prepared = Assignments(operation.Values, schema, rule, rule?.Set?.Create, update: false);
            if (prepared is not List<Assignment> assignments)
            {
                return prepared;
            }

            var table = ShapeFilters.Quote(operation.Table);
            var named = Named(assignments);
            var insert = assignments.Count == 0
                ? $"INSERT OR ABORT INTO {table} DEFAULT VALUES RETURNING rowid"
                : $"INSERT OR ABORT INTO {table} ({string.Join(", ", assignments.Select(item => ShapeFilters.Quote(item.Column)))}) VALUES ({string.Join(", ", assignments.Select(item => item.Sql))}) RETURNING rowid";
            IReadOnlyList<object?[]> inserted;
            try
            {
                inserted = await QueryAsync(insert, named).ConfigureAwait(false);
            }
            catch (SqlStatementException error) when (!error.TransactionEnded && error.Failure is Unique or PrimaryKey)
            {
                return await ExistsAsync(error.Message, operation.Table, schema, checks, Post(null, schema, assignments), null).ConfigureAwait(false);
            }
            catch (SqlStatementException error) when (!error.TransactionEnded && error.Failure is NotNull or Check or ForeignKeyFailure or Constraint)
            {
                return ConstraintFailed(error);
            }

            return inserted is [[long key, ..], ..]
                ? await CheckedAsync(operation.Table, key, checks, null, "create").ConfigureAwait(false)
                : new RowRefused(StatusCodes.Status400BadRequest, "constraint_failed", "the table's triggers stored no row");
        }

        private async ValueTask<object> UpdateAsync(UpdateRow operation, RulesTableSchema schema, RulesTable? rule, Checks checks)
        {
            var prepared = Assignments(operation.Values, schema, rule, rule?.Set?.Update, update: true);
            if (prepared is not List<Assignment> assignments)
            {
                return prepared;
            }

            var table = ShapeFilters.Quote(operation.Table);
            var named = Keyed(operation.Key);
            Bind(named, checks.Read, null, schema.Columns);
            var before = await QueryAsync(
                $"SELECT {string.Join(", ", schema.Columns.Select(ShapeFilters.Quote))}, {checks.ReadSql} FROM {table} WHERE rowid = $key", named).ConfigureAwait(false);
            if (before is not [var image, ..] || !checks.Owner && image[^1] is not 1L)
            {
                return RowNotFound;
            }

            if (assignments.Count == 0)
            {
                return new RowRefused(StatusCodes.Status400BadRequest, "invalid_request", "row names no column to change");
            }

            var changes = Named(assignments);
            changes["key"] = operation.Key;
            try
            {
                _ = await QueryAsync(
                    $"UPDATE OR ABORT {table} SET {string.Join(", ", assignments.Select(item => $"{ShapeFilters.Quote(item.Column)} = {item.Sql}"))} WHERE rowid = $key",
                    changes).ConfigureAwait(false);
            }
            catch (SqlStatementException error) when (!error.TransactionEnded && error.Failure is Unique or PrimaryKey)
            {
                return await ExistsAsync(error.Message, operation.Table, schema, checks, Post(image, schema, assignments), operation.Key).ConfigureAwait(false);
            }
            catch (SqlStatementException error) when (!error.TransactionEnded && error.Failure is NotNull or Check or ForeignKeyFailure or Constraint)
            {
                return ConstraintFailed(error);
            }

            return await CheckedAsync(operation.Table, operation.Key, checks, image, "update").ConfigureAwait(false);
        }

        private async ValueTask<object> DeleteAsync(DeleteRow operation, Checks checks, string name)
        {
            var table = ShapeFilters.Quote(operation.Table);
            var named = Keyed(operation.Key);
            Bind(named, checks.Read, null, checks.Stored);
            Bind(named, checks.Rule, null, checks.Stored);
            var found = await QueryAsync($"SELECT {checks.ReadSql}, {checks.RuleSql} FROM {table} WHERE rowid = $key", named).ConfigureAwait(false);
            if (found is not [var row, ..] || row[0] is not 1L)
            {
                return RowNotFound;
            }

            if (row[1] is not 1L)
            {
                return Denied(name, operation.Table);
            }

            try
            {
                _ = await QueryAsync($"DELETE FROM {table} WHERE rowid = $key", Keyed(operation.Key)).ConfigureAwait(false);
            }
            catch (SqlStatementException error) when (!error.TransactionEnded && error.Failure is NotNull or Check or ForeignKeyFailure or Constraint)
            {
                return ConstraintFailed(error);
            }

            return new RowAnswer(Key(operation.Key), null);
        }

        private async ValueTask<object> CheckedAsync(string table, long key, Checks checks, object?[]? before, string name)
        {
            var named = Keyed(key);
            Bind(named, checks.Rule, before, checks.Stored);
            Bind(named, checks.Read, null, checks.Stored);
            foreach (var clause in checks.Validate)
            {
                Bind(named, clause, before, checks.Stored);
            }

            List<string> select = [.. checks.Columns.Select(ShapeFilters.Quote), checks.RuleSql, .. checks.Validate.Select(clause => clause.Sql), checks.ReadSql];
            var found = await QueryAsync($"SELECT {string.Join(", ", select)} FROM {ShapeFilters.Quote(table)} WHERE rowid = $key", named).ConfigureAwait(false);
            if (found is not [var row, ..])
            {
                return checks.Owner ? new RowAnswer(Key(key), null) : Denied(name, table);
            }

            var width = checks.Columns.Count;
            if (row[width] is not 1L)
            {
                return Denied(name, table);
            }

            for (var clause = 0; clause < checks.Validate.Count; clause++)
            {
                if (row[width + 1 + clause] is not 1L)
                {
                    return new RowRefused(StatusCodes.Status400BadRequest, "validation_failed", $"validate[{clause}] is false for the written row",
                        new RowsErrorDetails(Table: table, Clause: clause));
                }
            }

            return new RowAnswer(Key(key), row[^1] is 1L
                ? new RowValues([.. checks.Columns.Select((column, index) => KeyValuePair.Create(column, row[index]))])
                : null);
        }

        private async ValueTask<RowRefused> ExistsAsync(string message, string table, RulesTableSchema schema, Checks checks,
            Dictionary<string, object?> post, long? self)
        {
            var refused = new RowRefused(StatusCodes.Status409Conflict, "row_exists", "a row with the same unique key exists");
            var named = new Dictionary<string, object?>(StringComparer.Ordinal);
            List<string> where = [];
            foreach (var (_, name) in SqlErrors.ConstraintColumns(message))
            {
                if (schema.Columns.FirstOrDefault(column => string.Equals(column, name, StringComparison.OrdinalIgnoreCase)) is not { } column
                    || !post.TryGetValue(column, out var value) || value is null)
                {
                    return refused;
                }

                named["k" + where.Count] = value;
                where.Add($"{ShapeFilters.Quote(column)} = $k{where.Count}");
            }

            if (where.Count == 0)
            {
                return refused;
            }

            if (self is { } own)
            {
                named["self"] = own;
                where.Add("rowid <> $self");
            }

            Bind(named, checks.Read, null, schema.Columns);
            var found = await QueryAsync($"SELECT rowid, {checks.ReadSql} FROM {ShapeFilters.Quote(table)} WHERE {string.Join(" AND ", where)} LIMIT 1", named)
                .ConfigureAwait(false);
            return found is [[long key, 1L], ..] ? refused with { Details = new RowsErrorDetails(Key: Key(key)) } : refused;
        }

        private object Assignments(IReadOnlyDictionary<string, object?> values, RulesTableSchema schema, RulesTable? rule,
            IReadOnlyDictionary<string, string>? set, bool update)
        {
            List<Assignment> assignments = [];
            foreach (var (given, value) in values)
            {
                if (schema.Columns.FirstOrDefault(column => string.Equals(column, given, StringComparison.OrdinalIgnoreCase)) is not { } column)
                {
                    return new RowRefused(StatusCodes.Status400BadRequest, "unknown_column", $"the table has no column {given}", new RowsErrorDetails(Column: given));
                }

                if (assignments.Exists(item => item.Column == column))
                {
                    return new RowRefused(StatusCodes.Status400BadRequest, "invalid_request", $"row names {column} twice", new RowsErrorDetails(Column: column));
                }

                if (rule is not null && (set?.Keys.Contains(column, StringComparer.OrdinalIgnoreCase) == true
                    || rule.Writable?.Contains(column, StringComparer.OrdinalIgnoreCase) != true))
                {
                    return new RowRefused(StatusCodes.Status403Forbidden, "column_not_writable", $"{column} is not writable", new RowsErrorDetails(Column: column));
                }

                if (update && column == schema.RowId)
                {
                    return new RowRefused(StatusCodes.Status400BadRequest, "invalid_request", "an update never changes the row id", new RowsErrorDetails(Column: column));
                }

                assignments.Add(new Assignment(column, "$v" + assignments.Count, value, Bound: true));
            }

            foreach (var (given, value) in set ?? new Dictionary<string, string>())
            {
                if (schema.Columns.FirstOrDefault(column => string.Equals(column, given, StringComparison.OrdinalIgnoreCase)) is not { } column
                    || !RulesChecks.IsValue(value) || update && value == "request.ulid")
                {
                    return RulesInvalid;
                }

                var name = "$v" + assignments.Count;
                assignments.Add(value.StartsWith("auth.", StringComparison.Ordinal) || value.StartsWith("request.", StringComparison.Ordinal)
                    ? new Assignment(column, name, Reference(value), Bound: true)
                    : new Assignment(column, value, null, Bound: false));
            }

            return assignments;
        }

        private object? Reference(string value) => value switch
        {
            "auth.uid" => auth!.Uid,
            "auth.org_id" => auth!.OrgId,
            "auth.org_role" => auth!.OrgRole,
            "auth.is_anonymous" => auth!.IsAnonymous ? 1L : 0L,
            "request.time" => now,
            "request.ulid" => EntityIds.New().ToString(),
            _ => Scalar(value["auth.claims.".Length..]),
        };

        private object? Scalar(string claim) => auth!.Claims.TryGetValue(claim, out var value)
            ? value switch
            {
                string text => text,
                long integer => integer,
                double real => real,
                IReadOnlyList<RulesScalar> => null,
            }
            : null;

        private string? List(string claim)
        {
            if (!auth!.Claims.TryGetValue(claim, out var value) || value is not IReadOnlyList<RulesScalar> items)
            {
                return null;
            }

            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartArray();
                foreach (var item in items)
                {
                    switch (item)
                    {
                        case string text:
                            writer.WriteStringValue(text);
                            break;
                        case long integer:
                            writer.WriteNumberValue(integer);
                            break;
                        case double real:
                            writer.WriteNumberValue(real);
                            break;
                    }
                }

                writer.WriteEndArray();
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        private void Bind(Dictionary<string, object?> named, CompiledCondition? condition, object?[]? before, IReadOnlyList<string> columns)
        {
            foreach (var parameter in condition?.Parameters ?? [])
            {
                named[parameter.Name] = parameter.Source switch
                {
                    ConditionSource.Uid => auth!.Uid,
                    ConditionSource.OrgId => auth!.OrgId,
                    ConditionSource.OrgRole => auth!.OrgRole,
                    ConditionSource.IsAnonymous => auth!.IsAnonymous ? 1L : 0L,
                    ConditionSource.Claim => Scalar(parameter.Key!),
                    ConditionSource.ClaimList => List(parameter.Key!),
                    ConditionSource.RequestTime => now,
                    ConditionSource.Row => before![IndexOf(columns, parameter.Key!)],
                    _ => throw new UnreachableException(),
                };
            }
        }

        private async ValueTask<IReadOnlyList<object?[]>> QueryAsync(string text, IReadOnlyDictionary<string, object?> named) =>
            (await transaction.ExecuteStatementAsync(new SqlStatement(text, [], named, 2, SqlSession.MaxBytes, timeout), cancellationToken).ConfigureAwait(false)).Rows;

        private static int IndexOf(IReadOnlyList<string> columns, string column)
        {
            for (var index = 0; index < columns.Count; index++)
            {
                if (columns[index] == column)
                {
                    return index;
                }
            }

            throw new UnreachableException();
        }

        private static Dictionary<string, object?> Named(List<Assignment> assignments)
        {
            var named = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var assignment in assignments.Where(item => item.Bound))
            {
                named[assignment.Sql] = assignment.Value;
            }

            return named;
        }

        private static Dictionary<string, object?> Keyed(long key) => new(StringComparer.Ordinal) { ["key"] = key };

        private static Dictionary<string, object?> Post(object?[]? before, RulesTableSchema schema, List<Assignment> assignments)
        {
            var post = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var index = 0; before is not null && index < schema.Columns.Count; index++)
            {
                post[schema.Columns[index]] = before[index];
            }

            foreach (var assignment in assignments)
            {
                if (assignment.Bound)
                {
                    post[assignment.Column] = assignment.Value;
                }
                else
                {
                    post.Remove(assignment.Column);
                }
            }

            return post;
        }

        private static object Compile(RulesTableSchema schema, RulesTable? rule, RulesOperation operation)
        {
            if (rule is null)
            {
                return new Checks(true, null, null, [], schema.Columns, schema.Columns);
            }

            List<string> columns = [];
            foreach (var given in rule.Columns ?? schema.Columns)
            {
                if (schema.Columns.FirstOrDefault(column => string.Equals(column, given, StringComparison.OrdinalIgnoreCase)) is not { } column)
                {
                    return RulesInvalid;
                }

                columns.Add(column);
            }

            CompiledCondition? read = null;
            if (rule.Read is { } readable)
            {
                if (RulesChecks.Compile(schema, readable, RulesOperation.Read) is not CompiledCondition compiled)
                {
                    return RulesInvalid;
                }

                read = compiled;
            }

            if (RulesChecks.Compile(schema, Condition(rule, operation)!, operation) is not CompiledCondition check)
            {
                return RulesInvalid;
            }

            List<CompiledCondition> validate = [];
            var clauses = operation == RulesOperation.Delete ? null : rule.Validate;
            foreach (var clause in clauses ?? [])
            {
                if (RulesChecks.Compile(schema, clause, RulesOperation.Validate) is not CompiledCondition compiled)
                {
                    return RulesInvalid;
                }

                validate.Add(compiled);
            }

            return new Checks(false, read, check, validate, columns, schema.Columns);
        }

        private static string? Condition(RulesTable rule, RulesOperation operation) => operation switch
        {
            RulesOperation.Create => rule.Create,
            RulesOperation.Update => rule.Update,
            RulesOperation.Delete => rule.Delete,
            RulesOperation.Read or RulesOperation.Validate => throw new UnreachableException(),
            _ => throw new UnreachableException(),
        };

        private static RowRefused ConstraintFailed(SqlStatementException error) =>
            new(StatusCodes.Status400BadRequest, "constraint_failed", error.Message);

        private static string Key(long key) => key.ToString(CultureInfo.InvariantCulture);

        private static RowRefused Denied(string operation, string table) =>
            new(StatusCodes.Status403Forbidden, "rule_denied", $"the rules deny {operation} on {table}", new RowsErrorDetails(Operation: operation, Table: table));

        private static readonly RowRefused RowNotFound = new(StatusCodes.Status404NotFound, "row_not_found", "the row does not exist or is not readable");

        private static readonly RowRefused RulesInvalid = new(StatusCodes.Status403Forbidden, "rules_invalid", "the rule this request needs no longer fits the table; deploy the rules again");
    }
}

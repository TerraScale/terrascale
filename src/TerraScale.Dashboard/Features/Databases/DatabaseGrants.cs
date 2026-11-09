using TerraScale.TerraBase.Contracts;

namespace TerraScale.Dashboard.Features.Databases
{
    // The submitted grant fields, kept so a refused save reopens its dialog with them.
    public sealed record DatabaseGrantForm(string Table, string Where, string Columns);

    // Table read grants let end-user tokens read one table's shape; TerraBase owns and validates them.
    public static class DatabaseGrants
    {
        public static async Task<IReadOnlyList<DatabaseReadGrant>> ListAsync(AccountContext context, DatabaseRecord database, IDatabaseBackend backend, DatabaseService databases, CancellationToken cancellationToken)
        {
            return await backend.ReadGrantsAsync(DatabaseService.Scope(context), await databases.HomeAsync(context, database, cancellationToken), database.Id, cancellationToken);
        }

        internal static async Task<IResult> MutateAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, DatabaseService databases,
            IDatabaseBackend backend, string action)
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return Results.Unauthorized();
            }

            var form = await FormRequests.ReadAsync(http, http.RequestAborted);
            if (!Csrf.Validate(http, form))
            {
                return Results.BadRequest("Invalid form token.");
            }

            var id = form["databaseId"].ToString();
            var submitted = new DatabaseGrantForm(form["table"].ToString().Trim(), form["where"].ToString().Trim(), form["columns"].ToString().Trim());
            try
            {
                context = await databases.CurrentAsync(context, http.RequestAborted);
                if (!context.CanManage)
                {
                    throw new AccountAccessException("Organization management access is required.");
                }

                var database = DatabaseService.Database(context, id);
                if (submitted.Table.Length is 0 or > 63 || submitted.Table.Any(char.IsControl))
                {
                    throw new AccountAccessException("Choose a table.", 422);
                }

                var scope = DatabaseService.Scope(context);
                var home = await databases.HomeAsync(context, database, http.RequestAborted);
                if (action == "grants/delete")
                {
                    try { await backend.DeleteReadGrantAsync(scope, home, database.Id, submitted.Table, http.RequestAborted); }
                    catch (TerraBaseNotFoundException error) when (error.ErrorCode == "no_read_grant") { }
                    return DatabaseResults.WantsHtml(http) ? HttpRedirects.SeeOther(DatabaseService.Path(id, "grants")) : Results.NoContent();
                }

                string[] columns = [.. submitted.Columns.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];
                DatabaseReadGrant grant;
                try
                {
                    grant = await backend.PutReadGrantAsync(scope, home, database.Id, submitted.Table,
                        submitted.Where.Length == 0 ? null : submitted.Where, columns.Length == 0 ? null : columns, http.RequestAborted);
                }
                catch (TerraBaseNotFoundException error) when (error.ErrorCode == "unknown_table")
                {
                    throw new AccountAccessException("Table " + submitted.Table + " does not exist.", 422);
                }
                return DatabaseResults.WantsHtml(http) ? HttpRedirects.SeeOther(DatabaseService.Path(id, "grants")) : Results.Json(grant);
            }
            catch (Exception error) when (DatabaseResults.KnownFailure(error))
            {
                if (!DatabaseResults.WantsHtml(http) || DatabaseResults.Status(error) == 404 && error is not TerraBaseException)
                {
                    return DatabaseResults.PageFailure(http, context, error);
                }

                return await DatabaseEndpoints.DetailPageAsync(http, context, id, "grants", backend, databases, DatabaseResults.Message(error), status: DatabaseResults.Status(error),
                    grantForm: action == "grants" ? submitted : null);
            }
        }
    }
}

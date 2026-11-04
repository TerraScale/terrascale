using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.E2E.Demo
{
    // Faults the actual app persistence boundary only in the test executable. Each
    // control is scoped to a freshly authorized owner, never an arbitrary org ID.
    sealed class TestBillingCheckoutPersistence(IDashboardState underlying) : IDashboardState
    {
        private readonly ConcurrentDictionary<string, byte> failWrites = new();
        private readonly ConcurrentDictionary<string, CheckoutBinding> failedWrites = new();
        private readonly ConcurrentDictionary<string, string> corruptReads = new();

        public void MapRoutes(WebApplication app)
        {
            app.MapPost("/__test/billing/checkouts/scenario", async (HttpContext http, DashboardSessions sessions, AccountService accounts, StripeBilling billing) =>
            {
                var context = await AccountRequest.RequireAsync(http, sessions, accounts);
                if (context is null)
                {
                    return Results.Unauthorized();
                }

                var form = await http.Request.ReadFormAsync(http.RequestAborted);
                if (!Csrf.Validate(http, form))
                {
                    return Results.BadRequest();
                }

                if (context.Role != "owner" || context.Organization is null)
                {
                    return Results.StatusCode(403);
                }

                if (form["scenario"] == "missing-index")
                {
                    failWrites[context.Organization.Id] = 0;
                }
                else
                {
                    var binding = await billing.ReadBindingAsync(context, http.RequestAborted);
                    if (binding?.CheckoutId is not { Length: > 0 })
                    {
                        return Results.BadRequest();
                    }

                    var row = await underlying.GetAsync(StripeBilling.CheckoutsTable, binding.CheckoutId, http.RequestAborted);
                    if (row is null || row.Id != binding.CheckoutId || AccountService.Read<CheckoutBinding>(row).OrganizationId != context.Organization.Id)
                    {
                        return Results.BadRequest();
                    }

                    if (form["scenario"] == "wrong-row-id")
                    {
                        corruptReads[binding.CheckoutId] = context.Organization.Id;
                    }
                    else if (form["scenario"] == "normal")
                    {
                        corruptReads.TryRemove(binding.CheckoutId, out _);
                    }
                    else
                    {
                        return Results.BadRequest();
                    }
                }
                return Results.NoContent();
            });
            app.MapGet("/__test/billing/checkouts/probe", async (HttpContext http, DashboardSessions sessions, AccountService accounts, StripeBilling billing) =>
            {
                var context = await AccountRequest.RequireAsync(http, sessions, accounts);
                if (context is null)
                {
                    return Results.Unauthorized();
                }

                if (context.Role != "owner" || context.Organization is null)
                {
                    return Results.StatusCode(403);
                }

                var binding = await billing.ReadBindingAsync(context, http.RequestAborted);
                var index = binding?.CheckoutId is { Length: > 0 } id ? await underlying.GetAsync(StripeBilling.CheckoutsTable, id, http.RequestAborted) : null;
                return Results.Json(new
                {
                    checkoutId = binding?.CheckoutId,
                    originalProjectId = binding?.CheckoutProjectId,
                    hasIndex = index is not null,
                    index = index is null ? null : AccountService.Read<CheckoutBinding>(index),
                    failedIndex = failedWrites.GetValueOrDefault(context.Organization.Id)
                });
            });
        }

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            return underlying.InitializeAsync(cancellationToken);
        }

        public Task CheckReadyAsync(CancellationToken cancellationToken)
        {
            return underlying.CheckReadyAsync(cancellationToken);
        }

        public async Task<StateRecord?> GetAsync(string table, string id, CancellationToken cancellationToken)
        {
            var row = await underlying.GetAsync(table, id, cancellationToken);
            if (table == StripeBilling.CheckoutsTable && row is not null && corruptReads.ContainsKey(id))
            {
                return new("foreign-checkout-row", row.Version, row.Body.Clone());
            }

            return row;
        }
        public Task PutAsync(string table, string id, JsonElement row, StatePrecondition precondition, CancellationToken cancellationToken)
        {
            if (table == StripeBilling.CheckoutsTable && ReferenceEquals(precondition, StatePrecondition.Absent))
            {
                var binding = row.Deserialize<CheckoutBinding>() ?? throw new IOException("Invalid checkout index fixture write.");
                if (binding.CheckoutId != id)
                {
                    throw new IOException("Checkout index fixture identity mismatch.");
                }

                if (failWrites.TryRemove(binding.OrganizationId, out _))
                {
                    failedWrites[binding.OrganizationId] = binding;
                    throw new IOException("Test-only interrupted checkout index persistence.");
                }
            }
            return underlying.PutAsync(table, id, row, precondition, cancellationToken);
        }
        public Task<IReadOnlyList<StateRecord>> ListAsync(string table, CancellationToken cancellationToken)
        {
            return underlying.ListAsync(table, cancellationToken);
        }

        public Task<StatePage> PageAsync(string table, IReadOnlyDictionary<string, string> scope, IReadOnlyList<string>? fields, int limit, string? cursor, CancellationToken cancellationToken)
        {
            return underlying.PageAsync(table, scope, fields, limit, cursor, cancellationToken);
        }

        public Task DeleteAsync(string table, string id, string version, CancellationToken cancellationToken)
        {
            return underlying.DeleteAsync(table, id, version, cancellationToken);
        }
    }
}

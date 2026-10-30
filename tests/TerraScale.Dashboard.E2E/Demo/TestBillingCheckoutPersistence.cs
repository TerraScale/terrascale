using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.TerraBase.Contracts;
using TerraScale.TerraBase.Contracts.Documents;

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

                    var document = await underlying.GetAsync(StripeBilling.CheckoutsCollection, binding.CheckoutId, http.RequestAborted);
                    if (document is null || document.Id != binding.CheckoutId || AccountService.Read<CheckoutBinding>(document).OrganizationId != context.Organization.Id)
                    {
                        return Results.BadRequest();
                    }

                    if (form["scenario"] == "wrong-document-id")
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
                var index = binding?.CheckoutId is { Length: > 0 } id ? await underlying.GetAsync(StripeBilling.CheckoutsCollection, id, http.RequestAborted) : null;
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

        public async Task<TerraBaseDocument?> GetAsync(string collection, string id, CancellationToken cancellationToken)
        {
            var document = await underlying.GetAsync(collection, id, cancellationToken);
            if (collection == StripeBilling.CheckoutsCollection && document is not null && corruptReads.ContainsKey(id))
            {
                return new("foreign-checkout-document", document.Version, "value", document.Document.Clone());
            }

            return document;
        }
        public Task PutAsync(string collection, string id, JsonElement document, TerraBasePrecondition precondition, CancellationToken cancellationToken)
        {
            if (collection == StripeBilling.CheckoutsCollection && ReferenceEquals(precondition, TerraBasePrecondition.Absent))
            {
                var binding = document.Deserialize<CheckoutBinding>() ?? throw new IOException("Invalid checkout index fixture write.");
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
            return underlying.PutAsync(collection, id, document, precondition, cancellationToken);
        }
        Task<TerraBaseDocumentPage> IDashboardState.ReadPageAsync(string collection, JsonElement? filter, int limit, string? cursor, CancellationToken ct)
        {
            return underlying.ReadPageAsync(collection, filter, limit, cursor, ct);
        }

        Task<TerraBaseTransactionResult> IDashboardState.TransactAsync(IReadOnlyList<DashboardStateOperation> operations, CancellationToken ct)
        {
            return underlying.TransactAsync(operations, ct);
        }

        public Task<IReadOnlyList<TerraBaseDocument>> ListAsync(string collection, CancellationToken cancellationToken)
        {
            return underlying.ListAsync(collection, cancellationToken);
        }

        public Task<TerraBaseDocumentPage> QueryPageAsync(string collection, TerraBaseQuery query, CancellationToken cancellationToken)
        {
            return underlying.QueryPageAsync(collection, query, cancellationToken);
        }

        public Task DeleteAsync(string collection, string id, string version, CancellationToken cancellationToken)
        {
            return underlying.DeleteAsync(collection, id, version, cancellationToken);
        }
    }
}

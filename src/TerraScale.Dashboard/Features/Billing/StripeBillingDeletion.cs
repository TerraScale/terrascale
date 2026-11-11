using Stripe;

namespace TerraScale.Dashboard.Features.Billing
{
    public sealed partial class StripeBilling
    {
        // Retained cleanup re-queries canonical provider state: even an unpublished
        // late Checkout remains discoverable through its durably owned customer.
        internal async Task EndCustomersForDeletionAsync(string organizationId, IReadOnlyList<string> retainedCustomers, CancellationToken cancellationToken)
        {
            if (!TerraScaleIds.IsValid(organizationId))
            {
                throw new IOException("Billing deletion scope is invalid.");
            }

            var client = await ClientAsync(cancellationToken);
            var customers = new HashSet<string>(retainedCustomers, StringComparer.Ordinal);
            if (await storage.BindingRowAsync(organizationId, cancellationToken) is { ErasureId: null } row)
            {
                var binding = ReadBinding(row, organizationId);
                if (binding.CustomerId.Length > 0)
                {
                    customers.Add(binding.CustomerId);
                }
            }
            string? page = null;
            do
            {
                var result = await client.V1.Customers.SearchAsync(new()
                {
                    Query = "metadata['organization_id']:'" + organizationId + "'",
                    Limit = 100,
                    Page = page
                }, cancellationToken: cancellationToken);
                foreach (var customer in result.Data)
                {
                    if (customer.Metadata.GetValueOrDefault("organization_id") != organizationId || string.IsNullOrEmpty(customer.Id))
                    {
                        throw new IOException("Billing customer deletion scope changed.");
                    }

                    customers.Add(customer.Id);
                }
                var next = result.HasMore ? result.NextPage : null;
                if (result.HasMore && (string.IsNullOrEmpty(next) || next == page))
                {
                    throw new IOException("Billing customer cursor is invalid.");
                }

                page = next;
            } while (page is not null);
            foreach (var customer in customers)
            {
                await EndCustomerAsync(client, organizationId, customer, cancellationToken);
            }
        }

        internal async Task FenceAsync(string organizationId, string erasureId, Func<BillingBinding, bool> owns, CancellationToken cancellationToken)
        {
            if (!TerraScaleIds.IsValid(erasureId))
            {
                throw new IOException("Erasure identity is invalid.");
            }

            for (var attempt = 0; attempt < 4; attempt++)
            {
                var row = await storage.BindingRowAsync(organizationId, cancellationToken);
                if (row is { ErasureId: not null })
                {
                    return;
                }

                if (row is not null && !owns(row))
                {
                    throw new IOException("Erasure ownership does not match.");
                }

                var fence = new BillingBinding { OrganizationId = organizationId, ErasureId = erasureId, Version = row?.Version ?? "" };
                if (row is null ? await storage.InsertAsync(fence, cancellationToken) : await storage.ReplaceAsync(fence, cancellationToken))
                {
                    return;
                }
            }

            throw new IOException("Billing changed concurrently.");
        }

        internal async Task VerifyFenceAsync(string organizationId, CancellationToken cancellationToken)
        {
            if (await storage.BindingRowAsync(organizationId, cancellationToken) is not { ErasureId: not null })
            {
                throw new IOException("Erasure fence is unavailable.");
            }
        }

        private static async Task EndCustomerAsync(StripeClient client, string organizationId, string customerId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(customerId))
            {
                throw new IOException("Billing customer identity is missing.");
            }

            string? after = null;
            do
            {
                var page = await client.V1.Checkout.Sessions.ListAsync(new() { Customer = customerId, Limit = 100, StartingAfter = after }, cancellationToken: cancellationToken);
                foreach (var item in page.Data)
                {
                    if (item.CustomerId != customerId)
                    {
                        throw new IOException("Checkout customer identity changed.");
                    }

                    if (item.Metadata.GetValueOrDefault("organization_id") != organizationId)
                    {
                        continue;
                    }

                    var checkout = item;
                    if (checkout.Status == "open")
                    {
                        checkout = await client.V1.Checkout.Sessions.ExpireAsync(checkout.Id, new(), Idempotent("delete-checkout-" + checkout.Id), cancellationToken);
                    }

                    if (checkout.Id != item.Id || checkout.CustomerId != customerId || checkout.Metadata.GetValueOrDefault("organization_id") != organizationId)
                    {
                        throw new IOException("Checkout deletion scope changed.");
                    }

                    if (checkout.Status == "complete" && checkout.Mode == "subscription")
                    {
                        if (checkout.SubscriptionId is not { Length: > 0 })
                        {
                            throw new IOException("Completed checkout subscription is unavailable.");
                        }

                        await CancelOwnedSubscriptionAsync(client, organizationId, customerId, checkout.SubscriptionId, cancellationToken);
                    }
                    else if (checkout.Status != "expired" && !(checkout.Status == "complete" && checkout.Mode == "setup"))
                    {
                        throw new IOException("Checkout cancellation is incomplete.");
                    }
                }
                var next = page.HasMore ? page.Data.LastOrDefault()?.Id : null;
                if (page.HasMore && (string.IsNullOrEmpty(next) || next == after))
                {
                    throw new IOException("Checkout cursor is invalid.");
                }

                after = next;
            } while (after is not null);
            after = null;
            do
            {
                var page = await client.V1.Subscriptions.ListAsync(new() { Customer = customerId, Status = "all", Limit = 100, StartingAfter = after }, cancellationToken: cancellationToken);
                foreach (var subscription in page.Data)
                {
                    if (subscription.CustomerId != customerId)
                    {
                        throw new IOException("Subscription customer identity changed.");
                    }

                    if (subscription.Metadata.GetValueOrDefault("organization_id") == organizationId)
                    {
                        await CancelOwnedSubscriptionAsync(client, organizationId, customerId, subscription.Id, cancellationToken);
                    }
                }
                var next = page.HasMore ? page.Data.LastOrDefault()?.Id : null;
                if (page.HasMore && (string.IsNullOrEmpty(next) || next == after))
                {
                    throw new IOException("Subscription cursor is invalid.");
                }

                after = next;
            } while (after is not null);
        }

        private static async Task CancelOwnedSubscriptionAsync(StripeClient client, string organizationId, string customerId, string subscriptionId, CancellationToken cancellationToken)
        {
            var subscription = await client.V1.Subscriptions.GetAsync(subscriptionId, cancellationToken: cancellationToken);
            if (subscription.Id != subscriptionId || subscription.CustomerId != customerId || subscription.Metadata.GetValueOrDefault("organization_id") != organizationId)
            {
                throw new IOException("Subscription ownership does not match.");
            }

            if (subscription.Status is "canceled" or "incomplete_expired")
            {
                return;
            }

            subscription = await client.V1.Subscriptions.CancelAsync(subscription.Id, cancellationToken: cancellationToken);
            if (subscription.Id != subscriptionId || subscription.CustomerId != customerId || subscription.Metadata.GetValueOrDefault("organization_id") != organizationId || subscription.Status is not ("canceled" or "incomplete_expired"))
            {
                throw new IOException("Subscription cancellation is incomplete.");
            }
        }
    }
}

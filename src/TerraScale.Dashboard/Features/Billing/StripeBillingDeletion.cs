using Stripe;
using TerraScale.DotNet;
using AccountService = TerraScale.Dashboard.Common.Accounts.AccountService;

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
            if (await state.GetAsync(Collection, organizationId, cancellationToken) is { } document && !AccountService.IsDeletionFence(document))
            {
                var binding = ReadBinding(document, organizationId);
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

            subscription = await client.V1.Subscriptions.CancelAsync(subscription.Id, new(), Idempotent("delete-organization-" + subscription.Id), cancellationToken);
            if (subscription.Id != subscriptionId || subscription.CustomerId != customerId || subscription.Metadata.GetValueOrDefault("organization_id") != organizationId || subscription.Status is not ("canceled" or "incomplete_expired"))
            {
                throw new IOException("Subscription cancellation is incomplete.");
            }
        }
    }
}

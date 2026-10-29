using System.Globalization;
using AccountService = TerraScale.Dashboard.Shared.Accounts.AccountService;

namespace TerraScale.Dashboard.Features.Billing;

public sealed record BillingCard(string Brand, string LastFour, long ExpiryMonth, long ExpiryYear)
{
    public string Expiry => ExpiryMonth.ToString("D2", CultureInfo.InvariantCulture) + "/" + (ExpiryYear % 100).ToString("D2", CultureInfo.InvariantCulture);
    public string Label => BrandName + " •••• " + LastFour + ", expires " + Expiry;
    private string BrandName => Brand switch
    {
        "amex" => "American Express",
        "diners" => "Diners Club",
        "discover" => "Discover",
        "eftpos_au" => "eftpos",
        "jcb" => "JCB",
        "mastercard" => "Mastercard",
        "unionpay" => "UnionPay",
        "visa" => "Visa",
        _ => "Card"
    };
}

public sealed class BillingPaymentMethodUnavailableException(string message) : IOException(message);

public sealed partial class StripeBilling
{
    public async Task<BillingCard?> PaymentMethodAsync(AccountContext context, CancellationToken cancellationToken)
    {
        context = await MemberAsync(context, cancellationToken);
        if (context.Role != "owner" || context.Organization!.OwnerUserId != context.UserId) return null;
        var binding = await ReadAsync(context.Organization.Id, cancellationToken);
        if (binding?.CustomerId is not { Length: > 0 }) return null;
        var index = await state.GetAsync(CustomersCollection, binding.CustomerId, cancellationToken);
        if (index is null || index.Id != binding.CustomerId || AccountService.Read<CustomerBinding>(index).OrganizationId != context.Organization.Id)
            throw new IOException("Payment method customer ownership does not match.");
        var client = await ClientAsync(cancellationToken);
        // The customer and its subscription are separate Stripe reads; they run together.
        var customerRead = client.V1.Customers.GetAsync(binding.CustomerId,
            new() { Expand = ["invoice_settings.default_payment_method"] }, cancellationToken: cancellationToken);
        var subscriptionRead = binding.SubscriptionId is { Length: > 0 } ? client.V1.Subscriptions.GetAsync(binding.SubscriptionId,
            new() { Expand = ["default_payment_method"] }, cancellationToken: cancellationToken) : null;
        var customer = await customerRead;
        if (customer.Id != binding.CustomerId || customer.Deleted == true || customer.Metadata?.GetValueOrDefault("organization_id") != context.Organization.Id)
            throw new BillingPaymentMethodUnavailableException("Payment method customer ownership does not match.");
        var method = customer.InvoiceSettings?.DefaultPaymentMethod;
        if (subscriptionRead is not null)
        {
            var subscription = await subscriptionRead;
            if (subscription.Id != binding.SubscriptionId || subscription.CustomerId != binding.CustomerId ||
                subscription.Metadata?.GetValueOrDefault("organization_id") != context.Organization.Id)
                throw new BillingPaymentMethodUnavailableException("Payment method subscription ownership does not match.");
            method = subscription.DefaultPaymentMethod ?? method;
        }
        if (method is null) return null;
        if (method.CustomerId != binding.CustomerId) throw new BillingPaymentMethodUnavailableException("Payment method belongs to a different customer.");
        if (method.Type != "card" || method.Card is not { } card) return null;
        if (card.Last4 is not { Length: 4 } || card.Last4.Any(character => !char.IsAsciiDigit(character)) ||
            card.ExpMonth is < 1 or > 12 || card.ExpYear is < 2000 or > 9999)
            throw new BillingPaymentMethodUnavailableException("Payment method card facts are invalid.");
        return new(card.Brand, card.Last4, card.ExpMonth, card.ExpYear);
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;
using TerraScale.Dashboard.Errors;
using TerraScale.Dashboard.Features.Billing.Invoices;

namespace TerraScale.Dashboard.Features.Billing;

internal readonly union RefundCancellation(bool, OrganizationRefundPending);
internal readonly union ParsedArguments(Arguments, CommandRejection);
internal sealed record OrganizationRefundPending(string Status);
internal readonly record struct CommandRejection(string Message);
internal sealed record Arguments(string Command, string OrganizationId, string AdminEmail, string BasePlanKey,
    long SpendCapCents, long WriteCentsPerMillion, long StorageCentsPerGBMonth);
public readonly union PortalSession(PortalSessionUrl, Validation);
public sealed record PortalSessionUrl(string Url);
public readonly union PaymentMethod(BillingCard, NoCard, Unavailable);
public readonly union WebhookReceipt(Done, Rejected, Deferred);
public sealed record NoCard;
public sealed record Rejected;
public sealed record Deferred(string EventId, string EventType, Exception Cause);
public sealed record InternalBillReported(InternalBill Bill, Unavailable? Failure);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(BillingInvoiceList))]
internal sealed partial class BillingMcpJson : JsonSerializerContext;

using System.Text.Json.Serialization;

namespace TerraScale.Authentication.Features.Impersonation;

public sealed record StartImpersonationRequest(string Reason, [property: JsonPropertyName("client_id")] string ClientId);

public sealed record ImpersonationTicket(string Id, string Ticket, DateTimeOffset ExpiresAt)
{
    public override string ToString() => nameof(ImpersonationTicket);
}

public sealed record ImpersonationBody(string Id, string UserId, string ActorId, string ActorEmail, string ClientId, string Reason, DateTimeOffset ExpiresAt);

public sealed record ImpersonationPage(IReadOnlyList<ImpersonationBody> Items);

public sealed record ImpersonationActor([property: JsonPropertyName("sub")] string Subject);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(StartImpersonationRequest))]
[JsonSerializable(typeof(ImpersonationTicket))]
[JsonSerializable(typeof(ImpersonationPage))]
[JsonSerializable(typeof(ImpersonationActor))]
internal sealed partial class ImpersonationJson : JsonSerializerContext;

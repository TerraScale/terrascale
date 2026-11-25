using System.Text.Json;
using TerraBase.Configuration;
using TerraBase.Hosting;
using TerraScale.Contracts;
using TerraScale.Contracts.ControlPlane;
using TerraScale.Endpoints;
using BindingUnsupported = TerraScale.Endpoints.Binding.UnsupportedMediaType;
using BindingValidation = TerraScale.Endpoints.Binding.Validation;
using BoundSnapshot = TerraScale.Endpoints.Binding.Bound<TerraBase.Features.ControlPlane.SignedSnapshot>;

namespace TerraBase.Features.ControlPlane;

internal readonly union ControlSnapshotResponse(ControlSnapshotAck, InvalidInput, CredentialRejected, StateConflict, BackendUnavailable);

public sealed record SignedSnapshot(ControlSnapshot Snapshot, ReadOnlyMemory<byte> Body);

internal sealed class ControlSnapshotEndpoint(ControlSnapshots snapshots, ControlSnapshotSync sync, TerraBaseOptions options, TimeProvider time)
    : Endpoint<SignedSnapshot, ControlSnapshotResponse>
{
    private const int MaxBodyBytes = 8 * 1024 * 1024;

    public override void Configure()
    {
        Post("/internal/v2/control/snapshot");
        Name("applyControlSnapshot");
        Summary("Applies a signed Control Plane snapshot");
        Description("The signature is ES256 (IEEE P1363, base64url) over the exact request body with the top-level signature value replaced by an empty string, "
            + "verified with TERRABASE_CONTROL_KEY_FILE. Older versions answer 409 stale_snapshot.");
        Tags("Internal");
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
    }

    protected override async ValueTask<BoundSnapshot> BindAsync(CancellationToken cancellationToken)
    {
        if (!string.Equals(HttpContext.Request.ContentType?.Split(';')[0].Trim(), "application/json", StringComparison.OrdinalIgnoreCase))
        {
            return new BindingUnsupported("Content-Type must be application/json");
        }

        using var buffer = new MemoryStream();
        await HttpContext.Request.Body.CopyToAsync(buffer, cancellationToken);
        if (buffer.Length > MaxBodyBytes)
        {
            return new BindingValidation("the control snapshot is at most 8 MiB");
        }

        var body = buffer.ToArray();
        return Parse(body) is { } snapshot ? new SignedSnapshot(snapshot, body) : new BindingValidation("the body is not a control snapshot");
    }

    public override async ValueTask<ControlSnapshotResponse> HandleAsync(
        SignedSnapshot request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await sync.Serialized.WaitAsync(cancellationToken);
        try
        {
            return await ApplyAsync(request, cancellationToken);
        }
        finally
        {
            _ = sync.Serialized.Release();
        }
    }

    private async ValueTask<ControlSnapshotResponse> ApplyAsync(SignedSnapshot request, CancellationToken cancellationToken)
    {
        return snapshots.Accept(request.Snapshot, request.Body) switch
        {
            AcceptedSnapshot { Changed: false } accepted => Ack(accepted.Snapshot),
            AcceptedSnapshot accepted => await sync.ApplyAsync(accepted.Snapshot, cancellationToken) switch
            {
                Completed => snapshots.Commit(accepted.Snapshot) switch
                {
                    Completed => Ack(accepted.Snapshot),
                    StateConflict conflict => conflict,
                },
                SnapshotKeyRefused refused => new StateConflict(refused.Message, refused.Code),
                BackendUnavailable unavailable => unavailable,
            },
            InvalidInput invalid => invalid,
            CredentialRejected unauthorized => unauthorized,
            StateConflict conflict => conflict,
            BackendUnavailable unavailable => unavailable,
        };
    }

    private static ControlSnapshot? Parse(byte[] body)
    {
        try
        {
            return JsonSerializer.Deserialize(body, TerraScaleContractsJsonContext.Default.ControlSnapshot);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private ControlSnapshotAck Ack(ControlSnapshot snapshot) => new(snapshot.Version, options.MachineId, options.Region, time.GetUtcNow());
}

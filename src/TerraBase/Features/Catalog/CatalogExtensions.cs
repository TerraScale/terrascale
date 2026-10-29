using TerraBase.Hosting;

namespace TerraBase.Features.Catalog;

public static class CatalogExtensions
{
    public static IServiceCollection AddTerraBaseCatalog(this IServiceCollection services)
    {
        services.AddSingleton<CatalogGate>();
        services.AddSingleton<ICatalogGate>(provider => provider.GetRequiredService<CatalogGate>());
        services.AddSingleton(provider => new Lazy<ICatalogGate>(provider.GetRequiredService<ICatalogGate>));
        services.Insert(0, ServiceDescriptor.Singleton<IHostedService>(provider => provider.GetRequiredService<CatalogGate>()));
        return services;
    }

    public static WebApplication UseTerraBaseCatalogGate(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var gate = app.Services.GetRequiredService<ICatalogGate>();
        app.Use(async (context, next) =>
        {
            if (!gate.IsOpen && context.GetEndpoint() is { } endpoint
                && (endpoint.Metadata.GetMetadata<AnchoredRevocation>() is not null
                    || endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAuthorizeData>() is not null
                    && endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>() is null))
            {
                context.Response.Headers.CacheControl = "no-store";
                await BadRequests.WriteAsync(context, StatusCodes.Status503ServiceUnavailable, "catalog_unverified",
                    "this machine's catalog restore anchor has not been verified", op: null).ConfigureAwait(false);
                return;
            }

            if (!gate.IsOpen || context.GetEndpoint()?.Metadata.GetMetadata<AnchoredRevocation>() is null)
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            var body = context.Response.Body;
            using var held = new RevocationResponse();
            context.Response.Body = held;
            try
            {
                await next(context).ConfigureAwait(false);
            }
            finally
            {
                context.Response.Body = body;
            }

            if (context.Response.StatusCode < StatusCodes.Status400BadRequest
                && !await gate.TryAnchorAsync(context.RequestAborted).ConfigureAwait(false))
            {
                context.Response.Clear();
                await BadRequests.WriteAsync(context, StatusCodes.Status503ServiceUnavailable, CatalogGate.AnchorUnavailable.Code,
                    CatalogGate.AnchorUnavailable.Message, op: null).ConfigureAwait(false);
                return;
            }

            held.Position = 0;
            await held.CopyToAsync(body, context.RequestAborted).ConfigureAwait(false);
        });
        return app;
    }

    private sealed class RevocationResponse : MemoryStream
    {
        private const int MaxBytes = 128 * 1024;

        public override void Write(byte[] buffer, int offset, int count)
        {
            Check(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Check(buffer.Length);
            base.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            Check(1);
            base.WriteByte(value);
        }

        public override void SetLength(long value)
        {
            if (value > MaxBytes)
            {
                throw new IOException("the revocation response exceeds its byte limit");
            }

            base.SetLength(value);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        private void Check(int bytes)
        {
            if (Position + bytes > MaxBytes)
            {
                throw new IOException("the revocation response exceeds its byte limit");
            }
        }
    }
}

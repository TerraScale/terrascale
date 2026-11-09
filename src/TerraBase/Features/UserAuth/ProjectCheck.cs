using TerraBase.Features.Management;
using TerraBase.Errors;
using TerraBase.Hosting;


namespace TerraBase.Features.UserAuth;

internal static class ProjectCheck
{
    private const int Serving = 0;
    private const int Started = 1;
    private const int CutOff = 2;
    private const int Refusing = 3;

    internal static async Task WatchKeyAsync(HttpContext context, Func<Task> next)
    {
        if (UserAuthExtensions.Identity(context.User) is null || context.Features.Get<ProjectToken>()?.Key is not { } key)
        {
            await next().ConfigureAwait(false);
            return;
        }

        var authority = context.RequestServices.GetRequiredService<IKeyAuthority>();
        var answer = (await authority.AdmitAsync(key, context.RequestAborted).ConfigureAwait(false)).Value;
        if (answer is not KeyActive active)
        {
            await SocketAdmission.RefuseKeyAsync(context, answer as KeyRefused).ConfigureAwait(false);
            return;
        }

        var aborted = context.RequestAborted;
        using var refusal = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        var state = Serving;
        context.Response.OnStarting(() =>
        {
            if (refusal.IsCancellationRequested && !aborted.IsCancellationRequested)
            {
                Interlocked.CompareExchange(ref state, CutOff, Serving);
            }

            if (Interlocked.CompareExchange(ref state, Started, Serving) == CutOff)
            {
                context.Abort();
            }

            return Task.CompletedTask;
        });
        using var cut = refusal.Token.Register(() =>
        {
            if (!aborted.IsCancellationRequested && Interlocked.CompareExchange(ref state, CutOff, Started) == Started)
            {
                context.Abort();
            }
        });
        using var watch = KeyWatch.Start(authority, watched: true, active.Until, _ => ValueTask.FromResult<KeyToken?>(key), refusal, context.RequestServices.GetRequiredService<TimeProvider>());
        context.RequestAborted = refusal.Token;
        try
        {
            await next().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (refusal.IsCancellationRequested && !aborted.IsCancellationRequested && !context.Response.HasStarted)
        {
            Interlocked.CompareExchange(ref state, CutOff, Serving);
        }
        finally
        {
            context.RequestAborted = aborted;
        }

        if (refusal.IsCancellationRequested && !aborted.IsCancellationRequested)
        {
            Interlocked.CompareExchange(ref state, CutOff, Serving);
        }

        if (Interlocked.CompareExchange(ref state, Refusing, CutOff) == CutOff && !context.Response.HasStarted)
        {
            context.Response.Clear();
            await SocketAdmission.RefuseKeyAsync(context, watch!.Refusal).ConfigureAwait(false);
        }
    }

    internal static async Task GateClaimsAsync(HttpContext context, Func<Task> next)
    {
        if (UserAuthExtensions.Identity(context.User) is not null
            && context.Features.Get<ProjectToken>() is { Allowlists.Count: > 0 } restricted
            && IpAllowlists.ClientOf(context) is var client && !IpAllowlists.Admits(restricted.Allowlists, client))
        {
            await BadRequests.WriteAsync(context, StatusCodes.Status403Forbidden, "ip_not_allowed", IpAllowlists.Refusal(client), op: null).ConfigureAwait(false);
            return;
        }

        if (UserAuthExtensions.Identity(context.User)?.HasClaim("token_kind", "end_user") == true
            && context.GetEndpoint()?.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName is not
                ("getShape" or "getShapeKey" or "getShapeManifest" or "connectShapeHints"
                or "createFileUpload" or "completeFileUpload" or "listBucketFiles" or "createFileDownload" or "deleteBucketFile"))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (UserAuthExtensions.Identity(context.User)?.FindFirst("project_id") is not null
            && (context.Request.Path.StartsWithSegments(ManagementRoute.Prefix, StringComparison.OrdinalIgnoreCase)
                || context.GetEndpoint()?.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == "legacyCreateDatabase"))
        {
            await BadRequests.WriteAsync(context, 404, "unknown_database", "the database does not exist", op: null).ConfigureAwait(false);
            return;
        }

        if (context.GetEndpoint() is not null
            && context.Request.RouteValues["project"] is string project
            && !context.Request.RouteValues.ContainsKey("organization") && context.User.Identity?.IsAuthenticated == true)
        {
            var jwt = UserAuthExtensions.Identity(context.User);
            if (jwt is not null)
            {
                if (jwt.FindFirst("project_id") is null)
                {
                    await BadRequests.WriteAsync(context, StatusCodes.Status401Unauthorized, "invalid_key",
                        "this token does not authorize project routes; use an API key or its project token", op: null).ConfigureAwait(false);
                    return;
                }

                if (jwt.FindFirst("project_id")?.Value != project)
                {
                    await BadRequests.WriteAsync(context, 404, "unknown_database", "the database does not exist", op: null).ConfigureAwait(false);
                    return;
                }
            }
            else if (context.GetEndpoint()?.Metadata.GetMetadata<ProjectManagementRoute>() is not null)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
        }

        await next().ConfigureAwait(false);
    }
}

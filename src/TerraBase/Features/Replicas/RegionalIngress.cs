using TerraBase.Hosting;

namespace TerraBase.Features.Replicas;

/// <summary>Counts customer and catalog requests, including direct authenticated service traffic outside the edge.</summary>
internal sealed class RegionalIngress(InternalCredential credential) : IDisposable
{
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _slots = new(1024, 1024);
    private readonly HttpClient _http = PeerHttp.Create(credential);
    private TaskCompletionSource _open = Completed();
    private TaskCompletionSource _empty = Completed();
    private int _active;
    private bool _held;

    internal async Task<IDisposable> EnterAsync(CancellationToken token)
    {
        await _slots.WaitAsync(token).ConfigureAwait(false);
        try
        {
            while (true)
            {
                Task opened;
                lock (_gate)
                {
                    if (!_held)
                    {
                        if (_active++ == 0)
                        {
                            _empty = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        }
                        return new Lease(this, drain: false);
                    }
                    opened = _open.Task;
                }
                await opened.WaitAsync(token).ConfigureAwait(false);
            }
        }
        catch
        {
            _slots.Release();
            throw;
        }
    }

    internal async Task<IDisposable> HoldAsync(CancellationToken token)
    {
        Task emptied;
        lock (_gate)
        {
            _held = true;
            _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
            emptied = _empty.Task;
        }
        var lease = new Lease(this, drain: true);
        try
        {
            await emptied.WaitAsync(token).ConfigureAwait(false);
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    internal async Task ForwardServiceAsync(HttpContext context, Uri address)
    {
        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method),
            new Uri(address, context.Request.Path.ToUriComponent() + context.Request.QueryString.ToUriComponent()));
        if (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            request.Content = new StreamContent(context.Request.Body);
            if (context.Request.ContentType is { } contentType)
            {
                request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            }
        }
        foreach (var (name, values) in context.Request.Headers)
        {
            if (name is not ("Host" or "Connection" or "Content-Length" or "Transfer-Encoding" or "Content-Type")
                && !name.Equals(InternalCredential.HeaderName, StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.TryAddWithoutValidation(name, (IEnumerable<string?>)values);
            }
        }
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted).ConfigureAwait(false);
        context.Response.StatusCode = (int)response.StatusCode;
        foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
        {
            if (name is not ("Connection" or "Transfer-Encoding"))
            {
                context.Response.Headers[name] = values.ToArray();
            }
        }
        await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
    }

    private void Release(bool drain)
    {
        lock (_gate)
        {
            if (drain)
            {
                _held = false;
                _open.TrySetResult();
            }
            else if (--_active == 0)
            {
                _empty.TrySetResult();
            }
        }
        if (!drain)
        {
            _slots.Release();
        }
    }

    private static TaskCompletionSource Completed()
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        result.SetResult();
        return result;
    }

    private sealed class Lease(RegionalIngress ingress, bool drain) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                ingress.Release(drain);
            }
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _slots.Dispose();
    }
}

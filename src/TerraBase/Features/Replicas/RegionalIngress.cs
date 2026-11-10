using TerraBase.Features.Regions;
using TerraBase.Hosting;
using Microsoft.AspNetCore.Http.Features;

namespace TerraBase.Features.Replicas;

internal sealed class RegionalIngress(InternalCredential credential) : IDisposable
{
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _slots = new(1024, 1024);
    private readonly HttpClient _http = PeerHttp.Create(credential);
    private TaskCompletionSource _open = Completed();
    private TaskCompletionSource _empty = Completed();
    private TaskCompletionSource _idle = Completed();
    private int _active;
    private int _outstanding;
    private bool _held;

    internal async Task<IDisposable> EnterAsync(CancellationToken token)
    {
        if (!await _slots.WaitAsync(0, token).ConfigureAwait(false))
        {
            throw new Qos.QosMemoryException("regional ingress is full");
        }
        lock (_gate)
        {
            if (_outstanding++ == 0)
            {
                _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
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
            ReleaseOutstanding();
            throw;
        }
    }

    internal Task WaitIdleAsync(CancellationToken token)
    {
        lock (_gate)
        {
            return _idle.Task.WaitAsync(token);
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

    internal async Task ForwardServiceAsync(HttpContext context, HomeNode writer)
    {
        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method),
            new Uri(writer.Address, context.Request.Path.ToUriComponent() + context.Request.QueryString.ToUriComponent()));
        if (context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true)
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
        HttpResponseMessage answered;
        try
        {
            answered = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception error) when (HomeForwarding.Unanswered(context, error))
        {
            await HomeForwarding.UnansweredAsync(context, writer.Region, error).ConfigureAwait(false);
            return;
        }

        using var response = answered;
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
            ReleaseOutstanding();
        }
    }

    private void ReleaseOutstanding()
    {
        lock (_gate)
        {
            if (--_outstanding == 0)
            {
                _idle.TrySetResult();
            }
        }
        _slots.Release();
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

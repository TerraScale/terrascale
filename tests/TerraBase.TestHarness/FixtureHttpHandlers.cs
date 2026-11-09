using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Http;

namespace TerraBase.Testing;

internal sealed class FixtureHttpHandlers : IHttpMessageHandlerBuilderFilter, IDisposable
{
    private readonly Lock _gate = new();
    private readonly HashSet<HttpMessageHandler> _handlers = [with(ReferenceEqualityComparer.Instance)];
    private bool _disposed;

    public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return builder =>
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                try
                {
                    next(builder);
                    var pipeline = builder.Build();
                    _handlers.Add(pipeline);
                    builder.AdditionalHandlers.Clear();
                    builder.PrimaryHandler = pipeline;
                }
                catch
                {
                    var handlers = new HashSet<HttpMessageHandler>(builder.AdditionalHandlers, ReferenceEqualityComparer.Instance)
                    {
                        builder.PrimaryHandler,
                    };
                    foreach (var handler in handlers.OfType<DelegatingHandler>().ToArray())
                    {
                        if (handler.InnerHandler is { } inner)
                        {
                            handlers.Remove(inner);
                        }
                    }

                    _handlers.UnionWith(handlers);
                    throw;
                }
            }
        };
    }

    public void Dispose()
    {
        HttpMessageHandler[] handlers;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            handlers = [.. _handlers];
            _handlers.Clear();
        }

        var errors = new List<Exception>();
        foreach (var handler in handlers)
        {
            try
            {
                handler.Dispose();
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
        }

        if (errors.Count == 1)
        {
            ExceptionDispatchInfo.Capture(errors[0]).Throw();
        }
        else if (errors.Count > 1)
        {
            throw new AggregateException(errors);
        }
    }
}

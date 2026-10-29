using System.Runtime.ExceptionServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TerraScale.Dashboard.E2E
{
    // One process observer routes diagnostics through the current request's fixture-owned service.
    internal sealed class TestInitializerDiagnostics : IDisposable
    {
        private static readonly HttpContextAccessor Current = new();
        private ILogger? logger;
        private int remaining = 4;

        static TestInitializerDiagnostics()
        {
            AppDomain.CurrentDomain.FirstChanceException += OnThrow;
        }

        public void Attach(WebApplication app)
        {
            logger = app.Logger;
        }

        private static void OnThrow(object? sender, FirstChanceExceptionEventArgs args)
        {
            if (args.Exception is not TypeInitializationException initializer || Current.HttpContext is not { } http)
            {
                return;
            }

            try
            {
                var owner = http.RequestServices.GetService<TestInitializerDiagnostics>();
                if (owner?.logger is not { } logger || Interlocked.Decrement(ref owner.remaining) < 0)
                {
                    return;
                }
                var root = initializer.GetBaseException();
                logger.LogError("Test initializer {InitializerType}: root {RootType}; stack {RootStack}",
                    Bound(initializer.TypeName, 256), Bound(root.GetType().FullName, 256), Bound(root.StackTrace, 2048));
            }
            catch (Exception) { /* Diagnostics must never alter the request outcome. */ }
        }

        public void Dispose()
        {
            logger = null;
        }

        private static string Bound(string? value, int limit)
        {
            return value is null ? "" : value[..Math.Min(value.Length, limit)];
        }
    }
}

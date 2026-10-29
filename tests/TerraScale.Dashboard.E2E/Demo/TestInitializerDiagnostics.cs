using System.Runtime.ExceptionServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TerraScale.Dashboard.E2E
{
    // Observe only this test host's requests; never log exception messages or request/config values.
    internal sealed class TestInitializerDiagnostics : IDisposable
    {
        private EventHandler<FirstChanceExceptionEventArgs>? handler;

        public void Attach(WebApplication app)
        {
            var marker = new object();
            var remaining = 4;
            var current = app.Services.GetRequiredService<IHttpContextAccessor>();
            app.Use((context, next) => { context.Items[marker] = true; return next(context); });
            void OnThrow(object? sender, FirstChanceExceptionEventArgs args)
            {
                if (args.Exception is not TypeInitializationException initializer || current.HttpContext is not { } http ||
                    !http.Items.ContainsKey(marker) || Interlocked.Decrement(ref remaining) < 0)
                {
                    return;
                }

                try
                {
                    var root = initializer.GetBaseException();
                    app.Logger.LogError("Test initializer {InitializerType}: root {RootType}; stack {RootStack}",
                        Bound(initializer.TypeName, 256), Bound(root.GetType().FullName, 256), Bound(root.StackTrace, 2048));
                }
                catch (Exception) { /* Diagnostics must never alter the request outcome. */ }
            }
            handler = OnThrow;
            AppDomain.CurrentDomain.FirstChanceException += handler;
        }

        public void Dispose()
        {
            AppDomain.CurrentDomain.FirstChanceException -= handler;
        }

        private static string Bound(string? value, int limit)
        {
            return value is null ? "" : value[..Math.Min(value.Length, limit)];
        }
    }
}

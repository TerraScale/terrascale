using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine.Turso;
using TerraBase.Fast.Documents;
using TerraBase.Features.Memory;
using TerraBase.Features.Qos;
using TerraBase.Tenancy;

namespace TerraBase.Fast.Storage;

public sealed class QosExecutionContextTests
{
    [Test]
    public async Task Native_reads_and_grouped_patches_borrow_only_their_request_resource_context()
    {
        await using var api = await DocumentApi.StartAsync(services: services =>
        {
            var registration = services.Single(service => service.ServiceType == typeof(IMemoryBudget));
            services.Remove(registration);
            services.AddSingleton(provider => new ContextProbe((IMemoryBudget)registration.ImplementationFactory!(provider)));
            services.AddSingleton<IMemoryBudget>(provider => provider.GetRequiredService<ContextProbe>());
            services.AddSingleton<IStartupFilter, RequestMarker>();
            var engine = services.Single(service => service.ServiceType == typeof(TursoStorageEngine));
            services.Remove(engine);
            services.AddSingleton(provider =>
            {
                var startupProbe = provider.GetRequiredService<ContextProbe>();
                var previous = startupProbe.Marker.Value;
                startupProbe.Marker.Value = new RequestState();
                try
                {
                    return (TursoStorageEngine)engine.ImplementationFactory!(provider);
                }
                finally
                {
                    startupProbe.Marker.Value = previous;
                }
            });
        });
        var probe = api.Server.Services.GetRequiredService<ContextProbe>();
        probe.Io = api.Server.Services.GetRequiredService<TursoStorageEngine>().Io;
        var id = DocumentApi.NewId();
        await Assert.That((await api.PutAsync(id, new { n = 0 })).Status).IsEqualTo(HttpStatusCode.Created);
        api.Client.DefaultRequestHeaders.Add("X-Test-Resource-Context", "true");

        var page = await api.QueryAsync(new { limit = 1 });
        await Assert.That(page.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(page.Ids).Contains(id);
        for (var n = 1; n <= 2; n++)
        {
            var patched = await api.SendRawAsync(HttpMethod.Patch, api.Document(id), $$"""{"n":{{n}}}""", "application/merge-patch+json");
            await Assert.That(patched.Status).IsEqualTo(HttpStatusCode.OK);
        }

        var observations = probe.Observations.ToArray();
        await Assert.That(observations.Any(observation => observation.Thread == "terrabase-turso")).IsTrue();
        await Assert.That(observations.Count(observation => observation.Thread == "terrabase-turso-writer")).IsGreaterThanOrEqualTo(2);
        await Assert.That(observations.Select(observation => observation.Context.Results).Distinct().Count()).IsEqualTo(3);
        foreach (var observation in observations)
        {
            await Assert.That(observation.Marker).IsNull();
            await Assert.That(observation.Context.Tenant?.OrganizationId).IsEqualTo(api.Tenant.OrganizationId);
            await Assert.That(observation.Context.Tenant?.Plan).IsEqualTo(PlanTier.Hobby);
            await Assert.That(observation.Context.Results).IsSameReferenceAs(observation.Request.Owner);
            await Assert.That(observation.Admitted).IsEqualTo(observation.Thread == "terrabase-turso-writer");
        }
    }

    private sealed class RequestState
    {
        internal QosContext.ResultMemory? Owner { get; set; }
    }

    private sealed record Observation(string? Thread, RequestState? Marker, QosContext.Snapshot Context, RequestState Request, bool Admitted);

    private sealed class ContextProbe(IMemoryBudget inner) : IMemoryBudget
    {
        private readonly ConcurrentDictionary<QosContext.ResultMemory, RequestState> _requests = new();
        internal AsyncLocal<RequestState?> Marker { get; } = new();
        internal ConcurrentQueue<Observation> Observations { get; } = new();
        internal TursoIoPool? Io { get; set; }

        public IMemoryReservation? TryReserve(MemoryLane lane, long bytes) => TryReserve(lane, bytes, QosContext.Current);

        public IMemoryReservation? TryReserve(MemoryLane lane, long bytes, TenantContext? tenant)
        {
            var context = QosContext.Capture();
            if (context.Results is { } owner)
            {
                if (Marker.Value is { } request)
                {
                    request.Owner = owner;
                    _requests.TryAdd(owner, request);
                }

                if (tenant is not null && Thread.CurrentThread.Name is "terrabase-turso" or "terrabase-turso-writer"
                    && _requests.TryGetValue(owner, out var source))
                {
                    Observations.Enqueue(new(Thread.CurrentThread.Name, Marker.Value, context, source, Io!.HasAdmission));
                }
            }

            return inner.TryReserve(lane, bytes, tenant);
        }
    }

    private sealed class RequestMarker(ContextProbe probe) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, invoke) =>
            {
                var previous = probe.Marker.Value;
                if (context.Request.Headers.ContainsKey("X-Test-Resource-Context"))
                {
                    probe.Marker.Value = new RequestState();
                }

                try
                {
                    await invoke(context);
                }
                finally
                {
                    probe.Marker.Value = previous;
                }
            });
            next(app);
        };
    }
}

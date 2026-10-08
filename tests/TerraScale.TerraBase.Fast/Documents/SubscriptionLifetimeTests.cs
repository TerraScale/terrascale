using System.Diagnostics.Metrics;
using System.Net;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Management.Usage;
using TerraBase.Features.Memory;
using TerraBase.Hosting;
using TerraBase.Testing;

namespace TerraBase.Fast.Documents;

public sealed class SubscriptionLifetimeTests
{
    [Test]
    public async Task An_idle_subscription_ends_at_signed_expiration_and_returns_memory()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var api = await ManagementApi.StartAsync(time: clock);
        var database = await api.CreateManagedDatabaseAsync(new Faker().Random.AlphaNumeric(8).ToLowerInvariant().Insert(0, "db"));
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        using var data = await api.CreateClientAsync(database.Organization, ApiKeyScopes.DocumentsWrite);
        await ManagementApi.CreateCollectionAsync(data, database, "items");
        clock.SetUtcNow(DateTimeOffset.UtcNow);
        await api.Server.Services.GetRequiredService<UsageMeter>().FlushAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var meter = api.Server.Services.GetRequiredService<IMeterFactory>().Create(Telemetry.MeterName);
        long reserved = 0;
        var liveEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, observer) =>
        {
            if (ReferenceEquals(instrument.Meter, meter) && instrument.Name is "terrabase.memory.reserved" or "terrabase.shape.live.waits")
            {
                observer.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            if (instrument.Name == "terrabase.memory.reserved")
            {
                reserved = value;
            }
            else if (value == 1)
            {
                liveEntered.TrySetResult();
            }
        });
        listener.Start();
        listener.RecordObservableInstruments();
        var baseline = reserved;
        var token = api.Issuer.Mint(api.User, descriptor =>
        {
            descriptor.Claims["project_id"] = database.Project;
            descriptor.Claims["scope"] = ApiKeyScopes.ShapesRead;
            descriptor.Expires = clock.GetUtcNow().AddSeconds(5).UtcDateTime;
        });
        using var client = api.WithToken(token);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var response = await client.GetAsync(Route(database), HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(deadline.Token) is { Length: > 0 })
        {
        }

        await liveEntered.Task.WaitAsync(deadline.Token);
        listener.RecordObservableInstruments();
        await Assert.That(reserved).IsGreaterThan(baseline);
        clock.Advance(TimeSpan.FromSeconds(6));
        while (await reader.ReadLineAsync(deadline.Token) is not null)
        {
        }

        listener.RecordObservableInstruments();
        await Assert.That(reserved).IsEqualTo(baseline);
    }

    [Test]
    public async Task Memory_admission_refuses_before_headers_and_a_retry_works_after_release()
    {
        await using var api = await ManagementApi.StartAsync(settings => settings["memory-budget"] = "67108864");
        var database = await api.CreateManagedDatabaseAsync();
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        using var data = await api.CreateClientAsync(database.Organization, ApiKeyScopes.DocumentsWrite);
        await ManagementApi.CreateCollectionAsync(data, database, "items");
        using var client = api.WithToken(api.Issuer.Mint(api.User, token =>
        {
            token.Claims["project_id"] = database.Project;
            token.Claims["scope"] = ApiKeyScopes.ShapesRead;
        }));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var budget = api.Server.Services.GetRequiredService<IMemoryBudget>();
        using (var held = budget.TryReserve(MemoryLane.Foreground, 40 * 1024 * 1024))
        {
            await Assert.That(held).IsNotNull();
            using var refused = await client.GetAsync(Route(database), deadline.Token);
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
            await Assert.That(await refused.Content.ReadAsStringAsync(deadline.Token)).Contains("subscription_busy");
        }

        using var admitted = await client.GetAsync(Route(database), HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        await Assert.That(admitted.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(admitted.Content.Headers.ContentType!.MediaType).IsEqualTo("text/event-stream");
    }

    private static Uri Route(ManagedDatabaseRef database) =>
        new($"/projects/{database.Project}/databases/{database.Name}/collections/items/subscribe", UriKind.Relative);
}

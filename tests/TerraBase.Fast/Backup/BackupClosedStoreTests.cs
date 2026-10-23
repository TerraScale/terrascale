using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Features.Backup;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Backup;

public sealed class BackupClosedStoreTests
{
    [Test]
    public async Task A_store_closed_under_a_read_is_skipped_quietly_and_its_database_is_reopened_by_the_next_discovery(CancellationToken cancellationToken)
    {
        var s3 = new FakeS3();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        var logs = new BackupLogs();
        await using var api = await ManagementApi.StartAsync(s3.Configure, services =>
        {
            s3.Route(services);
            ManualBackupHosting.Configure(services);
            services.AddSingleton<IWalBackup>(provider => new ClosingBackup(provider.GetRequiredService<TursoStorageEngine>()));
            TerraBaseTestServer.CaptureDebugLogs(services, logs);
        }, time);
        var engine = (ClosingBackup)api.Server.Services.GetRequiredService<IWalBackup>();
        var database = await api.CreateManagedDatabaseAsync("main");
        engine.Watch(database.Id);
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "items", cancellationToken);
        var backup = api.Server.Services.GetRequiredService<IContinuousBackup>();

        time.Advance(TimeSpan.FromSeconds(10));
        await backup.RunOnceAsync(cancellationToken);
        await Assert.That(engine.Opened).IsEqualTo(1);

        engine.CloseNextRead();
        time.Advance(TimeSpan.FromSeconds(10));
        await backup.RunOnceAsync(cancellationToken);
        await Assert.That(engine.Closed).IsEqualTo(1);
        await Assert.That(logs.Entries.Any(entry => entry.Level == LogLevel.Warning && entry.Message.StartsWith("Continuous backup of", StringComparison.Ordinal))).IsFalse();
        await Assert.That(logs.Entries.Any(entry => entry.Level == LogLevel.Debug && entry.Message.Contains("its database closed while it was read", StringComparison.Ordinal))).IsTrue();

        time.Advance(TimeSpan.FromSeconds(10));
        await backup.RunOnceAsync(cancellationToken);
        await Assert.That(engine.Opened).IsEqualTo(2);
    }

    private sealed class ClosingBackup(IWalBackup engine) : IWalBackup
    {
        private string? _watched;
        private int _armed;
        private int _opened;
        private int _closed;

        public int Opened => Volatile.Read(ref _opened);

        public int Closed => Volatile.Read(ref _closed);

        public void Watch(string database) => Volatile.Write(ref _watched, database);

        // The next read of the watched database finds its store closed, as a delete or a shutdown leaves it.
        public void CloseNextRead() => Volatile.Write(ref _armed, 1);

        public async ValueTask<IWalTap?> OpenTapAsync(TenantContext? database, CancellationToken cancellationToken)
        {
            if (await engine.OpenTapAsync(database, cancellationToken) is not { } tap)
            {
                return null;
            }

            if (database?.DatabaseId is { } id && id == Volatile.Read(ref _watched))
            {
                Interlocked.Increment(ref _opened);
                return new ClosingTap(tap, this);
            }

            return tap;
        }

        public ValueTask<bool> RollForwardAsync(string file, IAsyncEnumerable<Stream> segments, CancellationToken cancellationToken) =>
            engine.RollForwardAsync(file, segments, cancellationToken);

        public LogCut CutLog(Stream segment, DateTimeOffset at, long? previous) => engine.CutLog(segment, at, previous);

        public DateTimeOffset? ReadMark(Stream segment) => engine.ReadMark(segment);

        public ValueTask ImportDatabaseAsync(TenantContext database, string file, CancellationToken cancellationToken) =>
            engine.ImportDatabaseAsync(database, file, cancellationToken);

        public ValueTask<byte[]> ChecksumAsync(string file, CancellationToken cancellationToken) => engine.ChecksumAsync(file, cancellationToken);

        public bool TakeClose()
        {
            if (Interlocked.Exchange(ref _armed, 0) == 0)
            {
                return false;
            }

            Interlocked.Increment(ref _closed);
            return true;
        }
    }

    private sealed class ClosingTap(IWalTap tap, ClosingBackup owner) : IWalTap
    {
        public async ValueTask<WalRead> ReadAsync(Stream destination, CancellationToken cancellationToken)
        {
            // The store closed under the read: what a delete or a shutdown leaves behind.
            ObjectDisposedException.ThrowIf(owner.TakeClose(), this);
            return await tap.ReadAsync(destination, cancellationToken);
        }

        public ValueTask<WalSnapshot> SnapshotAsync(Stream destination, string snapshotPath, CancellationToken cancellationToken) =>
            tap.SnapshotAsync(destination, snapshotPath, cancellationToken);

        public ValueTask DisposeAsync() => tap.DisposeAsync();
    }

    private sealed class BackupLogs : ILoggerProvider, ILogger
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((logLevel, formatter(state, exception)));

        public void Dispose()
        {
        }
    }
}

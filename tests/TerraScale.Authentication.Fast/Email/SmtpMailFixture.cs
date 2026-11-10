using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using SmtpServer;
using SmtpServer.Authentication;
using SmtpServer.ComponentModel;
using SmtpServer.Mail;
using SmtpServer.Net;
using SmtpServer.Protocol;
using SmtpServer.Storage;

namespace TerraScale.Authentication.Fast.Email;

public sealed class SmtpMailFixture : IAsyncDisposable
{
    public const string Username = "ocid1.user.oc1..synthetic-smtp-credential";
    public const string Password = "synthetic-smtp-password-0123456789-a";

    private readonly SmtpServer.SmtpServer server;
    private readonly Task running;
    private readonly State state;

    private SmtpMailFixture(SmtpServer.SmtpServer server, Task running, IPEndPoint endpoint, State state)
    {
        this.server = server;
        this.running = running;
        this.Endpoint = endpoint;
        this.state = state;
    }

    public IPEndPoint Endpoint { get; }

    public IReadOnlyList<ReceivedMail> Accepted => [.. this.state.Accepted];

    public SmtpReplyCode? RefuseRecipientsWith
    {
        get => this.state.RefuseRecipientsWith;
        set => this.state.RefuseRecipientsWith = value;
    }

    public Task<ReceivedMail> WaitForAsync(string recipient) =>
        this.state.WaitForAsync(recipient).WaitAsync(TimeSpan.FromSeconds(10));

    public static async Task<SmtpMailFixture> StartAsync()
    {
        var state = new State();
        var listeners = new EndpointListenerFactory();
        var bound = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnStarted(object? sender, EndpointEventArgs args) => bound.TrySetResult((IPEndPoint)args.LocalEndPoint);
        listeners.EndpointStarted += OnStarted;
        var services = new ServiceProvider();
        services.Add((IEndpointListenerFactory)listeners);
        services.Add(new DelegatingUserAuthenticator((user, password) => user == Username && password == Password));
        services.Add((IMessageStoreFactory)new DelegatingMessageStoreFactory(_ => new RecordingStore(state)));
        services.Add((IMailboxFilterFactory)new DelegatingMailboxFilterFactory(_ => new DeliverToFilter(state)));
        var server = new SmtpServer.SmtpServer(
            new SmtpServerOptionsBuilder()
                .ServerName("localhost")
                .Endpoint(builder => builder.Port(0).AllowUnsecureAuthentication())
                .CommandWaitTimeout(TimeSpan.FromSeconds(10))
                .Build(),
            services);
        var shutdown = new CancellationTokenSource();
        var running = Task.Run(() => server.StartAsync(shutdown.Token));
        var endpoint = await bound.Task.WaitAsync(TimeSpan.FromSeconds(10));
        listeners.EndpointStarted -= OnStarted;
        return new SmtpMailFixture(server, running, endpoint, state);
    }

    public async ValueTask DisposeAsync()
    {
        this.server.Shutdown();
        try
        {
            await this.running;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed class State
    {
        public readonly ConcurrentQueue<ReceivedMail> Accepted = [];

        public SmtpReplyCode? RefuseRecipientsWith;

        private readonly Lock gate = new();
        private readonly Dictionary<string, TaskCompletionSource<ReceivedMail>> arrivals = new(StringComparer.OrdinalIgnoreCase);

        public Task<ReceivedMail> WaitForAsync(string recipient)
        {
            lock (this.gate)
            {
                if (this.arrivals.TryGetValue(recipient, out var pending) && pending.Task.IsCompleted)
                {
                    return pending.Task;
                }

                var source = new TaskCompletionSource<ReceivedMail>(TaskCreationOptions.RunContinuationsAsynchronously);
                this.arrivals[recipient] = source;
                return source.Task;
            }
        }

        public void Arrived(ReceivedMail mail)
        {
            this.Accepted.Enqueue(mail);
            lock (this.gate)
            {
                foreach (var to in mail.To)
                {
                    if (this.arrivals.TryGetValue(to, out var source))
                    {
                        source.TrySetResult(mail);
                    }
                    else
                    {
                        this.arrivals[to] = TaskCompletionSourceFactory.Completed(mail);
                    }
                }
            }
        }
    }

    private static class TaskCompletionSourceFactory
    {
        public static TaskCompletionSource<ReceivedMail> Completed(ReceivedMail mail)
        {
            var source = new TaskCompletionSource<ReceivedMail>(TaskCreationOptions.RunContinuationsAsynchronously);
            source.SetResult(mail);
            return source;
        }
    }

    private sealed class RecordingStore(State state) : MessageStore
    {
        public override async Task<SmtpServer.Protocol.SmtpResponse> SaveAsync(ISessionContext context, IMessageTransaction transaction, ReadOnlySequence<byte> buffer, CancellationToken cancellationToken)
        {
            using var copy = new MemoryStream(buffer.ToArray());
            state.Arrived(new ReceivedMail(
                transaction.From.AsAddress(),
                [.. transaction.To.Select(to => to.AsAddress())],
                await MimeKit.MimeMessage.LoadAsync(copy, cancellationToken)));
            return SmtpServer.Protocol.SmtpResponse.Ok;
        }
    }

    private sealed class DeliverToFilter(State state) : MailboxFilter
    {
        public override Task<bool> CanDeliverToAsync(ISessionContext context, IMailbox to, IMailbox from, CancellationToken cancellationToken)
        {
            if (state.RefuseRecipientsWith is { } code)
            {
                throw new SmtpResponseException(new SmtpServer.Protocol.SmtpResponse(code, "refused by test"));
            }

            return Task.FromResult(true);
        }
    }
}

public sealed record ReceivedMail(string From, IReadOnlyList<string> To, MimeKit.MimeMessage Message);

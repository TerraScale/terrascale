using TerraBase.Features.Backup;

namespace TerraBase.LiveRestoreDrill
{
    internal sealed class ReadOnlyStore(IObjectStore inner, string file, string scope) : IObjectStore
    {
        public Task PutAsync(string key, Stream content, CancellationToken cancellationToken)
        {
            throw new NotSupportedException("restore drill refuses writes");
        }

        public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken)
        {
            throw new NotSupportedException("restore drill refuses deletes");
        }

        public Task<bool> ReadAsync(string key, Func<Stream, CancellationToken, Task> read, CancellationToken cancellationToken)
        {
            Validate(key);
            return inner.ReadAsync(key, read, cancellationToken);
        }

        public IAsyncEnumerable<StoredObject> ListAsync(string prefix, string? startAfter, CancellationToken cancellationToken)
        {
            Validate(prefix);
            if (startAfter is not null)
            {
                Validate(startAfter);
            }

            return inner.ListAsync(prefix, startAfter, cancellationToken);
        }

        private void Validate(string key)
        {
            if ((!key.StartsWith(BackupLayout.Snapshots(file), StringComparison.Ordinal)
                    && !key.StartsWith(file + "/wal/", StringComparison.Ordinal)
                    && !key.StartsWith(BackupLayout.Keys(scope), StringComparison.Ordinal))
                || key.Split('/').Any(part => part is "." or ".."))
            {
                throw new InvalidDataException("restore drill refuses objects outside its file and key scope");
            }
        }

        // Run before every drill: these calls must throw before reaching the underlying store.
        internal async Task CheckWriteRejectionAsync()
        {
            await ExpectRejectionAsync(() => PutAsync("", Stream.Null, CancellationToken.None)).ConfigureAwait(false);
            await ExpectRejectionAsync(() => DeleteAsync("", CancellationToken.None)).ConfigureAwait(false);
        }

        private static async Task ExpectRejectionAsync(Func<Task> operation)
        {
            try
            {
                await operation().ConfigureAwait(false);
            }
            catch (NotSupportedException)
            {
                return;
            }

            throw new InvalidOperationException("read-only self-check failed");
        }
    }

    // Defense in depth: even an accidental SDK call cannot send a mutation or credentials to another host.
    internal sealed class ReadOnlyTransport(string bucket) : DelegatingHandler
    {
        internal const string Endpoint = "https://fly.storage.tigris.dev";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri;
            var rejected = request.Method != HttpMethod.Get || uri is null || uri.Scheme != "https" || uri.Host != "fly.storage.tigris.dev"
                || !uri.IsDefaultPort || uri.UserInfo.Length != 0
                || (uri.AbsolutePath != "/" + bucket && !uri.AbsolutePath.StartsWith("/" + bucket + "/", StringComparison.Ordinal));
            return rejected
                ? throw new NotSupportedException("restore drill permits only GET on its pinned Tigris bucket")
                : base.SendAsync(request, cancellationToken);
        }
    }
}

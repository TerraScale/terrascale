using System.Text.Json;

namespace TerraScale.Dashboard.Platform.Persistence
{
    public static class PlatformJson
    {
        // Discovery, headless responses and token grants have small fixed envelopes.
        private const int MaxBytes = 65536;

        public static async Task<JsonDocument> ReadAsync(HttpContent content, CancellationToken cancellationToken, int maxBytes = MaxBytes)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            if (content.Headers.ContentLength > maxBytes)
            {
                throw new HttpRequestException("Platform response is too large.");
            }

            await using var stream = await content.ReadAsStreamAsync(deadline.Token);
            var bytes = new byte[maxBytes + 1];
            var length = 0;
            while (length < bytes.Length)
            {
                var count = await stream.ReadAsync(bytes.AsMemory(length), deadline.Token);
                if (count == 0)
                {
                    break;
                }

                length += count;
            }
            if (length > maxBytes)
            {
                throw new HttpRequestException("Platform response is too large.");
            }

            return JsonDocument.Parse(bytes.AsMemory(0, length));
        }
    }
}

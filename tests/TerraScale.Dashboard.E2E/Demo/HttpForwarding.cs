namespace TerraScale.Dashboard.E2E
{
    internal static class HttpForwarding
    {
        public static async Task<HttpResponseMessage> SendAsync(HttpClient fallback, HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // An outer HttpClient already marked this request as sent; the fallback needs its own message.
            using var forwarded = new HttpRequestMessage(request.Method, request.RequestUri)
            { Version = request.Version, VersionPolicy = request.VersionPolicy };
            foreach (var header in request.Headers)
            {
                forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            foreach (var option in request.Options)
            {
                forwarded.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
            }

            if (request.Content is not null)
            {
                forwarded.Content = new ByteArrayContent(await request.Content.ReadAsByteArrayAsync(cancellationToken));
                foreach (var header in request.Content.Headers)
                {
                    forwarded.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }
            var response = await fallback.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.RequestMessage = request;
            return response;
        }
    }
}

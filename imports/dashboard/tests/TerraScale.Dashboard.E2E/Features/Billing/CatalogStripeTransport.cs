using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;

namespace TerraScale.Dashboard.E2E.Features.Billing;

// Mutate only provider responses; checkout still crosses the application's real Stripe SDK boundary.
internal sealed class CatalogStripeTransport(IHttpClientFactory original, string scenario) : HttpMessageHandler, IHttpClientFactory
{
    public int CatalogReads { get; private set; }
    public int CheckoutWrites { get; private set; }
    public string CheckoutPrice { get; private set; } = "";
    public HttpClient CreateClient(string name) => new(this, false);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var forwarded = new HttpRequestMessage(request.Method, request.RequestUri)
        { Version = request.Version, VersionPolicy = request.VersionPolicy };
        foreach (var header in request.Headers) forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
        foreach (var option in request.Options) forwarded.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
        if (request.Content is { } content)
        {
            forwarded.Content = new ByteArrayContent(await content.ReadAsByteArrayAsync(cancellationToken));
            foreach (var header in content.Headers) forwarded.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        if (request.RequestUri is { Host: "api.stripe.com", AbsolutePath: "/v1/checkout/sessions" } && request.Method == HttpMethod.Post)
        {
            CheckoutWrites++;
            CheckoutPrice = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken)).GetValueOrDefault("line_items[0][price]").ToString();
        }
        using var client = original.CreateClient("platform");
        var response = await client.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.RequestMessage = request;
        if (request.RequestUri is not { Host: "api.stripe.com", AbsolutePath: "/v1/prices" } || request.Method != HttpMethod.Get || !response.IsSuccessStatusCode)
            return response;
        CatalogReads++;
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))!;
        var price = body["data"]!.AsArray().Single(item => item!["id"]!.GetValue<string>() == "price_pro_monthly")!;
        switch (scenario)
        {
            case "amount": price["unit_amount"] = 9900; break;
            case "interval": price["recurring"]!["interval"] = "year"; break;
            case "interval-count": price["recurring"]!["interval_count"] = 2; break;
            case "product": price["product"]!["metadata"]!["plan"] = "business"; price["product"]!["name"] = "Other product"; break;
            case "metered": price["recurring"]!["usage_type"] = "metered"; break;
            case "inactive": price["active"] = false; break;
            case "inactive-product": price["product"]!["active"] = false; break;
            case "currency": price["currency"] = "eur"; break;
        }
        response.Content.Dispose();
        response.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return response;
    }
}

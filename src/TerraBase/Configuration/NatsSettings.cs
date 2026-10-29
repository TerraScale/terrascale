namespace TerraBase.Configuration;

internal sealed class NatsSettings
{
    internal static string[] Keys => ["nats-url", "nats-credentials-file", "nats-certificate", "nats-private-key", "nats-ca"];

    public required string Url { get; init; }
    public required string CredentialsFile { get; init; }
    public required string Certificate { get; init; }
    public required string PrivateKey { get; init; }
    public required string Authority { get; init; }

    public override string ToString() => "NATS role identity";

    internal static NatsSettings? Parse(IReadOnlyDictionary<string, string> values)
    {
        if (!Keys.Any(values.ContainsKey))
        {
            return null;
        }

        if (!Keys.All(values.ContainsKey)
            || values["nats-url"].Split(',').Any(url => !Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("tls" or "nats") || parsed.Port <= 0)
            || Keys.Skip(1).Any(key => !Path.IsPathFullyQualified(values[key])))
        {
            throw new TerraBaseConfigurationException($"{string.Join(", ", Keys)} must be set together: tls:// or nats:// URLs with ports and absolute file paths");
        }

        return new NatsSettings
        {
            Url = values["nats-url"],
            CredentialsFile = values["nats-credentials-file"],
            Certificate = values["nats-certificate"],
            PrivateKey = values["nats-private-key"],
            Authority = values["nats-ca"],
        };
    }
}

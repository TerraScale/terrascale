namespace TerraScale.Auth.Tests;

/// <summary>Fake secrets for log-redaction tests. No real credential is ever written into a log line.</summary>
internal static class LogSecrets
{
    public const string FakeSecret = "tok_FAKE_SECRET_123";

    /// <summary>An upstream reply whose rendering carries a token, as a custom log value might.</summary>
    public sealed class UpstreamReply(string token)
    {
        public override string ToString() => $"token={token}";
    }
}

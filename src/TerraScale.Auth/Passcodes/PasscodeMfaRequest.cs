namespace TerraScale.Auth.Passcodes;

using TerraScale.Auth.Api;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Email;
using TerraScale.Auth.Mfa;
using TerraScale.Auth.Store;
using TerraScale.Auth.Ui;
using TerraScale.Auth.Users;

/// <summary>Passcode MFA step body.</summary>
internal sealed record PasscodeMfaRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("mfa_token")] string? MfaToken,
    string? Code);

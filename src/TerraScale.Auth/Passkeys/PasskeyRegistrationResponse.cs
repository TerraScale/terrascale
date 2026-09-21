namespace TerraScale.Auth.Passkeys;

using System.Net;
using System.Text;
using TerraScale.Auth.Api;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Store;
using TerraScale.Auth.Ui;
using TerraScale.Auth.Users;
using TerraScale.Auth.WebAuthn;
using TerraScale.DotNet;

/// <summary>Registration ceremony response fields.</summary>
public sealed record PasskeyRegistrationResponse(string ClientDataJSON, string AttestationObject, string[]? Transports);

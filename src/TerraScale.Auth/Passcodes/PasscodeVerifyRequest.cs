namespace TerraScale.Auth.Passcodes;

using TerraScale.Auth.Api;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Email;
using TerraScale.Auth.Mfa;
using TerraScale.Auth.Store;
using TerraScale.Auth.Ui;
using TerraScale.Auth.Users;

/// <summary>Passcode verify body.</summary>
internal sealed record PasscodeVerifyRequest(string? Email, string? Code);

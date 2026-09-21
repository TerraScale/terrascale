namespace TerraScale.Auth.Organizations;

using System.Net;
using TerraScale.Auth.Api;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Email;
using TerraScale.Auth.Store;
using TerraScale.Auth.Ui;
using TerraScale.Auth.Users;
using TerraScale.DotNet;

/// <summary>Create membership request body.</summary>
internal sealed record CreateMembershipRequest([property: System.Text.Json.Serialization.JsonPropertyName("user_id")] string? UserId, string? Role);

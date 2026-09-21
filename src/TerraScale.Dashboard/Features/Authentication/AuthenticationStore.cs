using System.Collections.Immutable;
using System.Security.Cryptography;
using TerraScale.Dashboard.Features.Authentication;

namespace TerraScale.Dashboard;

public sealed partial class DashboardStore
{
    public AuthInstanceState AuthInstance
    {
        get
        {
            lock (World.AuthInstances)
            {
                if (!World.AuthInstances.TryGetValue(ActiveProjectId, out var instance))
                {
                    instance = AuthInstanceState.Create(ActiveProject.Name, seeded: false);
                    World.AuthInstances[ActiveProjectId] = instance;
                }
                return instance;
            }
        }
    }

    public void SaveAuthResource(string section, IReadOnlyDictionary<string, string> values, string? id = null)
    {
        var state = AuthInstance;
        lock (state)
        {
            var existing = state.Resources.FirstOrDefault(resource => resource.Section == section && resource.Id == id);
            var fields = values.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);
            // Demo credentials cannot authenticate against ts-auth. Never retain supplied secrets.
            fields = fields.Remove("client_secret");
            if (existing is not null && existing.Value("credential_configured") == "true" || values.GetValueOrDefault("client_secret", "").Length > 0)
                fields = fields.SetItem("credential_configured", "true");
            var item = new AuthResource(existing?.Id ?? AccountWorld.NewId(section[..Math.Min(3, section.Length)]), section,
                fields, existing?.Status ?? (section == "invitations" ? "Pending" : "Active"), existing?.CreatedAt ?? DateTimeOffset.UtcNow);
            if (existing is null && (section is "keys" or "directories" or "webhooks" || section == "clients" && values.GetValueOrDefault("type") != "Public client"))
            {
                var secret = "demo_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
                state.PendingSecret = secret;
                item = item with { Values = item.Values.SetItem("prefix", secret[..13]) };
            }
            state.Resources = existing is null ? state.Resources.Add(item) : state.Resources.Replace(existing, item);
            RecordAuthAudit($"{section}.{(existing is null ? "created" : "updated")}", item.Id);
        }
    }

    public string? TakeAuthSecret()
    {
        var state = AuthInstance;
        lock (state)
        {
            var secret = state.PendingSecret;
            state.PendingSecret = null;
            return secret;
        }
    }

    public bool RemoveAuthResource(string section, string id)
    {
        var state = AuthInstance;
        lock (state)
        {
            var item = state.Resources.FirstOrDefault(resource => resource.Section == section && resource.Id == id);
            if (item is null) return false;
            if (section is "sessions" or "keys" or "invitations")
                state.Resources = state.Resources.Replace(item, item with { Status = "Revoked" });
            else
            {
                state.Resources = [.. state.Resources.Where(resource => resource.Id != id
                    && !(section == "users" && resource.Value("user") == id)
                    && !(section == "organizations" && resource.Value("organization") == id)
                    && !(section == "webhooks" && resource.Value("endpoint") == id))];
            }
            RecordAuthAudit($"{section}.{(section is "sessions" or "keys" or "invitations" ? "revoked" : "deleted")}", id);
            return true;
        }
    }

    public bool ApplyAuthAction(string section, string id, string action)
    {
        var state = AuthInstance;
        lock (state)
        {
            var item = state.Resources.FirstOrDefault(resource => resource.Section == section && resource.Id == id);
            if (item is null) return false;
            switch (section, action)
            {
                case ("users", "block"):
                    state.Resources = state.Resources.Replace(item, item with { Status = item.Status == "Blocked" ? "Active" : "Blocked" });
                    state.Resources = [.. state.Resources.Select(resource => resource.Section == "sessions" && resource.Value("user") == id ? resource with { Status = "Revoked" } : resource)];
                    break;
                case ("users", "revoke-sessions"):
                    state.Resources = [.. state.Resources.Select(resource => resource.Section == "sessions" && resource.Value("user") == id ? resource with { Status = "Revoked" } : resource)];
                    break;
                case ("directories", "rotate"):
                    state.PendingSecret = "demo_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
                    break;
                case ("deliveries", "replay") when item.Status == "Failed":
                    state.Resources = state.Resources.Replace(item, item with { Status = "Pending" });
                    break;
                case ("webhooks", "test"):
                    state.Resources = state.Resources.Add(new(AccountWorld.NewId("del"), "deliveries",
                        new Dictionary<string, string> { ["name"] = "Test event", ["endpoint"] = id, ["event"] = "webhook.test", ["attempts"] = "0" }.ToImmutableDictionary(), "Pending", DateTimeOffset.UtcNow));
                    break;
                default: return false;
            }
            RecordAuthAudit($"{section}.{action}", id);
            return true;
        }
    }

    public void SaveAuthBranding(string name, string logoUrl, string themeColor)
    {
        var state = AuthInstance;
        lock (state)
        {
            state.Name = name.Trim();
            state.LogoUrl = logoUrl.Trim();
            state.ThemeColor = themeColor.Trim();
            RecordAuthAudit("instance.updated", ActiveProjectId);
        }
    }

    private void RecordAuthAudit(string name, string target) => AuthInstance.Resources = AuthInstance.Resources.Add(new(
        AccountWorld.NewId("evt"), "audit", new Dictionary<string, string>
        { ["name"] = name, ["actor"] = CurrentUser.Email, ["target"] = target }.ToImmutableDictionary(), "Recorded", DateTimeOffset.UtcNow));
}

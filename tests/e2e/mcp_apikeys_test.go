package e2e

import (
	"slices"
	"strings"
	"testing"
)

// TestMCPAPIKeysIssueRotateAndRevokeCredentials manages a project's API keys
// and M2M clients through the MCP tools.
func TestMCPAPIKeysIssueRotateAndRevokeCredentials(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	registerAndOnboard(t, page, newAccount("mcp-apikeys"))
	session := connectMCP(t, createAccessToken(t, page, journeyPassword, "credentials assistant"))

	var database struct{ ID string }
	callTool(t, session, "create_database", map[string]any{"name": uniqueName("keys"), "region": "GRU"}, &database)

	type issued struct {
		APIKey struct {
			KeyID      string
			Prefix     string
			DatabaseID string
			Scopes     []string
			IsActive   bool
		}
		Secret string
	}
	var key issued
	callTool(t, session, "create_api_key", map[string]any{
		"name": "mcp key", "scopes": []string{"database:read", "database:write"}, "databaseId": database.ID,
	}, &key)
	if !strings.HasPrefix(key.Secret, "tsk_demo_") || !key.APIKey.IsActive || key.APIKey.DatabaseID != database.ID ||
		!slices.Equal(key.APIKey.Scopes, []string{"database:read", "database:write"}) {
		t.Fatalf("create_api_key = %+v, want an active database key with a tsk_demo_ secret", key)
	}
	var client issued
	callTool(t, session, "create_m2m_client", map[string]any{"name": "mcp worker", "scopes": []string{"database:read"}}, &client)
	if !strings.HasPrefix(client.Secret, "tsm_demo_") {
		t.Fatalf("create_m2m_client secret %q must start with tsm_demo_", client.Secret)
	}

	type listed struct {
		Credentials []struct {
			KeyID, Name, Kind, Secret string
			IsActive                  bool
		}
	}
	var all listed
	callTool(t, session, "list_api_keys", nil, &all)
	if len(all.Credentials) != 2 {
		t.Fatalf("list_api_keys = %+v, want the key and the M2M client", all.Credentials)
	}
	for _, credential := range all.Credentials {
		if credential.Secret != "" {
			t.Errorf("list_api_keys exposed the secret of %s", credential.Name)
		}
	}
	var machines listed
	callTool(t, session, "list_api_keys", map[string]any{"kind": "m2m"}, &machines)
	if len(machines.Credentials) != 1 || machines.Credentials[0].KeyID != client.APIKey.KeyID || machines.Credentials[0].Kind != "m2m" {
		t.Errorf("list_api_keys(kind=m2m) = %+v, want only the M2M client", machines.Credentials)
	}

	var rotated issued
	callTool(t, session, "rotate_api_key", map[string]any{"keyId": key.APIKey.KeyID}, &rotated)
	if rotated.Secret == key.Secret || !strings.HasPrefix(rotated.Secret, "tsk_demo_") || rotated.APIKey.Prefix == key.APIKey.Prefix {
		t.Errorf("rotate_api_key = %+v, want a new secret and prefix", rotated)
	}
	callTool(t, session, "revoke_api_key", map[string]any{"keyId": key.APIKey.KeyID}, nil)
	var inactive listed
	callTool(t, session, "list_api_keys", map[string]any{"status": "inactive"}, &inactive)
	if len(inactive.Credentials) != 1 || inactive.Credentials[0].KeyID != key.APIKey.KeyID || inactive.Credentials[0].IsActive {
		t.Errorf("list_api_keys(status=inactive) = %+v, want the revoked key", inactive.Credentials)
	}
	if message := callToolError(t, session, "rotate_api_key", map[string]any{"keyId": key.APIKey.KeyID}); !strings.Contains(message, "not found") {
		t.Errorf("rotating a revoked key error = %q, want not found", message)
	}
	if message := callToolError(t, session, "create_api_key", map[string]any{"name": "no scopes", "scopes": []string{}}); !strings.Contains(message, "scopes") {
		t.Errorf("create_api_key without scopes error = %q, want the scope rule", message)
	}
	if message := callToolError(t, session, "list_api_keys", map[string]any{"kind": "robot"}); !strings.Contains(message, "kind") {
		t.Errorf("list_api_keys with an unknown kind error = %q, want the kind rule", message)
	}
}

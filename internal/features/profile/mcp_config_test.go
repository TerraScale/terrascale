package profile

import (
	"encoding/json/v2"
	"testing"
)

func TestMCPClientConfigIsValidJSONForAnyEndpoint(t *testing.T) {
	data := pageData{MCPEndpoint: `https://dash"board\.example/mcp`, CreatedToken: &createdAccessToken{Secret: "tspat_secret"}}
	var config struct {
		MCPServers map[string]struct {
			URL     string            `json:"url"`
			Headers map[string]string `json:"headers"`
		} `json:"mcpServers"`
	}
	if err := json.Unmarshal([]byte(mcpClientConfig(data)), &config); err != nil {
		t.Fatalf("config is not JSON: %v\n%s", err, mcpClientConfig(data))
	}
	server := config.MCPServers["terrascale"]
	if server.URL != data.MCPEndpoint || server.Headers["Authorization"] != "Bearer tspat_secret" {
		t.Errorf("config = %+v, want the endpoint and the new token", server)
	}
}

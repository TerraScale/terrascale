package e2e

import (
	"net/http"
	"slices"
	"strings"
	"testing"

	"github.com/modelcontextprotocol/go-sdk/mcp"
)

// TestMCPDatabaseLifecycleThroughAccessToken registers an account, issues a
// personal access token, and manages databases through the stateless MCP
// endpoint with the official MCP Go SDK client.
func TestMCPDatabaseLifecycleThroughAccessToken(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	registerAndOnboard(t, page, newAccount("mcp-databases"))
	session := connectMCP(t, createAccessToken(t, page, journeyPassword, "e2e assistant"))

	if version := session.InitializeResult().ProtocolVersion; version != mcpProtocolVersion {
		t.Fatalf("negotiated MCP protocol %q, want stateless %q", version, mcpProtocolVersion)
	}
	toolList, err := session.ListTools(t.Context(), &mcp.ListToolsParams{})
	must(t, err)
	foundOnboardingTool := false
	for _, tool := range toolList.Tools {
		if tool.Name == "save_onboarding_step" {
			foundOnboardingTool = true
			if tool.Annotations == nil || tool.Annotations.OpenWorldHint == nil || !*tool.Annotations.OpenWorldHint {
				t.Fatal("save_onboarding_step must advertise that it can contact Stripe")
			}
			break
		}
	}
	if !foundOnboardingTool {
		t.Fatal("tools/list did not include save_onboarding_step")
	}
	names := toolNames(t, session)
	for _, want := range []string{"list_databases", "get_database", "create_database", "branch_database", "delete_database"} {
		if !slices.Contains(names, want) {
			t.Errorf("tools/list is missing %s; got %v", want, names)
		}
	}

	type database struct {
		ID, Name, Status, Region string
	}
	name := uniqueName("mcp")
	var created database
	callTool(t, session, "create_database", map[string]any{"name": name, "region": "GRU"}, &created)
	if created.Name != name || created.Status != "ready" || created.Region != "GRU" {
		t.Fatalf("create_database = %+v, want ready %s in GRU", created, name)
	}
	var listed struct{ Databases []database }
	callTool(t, session, "list_databases", nil, &listed)
	if !slices.ContainsFunc(listed.Databases, func(d database) bool { return d.ID == created.ID }) {
		t.Fatalf("list_databases = %+v, want %s", listed.Databases, created.ID)
	}
	var detail struct {
		ID        string
		Regions   []string
		CanManage bool
	}
	callTool(t, session, "get_database", map[string]any{"databaseId": created.ID}, &detail)
	if detail.ID != created.ID || !detail.CanManage || len(detail.Regions) == 0 {
		t.Errorf("get_database = %+v, want a manageable database with regions", detail)
	}

	if message := callToolError(t, session, "create_database", map[string]any{"name": "Not Valid"}); !strings.Contains(message, "lowercase") {
		t.Errorf("invalid create_database error = %q, want the naming rule", message)
	}
	if message := callToolError(t, session, "list_databases", map[string]any{"organizationId": "org_missing"}); !strings.Contains(message, "not found") {
		t.Errorf("foreign organization scope error = %q, want not found", message)
	}
	if message := callToolError(t, session, "delete_database", map[string]any{"databaseId": created.ID, "confirmName": "wrong"}); !strings.Contains(message, "confirm") {
		t.Errorf("unconfirmed delete_database error = %q, want a confirmation error", message)
	}
	callTool(t, session, "delete_database", map[string]any{"databaseId": created.ID, "confirmName": name}, nil)
	callTool(t, session, "list_databases", nil, &listed)
	if slices.ContainsFunc(listed.Databases, func(d database) bool { return d.ID == created.ID }) {
		t.Errorf("deleted database %s is still listed", created.ID)
	}
}

// TestMCPRejectsMissingAndUnknownAccessTokens checks that the MCP endpoint
// answers 401 with a Bearer challenge and never serves GET streams.
func TestMCPRejectsMissingAndUnknownAccessTokens(t *testing.T) {
	t.Parallel()
	for _, token := range []string{"", "tspat_unknown"} {
		request, err := http.NewRequestWithContext(t.Context(), http.MethodPost, baseURL+"/mcp", strings.NewReader(`{}`))
		must(t, err)
		if token != "" {
			request.Header.Set("Authorization", "Bearer "+token)
		}
		response, err := http.DefaultClient.Do(request)
		must(t, err)
		response.Body.Close()
		if response.StatusCode != http.StatusUnauthorized || !strings.HasPrefix(response.Header.Get("WWW-Authenticate"), "Bearer") {
			t.Errorf("token %q: status %d, WWW-Authenticate %q; want 401 with a Bearer challenge",
				token, response.StatusCode, response.Header.Get("WWW-Authenticate"))
		}
	}
	page := newPage(t)
	registerAndOnboard(t, page, newAccount("mcp-get"))
	request, err := http.NewRequestWithContext(t.Context(), http.MethodGet, baseURL+"/mcp", nil)
	must(t, err)
	request.Header.Set("Authorization", "Bearer "+createAccessToken(t, page, journeyPassword, "stream probe"))
	response, err := http.DefaultClient.Do(request)
	must(t, err)
	response.Body.Close()
	if response.StatusCode != http.StatusMethodNotAllowed {
		t.Errorf("GET /mcp = %d, want 405 because the stateless endpoint has no GET stream", response.StatusCode)
	}
}

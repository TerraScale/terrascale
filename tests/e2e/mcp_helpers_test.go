package e2e

import (
	"encoding/json/v2"
	"net/http"
	"strings"
	"testing"

	"github.com/modelcontextprotocol/go-sdk/mcp"
	"github.com/mxschmitt/playwright-go"
)

// mcpProtocolVersion is the stateless MCP revision the dashboard serves.
const mcpProtocolVersion = "2026-07-28"

// createAccessToken issues a personal access token for the page's signed-in
// account, whose password is password, through the profile JSON route and
// returns its one-time secret.
func createAccessToken(t *testing.T, page playwright.Page, password, name string) string {
	t.Helper()
	response, err := page.Request().Post("/api/v1/dashboard/profile/access-tokens", playwright.APIRequestContextPostOptions{
		Headers: map[string]string{
			"Authorization": "Bearer " + sessionToken(t, page.Context()),
			"Accept":        "application/json",
		},
		Form: map[string]any{"name": name, "expiresInDays": "30", "password": password},
	})
	must(t, err)
	var created struct {
		Secret string `json:"secret"`
	}
	responseJSON(t, response, 201, &created)
	if !strings.HasPrefix(created.Secret, "tspat_") {
		t.Fatalf("access token secret %q must start with tspat_", created.Secret)
	}
	return created.Secret
}

// connectMCP opens a stateless MCP client session authenticated with token
// using the official MCP Go SDK.
func connectMCP(t *testing.T, token string) *mcp.ClientSession {
	t.Helper()
	transport := &mcp.StreamableClientTransport{
		Endpoint:   baseURL + "/mcp",
		HTTPClient: &http.Client{Transport: bearerTransport{token: token}},
		MaxRetries: -1,
	}
	client := mcp.NewClient(&mcp.Implementation{Name: "dashboard-e2e", Version: "1.0.0"}, nil)
	session, err := client.Connect(t.Context(), transport, nil)
	must(t, err)
	t.Cleanup(func() { _ = session.Close() })
	return session
}

type bearerTransport struct {
	token string
}

func (b bearerTransport) RoundTrip(r *http.Request) (*http.Response, error) {
	r = r.Clone(r.Context())
	r.Header.Set("Authorization", "Bearer "+b.token)
	return http.DefaultTransport.RoundTrip(r)
}

// callTool calls a tool that must succeed and decodes its structured result
// into target when target is not nil.
func callTool(t *testing.T, session *mcp.ClientSession, name string, arguments map[string]any, target any) {
	t.Helper()
	result := invokeTool(t, session, name, arguments)
	if result.IsError {
		t.Fatalf("tool %s(%v) failed: %s", name, arguments, toolText(result))
	}
	if target == nil {
		return
	}
	encoded, err := json.Marshal(result.StructuredContent)
	must(t, err)
	if err := json.Unmarshal(encoded, target, json.MatchCaseInsensitiveNames(true)); err != nil {
		t.Fatalf("decode %s result: %v: %s", name, err, encoded)
	}
}

// callToolError calls a tool that must fail and returns its error text.
func callToolError(t *testing.T, session *mcp.ClientSession, name string, arguments map[string]any) string {
	t.Helper()
	result := invokeTool(t, session, name, arguments)
	if !result.IsError {
		t.Fatalf("tool %s(%v) succeeded, want an error result", name, arguments)
	}
	return toolText(result)
}

func invokeTool(t *testing.T, session *mcp.ClientSession, name string, arguments map[string]any) *mcp.CallToolResult {
	t.Helper()
	if arguments == nil {
		arguments = map[string]any{}
	}
	result, err := session.CallTool(t.Context(), &mcp.CallToolParams{Name: name, Arguments: arguments})
	must(t, err)
	return result
}

func toolText(result *mcp.CallToolResult) string {
	var text []string
	for _, content := range result.Content {
		if block, ok := content.(*mcp.TextContent); ok {
			text = append(text, block.Text)
		}
	}
	return strings.Join(text, "\n")
}

// toolNames lists every tool the session can call.
func toolNames(t *testing.T, session *mcp.ClientSession) []string {
	t.Helper()
	var names []string
	for tool, err := range session.Tools(t.Context(), nil) {
		must(t, err)
		names = append(names, tool.Name)
	}
	return names
}

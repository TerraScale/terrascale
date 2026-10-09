package e2e

import (
	"net/http"
	"net/url"
	"slices"
	"strings"
	"testing"

	"github.com/modelcontextprotocol/go-sdk/mcp"
	"github.com/mxschmitt/playwright-go"
)

// TestMCPAccessTokenIsShownOnceUsedAndRevokedFromProfile creates a personal
// access token in the profile page, connects an MCP client with it, and
// revokes it from the same page.
func TestMCPAccessTokenIsShownOnceUsedAndRevokedFromProfile(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	account := newAccount("mcp-token-ui")
	registerAndOnboard(t, page, account)

	goTo(t, page, "/profile?tab=access-tokens")
	visible(t, pageByRole(page, *playwright.AriaRoleHeading, "Personal access tokens"))
	form := page.Locator(`form[action="/api/v1/dashboard/profile/access-tokens"]`)
	fill(t, form.Locator(`[name="name"]`), "Claude desktop")
	selectOption(t, form.Locator(`[name="expiresInDays"]`), "30")
	fill(t, form.Locator(`[name="password"]`), "not-my-password")
	click(t, byRole(form, *playwright.AriaRoleButton, "Create token"))
	must(t, expect.Locator(page.GetByRole(*playwright.AriaRoleAlert)).ToContainText("Current password is incorrect."))
	must(t, expect.Locator(page.Locator("#access-token-secret")).ToHaveCount(0))

	fill(t, form.Locator(`[name="name"]`), "Claude desktop")
	selectOption(t, form.Locator(`[name="expiresInDays"]`), "30")
	fill(t, form.Locator(`[name="password"]`), account.Password)
	click(t, byRole(form, *playwright.AriaRoleButton, "Create token"))

	secretField := page.Locator("#access-token-secret")
	visible(t, secretField)
	secret, err := secretField.InputValue()
	must(t, err)
	if !strings.HasPrefix(secret, "tspat_") {
		t.Fatalf("revealed access token %q must start with tspat_", secret)
	}
	config, err := page.Locator("#profile-mcp-config pre").TextContent()
	must(t, err)
	if !strings.Contains(config, "Bearer "+secret) || !strings.Contains(config, baseURL+"/mcp") {
		t.Errorf("MCP client config must carry the new token and %s/mcp:\n%s", baseURL, config)
	}
	screenshot(t, page, "profile-access-token-created")
	// Reloading after the reveal must not create a second token.
	_, err = page.Reload()
	must(t, err)
	must(t, expect.Locator(page.Locator("#access-token-secret")).ToHaveCount(0))
	must(t, expect.Locator(page.Locator("#profile-access-tokens tbody tr").Filter(playwright.LocatorFilterOptions{HasText: "Claude desktop"})).ToHaveCount(1))

	session := connectMCP(t, secret)
	var profile struct{ Email string }
	callTool(t, session, "get_profile", nil, &profile)
	if profile.Email != account.Email {
		t.Errorf("get_profile email = %q, want %q", profile.Email, account.Email)
	}

	goTo(t, page, "/profile?tab=access-tokens")
	must(t, expect.Locator(page.Locator("#access-token-secret")).ToHaveCount(0))
	row := page.Locator("#profile-access-tokens tbody tr").Filter(playwright.LocatorFilterOptions{HasText: "Claude desktop"})
	visible(t, row)
	must(t, expect.Locator(row).ToContainText("…"+secret[len(secret)-4:]))
	screenshot(t, page, "profile-access-tokens")

	click(t, byRole(row, *playwright.AriaRoleButton, "Revoke", true))
	dialog := pageByRole(page, *playwright.AriaRoleDialog, "Revoke “Claude desktop”?")
	visible(t, dialog)
	click(t, byRole(dialog, *playwright.AriaRoleButton, "Revoke token"))
	visible(t, page.GetByText("Access token revoked."))
	must(t, expect.Locator(page.Locator("#profile-access-tokens tbody tr")).ToHaveCount(0))

	if _, err := session.CallTool(t.Context(), &mcp.CallToolParams{Name: "get_profile", Arguments: map[string]any{}}); err == nil {
		t.Error("a session using a revoked access token must be rejected")
	}
	client := mcp.NewClient(&mcp.Implementation{Name: "dashboard-e2e", Version: "1.0.0"}, nil)
	if _, err := client.Connect(t.Context(), &mcp.StreamableClientTransport{
		Endpoint: baseURL + "/mcp", HTTPClient: &http.Client{Transport: bearerTransport{token: secret}}, MaxRetries: -1,
	}, nil); err == nil {
		t.Error("connecting with a revoked access token must fail")
	}
}

// TestMCPPasswordChangeRevokesAccessTokens changes the password in the
// profile and checks that the account's access tokens stop working with it.
func TestMCPPasswordChangeRevokesAccessTokens(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	account := newAccount("mcp-token-password")
	registerAndOnboard(t, page, account)
	secret := createAccessToken(t, page, journeyPassword, "before password change")
	session := connectMCP(t, secret)
	callTool(t, session, "get_profile", nil, nil)

	goTo(t, page, "/profile?tab=security")
	const nextPassword = "E2e-token-next-123!"
	password := page.Locator(`form[action="/api/v1/dashboard/profile/password"]`)
	fill(t, password.Locator(`[name="currentPassword"]`), account.Password)
	fill(t, password.Locator(`[name="newPassword"]`), nextPassword)
	fill(t, password.Locator(`[name="confirmPassword"]`), nextPassword)
	click(t, byRole(password, *playwright.AriaRoleButton, "Change password and sign out"))
	waitForPath(t, page, func(u *url.URL) bool { return u.Path == "/login" })

	request, err := http.NewRequestWithContext(t.Context(), http.MethodPost, baseURL+"/mcp", strings.NewReader(`{}`))
	must(t, err)
	request.Header.Set("Authorization", "Bearer "+secret)
	response, err := http.DefaultClient.Do(request)
	must(t, err)
	response.Body.Close()
	if response.StatusCode != http.StatusUnauthorized {
		t.Errorf("POST /mcp with a token issued before the password change = %d, want 401", response.StatusCode)
	}
	if _, err := session.CallTool(t.Context(), &mcp.CallToolParams{Name: "get_profile", Arguments: map[string]any{}}); err == nil {
		t.Error("a session using a token issued before the password change must be rejected")
	}
}

// TestMCPProfileToolsReadAndUpdateTheSignedInUser covers the profile tools
// and checks that credential ceremonies are not exposed.
func TestMCPProfileToolsReadAndUpdateTheSignedInUser(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	account := newAccount("mcp-profile")
	registerAndOnboard(t, page, account)
	session := connectMCP(t, createAccessToken(t, page, journeyPassword, "profile tools"))

	names := toolNames(t, session)
	for _, hidden := range []string{"create_access_token", "change_password", "change_email", "enroll_mfa", "register_passkey"} {
		if slices.Contains(names, hidden) {
			t.Errorf("credential ceremony %s must stay browser-only", hidden)
		}
	}

	type profile struct {
		Name, Email, Phone, Locale, Role string
		Permissions                      []string
		AccessTokens                     []struct{ ID, Name string }
	}
	var before profile
	callTool(t, session, "get_profile", nil, &before)
	if before.Email != account.Email || before.Name != account.DisplayName || before.Role != "owner" || len(before.Permissions) == 0 {
		t.Fatalf("get_profile = %+v, want the owner %s", before, account.Email)
	}
	if len(before.AccessTokens) != 1 || before.AccessTokens[0].Name != "profile tools" {
		t.Fatalf("get_profile access tokens = %+v, want the profile tools token", before.AccessTokens)
	}

	callTool(t, session, "update_profile", map[string]any{"name": "Renamed by MCP", "phone": "+55 11 5555-0100"}, nil)
	callTool(t, session, "update_profile", map[string]any{"name": "Renamed again"}, nil)
	callTool(t, session, "set_language", map[string]any{"locale": "pt-BR"}, nil)
	var after profile
	callTool(t, session, "get_profile", nil, &after)
	if after.Name != "Renamed again" || after.Phone != "+55 11 5555-0100" || after.Locale != "pt-BR" {
		t.Errorf("get_profile after updates = %+v, want the new name, kept phone, and pt-BR", after)
	}
	goTo(t, page, "/profile")
	visible(t, pageByRole(page, *playwright.AriaRoleHeading, "Renamed again"))

	if message := callToolError(t, session, "set_language", map[string]any{"locale": "fr"}); !strings.Contains(message, "available language") {
		t.Errorf("unsupported language error = %q", message)
	}
	if message := callToolError(t, session, "revoke_access_token", map[string]any{"tokenId": "34NASCCT3J041BRM5YKNZQ0FWB"}); !strings.Contains(message, "not found") {
		t.Errorf("unknown token revoke error = %q", message)
	}
	var listed struct{ AccessTokens []struct{ ID string } }
	callTool(t, session, "list_access_tokens", nil, &listed)
	if len(listed.AccessTokens) != 1 || listed.AccessTokens[0].ID != before.AccessTokens[0].ID {
		t.Errorf("list_access_tokens = %+v, want only %s", listed.AccessTokens, before.AccessTokens[0].ID)
	}
}

package e2e

import (
	"slices"
	"strings"
	"testing"
)

type authResource struct {
	ID, Name, Status, CallbackURL string
	Values                        map[string]string
	Actions                       []string
}

// TestMCPAuthenticationConsoleManagesInstance configures a project's ts-auth
// instance entirely through the MCP authentication tools.
func TestMCPAuthenticationConsoleManagesInstance(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	registerAndOnboard(t, page, newAccount("mcp-auth"))
	session := connectMCP(t, createAccessToken(t, page, journeyPassword, "auth assistant"))

	names := toolNames(t, session)
	for _, want := range []string{
		"auth_get_overview", "auth_describe_sections", "auth_list_resources", "auth_save_resource",
		"auth_delete_resource", "auth_run_resource_action", "auth_set_strategy", "auth_update_branding", "auth_list_audit_events",
	} {
		if !slices.Contains(names, want) {
			t.Errorf("tools/list is missing %s", want)
		}
	}

	var catalog struct {
		Sections []struct {
			Key    string
			Fields []struct{ Key string }
		}
		ConnectionProviders []struct{ Protocol string }
	}
	callTool(t, session, "auth_describe_sections", nil, &catalog)
	if len(catalog.Sections) != 11 || len(catalog.ConnectionProviders) != 5 {
		t.Fatalf("auth_describe_sections = %d sections, %d providers; want 11 and 5", len(catalog.Sections), len(catalog.ConnectionProviders))
	}

	save := func(section string, values map[string]any) authResource {
		t.Helper()
		var saved authResource
		callTool(t, session, "auth_save_resource", map[string]any{"section": section, "values": values}, &saved)
		return saved
	}
	org := save("organizations", map[string]any{"name": "Acme Inc.", "slug": "acme"})
	user := save("users", map[string]any{"name": "Ada Lovelace", "email": "ada@example.com"})
	membership := save("memberships", map[string]any{"organization": org.ID, "user": user.ID, "role": "admin"})
	if membership.Values["role"] != "admin" || membership.Name != "Ada Lovelace" {
		t.Errorf("membership = %+v, want Ada as admin", membership)
	}
	key := save("keys", map[string]any{"name": "Backend", "scopes": "users,sessions"})
	if key.Values["scopes"] != "users,sessions" {
		t.Errorf("management key scopes = %q, want users,sessions", key.Values["scopes"])
	}
	connection := save("connections", map[string]any{
		"protocol": "OIDC", "name": "Okta", "issuer": "https://login.example.com",
		"client_id": "client-1", "client_secret": "never-stored",
	})
	if _, stored := connection.Values["client_secret"]; stored || connection.Values["credential_configured"] != "true" || connection.CallbackURL == "" {
		t.Errorf("connection = %+v, want a configured credential, no stored secret, and a callback URL", connection)
	}

	if message := callToolError(t, session, "auth_save_resource", map[string]any{"section": "users", "values": map[string]any{"name": "No Email"}}); !strings.Contains(message, "Email address is required") {
		t.Errorf("user without email error = %q, want the required-field message", message)
	}
	if message := callToolError(t, session, "auth_list_resources", map[string]any{"section": "overview"}); !strings.Contains(message, "enum") {
		t.Errorf("non-list section error = %q, want the section enum to reject it", message)
	}
	if message := callToolError(t, session, "auth_list_resources", map[string]any{"section": "users", "projectId": "prj_missing"}); !strings.Contains(message, "not found") {
		t.Errorf("foreign project scope error = %q, want not found", message)
	}

	var updated authResource
	callTool(t, session, "auth_save_resource", map[string]any{"section": "users", "id": user.ID, "values": map[string]any{"name": "Ada King", "email": "ada@example.com"}}, &updated)
	if updated.Name != "Ada King" {
		t.Errorf("updated user = %+v, want the new name", updated)
	}
	var blocked authResource
	callTool(t, session, "auth_run_resource_action", map[string]any{"section": "users", "id": user.ID, "action": "block"}, &blocked)
	if blocked.Status != "Blocked" {
		t.Errorf("blocked user status = %q, want Blocked", blocked.Status)
	}
	callToolError(t, session, "auth_run_resource_action", map[string]any{"section": "users", "id": user.ID, "action": "rotate"})

	var users struct{ Resources []authResource }
	callTool(t, session, "auth_list_resources", map[string]any{"section": "users", "search": "ada@"}, &users)
	if len(users.Resources) != 1 || users.Resources[0].Status != "Blocked" || !slices.Contains(users.Resources[0].Actions, "block") {
		t.Errorf("searched users = %+v, want the blocked user with its block action", users.Resources)
	}

	var revoked struct{ Outcome string }
	callTool(t, session, "auth_delete_resource", map[string]any{"section": "keys", "id": key.ID}, &revoked)
	if revoked.Outcome != "revoked" {
		t.Errorf("deleting a management key = %q, want revoked", revoked.Outcome)
	}
	callTool(t, session, "auth_delete_resource", map[string]any{"section": "organizations", "id": org.ID}, nil)
	var memberships struct{ Resources []authResource }
	callTool(t, session, "auth_list_resources", map[string]any{"section": "memberships"}, &memberships)
	if len(memberships.Resources) != 0 {
		t.Errorf("memberships after deleting their organization = %+v, want none", memberships.Resources)
	}

	var strategies struct {
		Strategies []struct {
			Key     string
			Enabled bool
		}
	}
	callTool(t, session, "auth_set_strategy", map[string]any{"strategy": "totp", "enabled": false}, &strategies)
	for _, strategy := range strategies.Strategies {
		if (strategy.Key == "totp" || strategy.Key == "recovery_codes") && strategy.Enabled {
			t.Errorf("strategy %s stays enabled after disabling totp", strategy.Key)
		}
	}
	if message := callToolError(t, session, "auth_set_strategy", map[string]any{"strategy": "recovery_codes", "enabled": true}); !strings.Contains(message, "requires") {
		t.Errorf("enabling recovery codes without totp error = %q, want the requirement", message)
	}

	callToolError(t, session, "auth_update_branding", map[string]any{"name": "Acme", "themeColor": "blue"})
	callTool(t, session, "auth_update_branding", map[string]any{"name": "Acme Login", "themeColor": "#4F46E5"}, nil)
	var overview struct {
		InstanceName string
		Issuer       string
		Counts       map[string]int
		BlockedUsers int
		Branding     struct{ ThemeColor string }
	}
	callTool(t, session, "auth_get_overview", nil, &overview)
	if overview.InstanceName != "Acme Login" || overview.Branding.ThemeColor != "#4f46e5" || overview.BlockedUsers != 1 ||
		overview.Counts["users"] != 1 || !strings.HasPrefix(overview.Issuer, "https://gru.auth.terrascale.tech/") {
		t.Errorf("auth_get_overview = %+v, want the saved branding, one blocked user, and the issuer", overview)
	}

	var audit struct{ Events []struct{ Action string } }
	callTool(t, session, "auth_list_audit_events", nil, &audit)
	if !slices.ContainsFunc(audit.Events, func(e struct{ Action string }) bool { return e.Action == "auth.users.blocked" }) {
		t.Errorf("audit events = %+v, want auth.users.blocked", audit.Events)
	}
}

// TestMCPAuthenticationEmails brands and rewords a project's emails through
// the MCP email tools, then resets the wording.
func TestMCPAuthenticationEmails(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	account := newAccount("mcp-emails")
	registerAndOnboard(t, page, account)
	session := connectMCP(t, createAccessToken(t, page, journeyPassword, "email assistant"))

	type emails struct {
		Version  int
		Branding struct {
			BrandName    string `json:"brand_name"`
			PrimaryColor string `json:"primary_color"`
			LogoWidth    int    `json:"logo_width"`
		}
		Templates []struct {
			Kind       string
			Locale     string
			Overridden bool
		}
		Template struct {
			Overridden bool
			Variables  []string
			Fields     struct{ Subject, Body string }
			Defaults   struct{ Subject, Body string }
		}
	}
	var initial emails
	callTool(t, session, "auth_get_emails", map[string]any{"template": "magic_link", "locale": "pt-BR"}, &initial)
	if len(initial.Templates) != 14 || initial.Template.Overridden || !slices.Contains(initial.Template.Variables, "link") {
		t.Fatalf("auth_get_emails = %+v, want 7 emails in 2 languages with defaults", initial)
	}

	if message := callToolError(t, session, "auth_update_email_branding", map[string]any{"branding": map[string]any{"primary_color": "blue"}}); !strings.Contains(message, "hex") {
		t.Errorf("invalid color error = %q, want the hex rule", message)
	}
	var saved struct{ Version int }
	callTool(t, session, "auth_update_email_branding", map[string]any{"branding": map[string]any{"brand_name": "Acme", "primary_color": "#FF5500"}}, &saved)
	if saved.Version != initial.Version+1 {
		t.Errorf("version after branding = %d, want %d", saved.Version, initial.Version+1)
	}
	if message := callToolError(t, session, "auth_update_email_branding", map[string]any{"branding": map[string]any{"logo_url": "https://acme.example/logo.png"}}); !strings.Contains(message, "width") {
		t.Errorf("logo without size error = %q, want the size rule", message)
	}
	callTool(t, session, "auth_update_email_branding", map[string]any{"branding": map[string]any{"logo_url": "https://acme.example/logo.png", "logo_width": 120, "logo_height": 32}}, &saved)

	var reworded emails
	callTool(t, session, "auth_update_email_template", map[string]any{"template": "magic_link", "locale": "pt-BR", "fields": map[string]any{"subject": "Entre na {{ app_name }}"}}, &reworded)
	if !reworded.Template.Overridden || reworded.Template.Fields.Subject != "Entre na {{ app_name }}" || reworded.Template.Fields.Body != "" ||
		reworded.Branding.BrandName != "Acme" || reworded.Branding.PrimaryColor != "#ff5500" || reworded.Branding.LogoWidth != 120 {
		t.Errorf("after rewording = %+v, want only the subject overridden and the branding kept", reworded)
	}
	if message := callToolError(t, session, "auth_update_email_template", map[string]any{"template": "magic_link", "fields": map[string]any{"body": "{{ password }}"}}); !strings.Contains(message, "password") {
		t.Errorf("unknown variable error = %q, want it named", message)
	}

	var rendered struct{ Subject, HTML string }
	callTool(t, session, "auth_preview_email", map[string]any{"template": "magic_link", "locale": "pt-BR", "colorScheme": "dark"}, &rendered)
	if rendered.Subject != "Entre na Acme" || !strings.Contains(rendered.HTML, "Acme") {
		t.Errorf("auth_preview_email = %q, want the new subject and brand", rendered.Subject)
	}
	var sent struct{ To string }
	callTool(t, session, "auth_send_test_email", map[string]any{"template": "magic_link", "locale": "pt-BR"}, &sent)
	if sent.To != account.Email {
		t.Errorf("test email went to %q, want the caller %q", sent.To, account.Email)
	}

	var cleared emails
	callTool(t, session, "auth_update_email_branding", map[string]any{"branding": map[string]any{}, "clear": []string{"logo_url", "brand_name"}}, nil)
	callTool(t, session, "auth_update_email_template", map[string]any{"template": "magic_link", "locale": "pt-BR", "fields": map[string]any{"heading": "Olá"}, "clear": []string{"subject"}}, &cleared)
	if cleared.Branding.LogoWidth != 0 || cleared.Branding.BrandName != "" || cleared.Branding.PrimaryColor != "#ff5500" ||
		cleared.Template.Fields.Subject != "" || !cleared.Template.Overridden {
		t.Errorf("after clearing = %+v, want the logo, brand name, and subject back to their defaults and the rest kept", cleared)
	}
	if message := callToolError(t, session, "auth_update_email_template", map[string]any{"template": "magic_link", "clear": []string{"color"}}); !strings.Contains(message, "color") {
		t.Errorf("clearing an unknown field error = %q, want it named", message)
	}

	var reset emails
	callTool(t, session, "auth_reset_email_template", map[string]any{"template": "magic_link", "locale": "pt-BR"}, &reset)
	if reset.Template.Overridden || reset.Template.Fields.Subject != "" {
		t.Errorf("after reset = %+v, want the defaults", reset.Template)
	}

	var audit struct{ Events []struct{ Action string } }
	callTool(t, session, "auth_list_audit_events", nil, &audit)
	for _, want := range []string{"auth.emails.branding_saved", "auth.emails.template_saved", "auth.emails.test_sent", "auth.emails.template_reset"} {
		if !slices.ContainsFunc(audit.Events, func(e struct{ Action string }) bool { return e.Action == want }) {
			t.Errorf("audit events lack %s", want)
		}
	}
}

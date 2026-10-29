package tsauth

import (
	"bytes"
	"encoding/json"
	"errors"

	"terrascale.tech/sdk/internal/entityid"
)

// ProjectAuthSettings describes the project's hosted sign-in tenant. Provider
// secrets are never returned. Counts retain the producer's signed 64-bit range.
type ProjectAuthSettings struct {
	ProjectID     string                    `json:"project_id" ulid:"required"`
	Issuer        string                    `json:"issuer"`
	DiscoveryURL  string                    `json:"discovery_url"`
	SignInURL     string                    `json:"sign_in_url"`
	Methods       ProjectAuthMethods        `json:"methods"`
	Branding      *ProjectAuthBranding      `json:"branding"`
	Counts        ProjectAuthCounts         `json:"counts"`
	Providers     []ProjectAuthProvider     `json:"providers"`
	Configuration *ProjectAuthConfiguration `json:"configuration"`
}

// ProjectAuthMethods contains required response booleans, including false.
type ProjectAuthMethods struct {
	Passcode bool `json:"passcode"`
	Password bool `json:"password"`
	SignUp   bool `json:"sign_up"`
}

// ProjectAuthMethodsPatch leaves nil methods unchanged; false applies a change.
// The server requires at least one of passcode and password to remain available.
type ProjectAuthMethodsPatch struct {
	Passcode *bool `json:"passcode,omitempty"`
	Password *bool `json:"password,omitempty"`
	SignUp   *bool `json:"sign_up,omitempty"`
}

// ProjectAuthBranding replaces hosted-page and email branding when sent. Nil
// fields clear stored values; nil branding in a patch leaves branding unchanged.
type ProjectAuthBranding struct {
	Name            *string                 `json:"name"`
	LogoURL         *string                 `json:"logo_url"`
	ThemeColor      *string                 `json:"theme_color"`
	TitleSize       *int                    `json:"title_size"`
	DefaultTheme    *string                 `json:"default_theme"`
	FooterLinks     []ProjectAuthFooterLink `json:"footer_links"`
	BackgroundColor *string                 `json:"background_color"`
	TextColor       *string                 `json:"text_color"`
	EmailMessage    *string                 `json:"email_message"`
}

// ProjectAuthFooterLink is a labelled HTTPS link below the hosted sign-in card.
type ProjectAuthFooterLink struct {
	Label string `json:"label"`
	URL   string `json:"url"`
}

// ProjectAuthConfiguration replaces project redirect and session policy. Send
// an empty (non-nil) RedirectURLs slice for no additional redirect restriction.
// A zero SessionLifetimeSeconds omits the field for the server default (1209600);
// explicit values must be 300..2592000.
type ProjectAuthConfiguration struct {
	RedirectURLs           []string `json:"redirect_urls"`
	SessionLifetimeSeconds int      `json:"session_lifetime_seconds,omitempty"`
}

// ProjectAuthCounts counts tenant users and OAuth clients.
type ProjectAuthCounts struct {
	Users   int64 `json:"users"`
	Clients int64 `json:"clients"`
}

// ProjectAuthProvider contains public provider metadata and the callback to
// register with that provider. ClientID is a vendor ID, not a TerraScale ULID.
type ProjectAuthProvider struct {
	Provider    string  `json:"provider"`
	Configured  bool    `json:"configured"`
	ClientID    *string `json:"client_id"`
	Issuer      *string `json:"issuer"`
	TeamID      *string `json:"team_id"`
	KeyID       *string `json:"key_id"`
	CallbackURL string  `json:"callback_url"`
}

// ProjectAuthSettingsPatch leaves nil sections unchanged. Sent branding and
// configuration replace their sections; individual nil branding values clear.
type ProjectAuthSettingsPatch struct {
	Methods       *ProjectAuthMethodsPatch  `json:"methods,omitempty"`
	Branding      *ProjectAuthBranding      `json:"branding,omitempty"`
	Configuration *ProjectAuthConfiguration `json:"configuration,omitempty"`
}

// UnmarshalJSON rejects missing required results instead of inventing false
// booleans or zero counts. Unknown fields remain compatible with future servers.
func (s *ProjectAuthSettings) UnmarshalJSON(data []byte) error {
	invalid := errors.New("tsauth: invalid project auth settings response")
	type plain ProjectAuthSettings
	var next plain
	var raw map[string]json.RawMessage
	if json.Unmarshal(data, &next) != nil || json.Unmarshal(data, &raw) != nil {
		return invalid
	}
	required := func(data []byte, names ...string) bool {
		var fields map[string]json.RawMessage
		if json.Unmarshal(data, &fields) != nil {
			return false
		}
		for _, name := range names {
			v := bytes.TrimSpace(fields[name])
			if len(v) == 0 || bytes.Equal(v, []byte("null")) {
				return false
			}
		}
		return true
	}
	if !required(data, "project_id", "issuer", "discovery_url", "sign_in_url", "methods", "counts", "providers") || raw["branding"] == nil ||
		!required(raw["methods"], "passcode", "password", "sign_up") || !required(raw["counts"], "users", "clients") ||
		entityid.Check(next.ProjectID) != nil || next.Issuer == "" || next.DiscoveryURL == "" || next.SignInURL == "" || next.Counts.Users < 0 || next.Counts.Clients < 0 {
		return invalid
	}
	var providers []json.RawMessage
	if json.Unmarshal(raw["providers"], &providers) != nil {
		return invalid
	}
	for i, p := range providers {
		if !required(p, "provider", "configured", "callback_url") || next.Providers[i].Provider == "" || next.Providers[i].CallbackURL == "" {
			return invalid
		}
	}
	if next.Configuration != nil && (!required(raw["configuration"], "redirect_urls", "session_lifetime_seconds")) {
		return invalid
	}
	*s = ProjectAuthSettings(next)
	return nil
}

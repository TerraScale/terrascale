package profile

import (
	"context"
	"slices"
	"strings"
	"time"
	"unicode"
	"unicode/utf8"

	"terrascale.tech/dashboard/internal/core"
)

// locales are the interface languages a profile may choose.
var locales = []string{"en", "pt-BR"}

// Service owns the signed-in user's profile, language, and personal access
// tokens. HTTP handlers and MCP tools both call it.
type Service struct {
	deps core.Deps
}

type profileSummary struct {
	UserID               string            `json:"userId"`
	AccountID            string            `json:"accountId"`
	Name                 string            `json:"name"`
	Email                string            `json:"email"`
	Phone                string            `json:"phone"`
	Locale               string            `json:"locale"`
	ActiveOrganizationID string            `json:"activeOrganizationId" jsonschema:"Organization selected in the dashboard, used when a tool omits organizationId."`
	ActiveProjectID      string            `json:"activeProjectId" jsonschema:"Project selected in the dashboard, used when a tool omits projectId."`
	OrganizationID       string            `json:"organizationId" jsonschema:"Organization this call is scoped to; role and permissions apply to it."`
	ProjectID            string            `json:"projectId" jsonschema:"Project this call is scoped to."`
	OrganizationName     string            `json:"organizationName" jsonschema:"Name of the organization this call is scoped to."`
	Role                 string            `json:"role" jsonschema:"Role in the selected organization, empty when unavailable."`
	Permissions          []string          `json:"permissions" jsonschema:"Common resource capabilities in the selected organization and project."`
	MFAEnabled           bool              `json:"mfaEnabled" jsonschema:"Whether the demo authenticator record is enabled."`
	Passkeys             []passkeyView     `json:"passkeys"`
	AccessTokens         []accessTokenView `json:"accessTokens"`
}

type passkeyView struct {
	ID        string    `json:"id"`
	Name      string    `json:"name"`
	CreatedAt time.Time `json:"createdAt"`
}

// Profile returns the signed-in user's profile for the principal's scope.
func (s Service) Profile(ctx context.Context, p core.Principal) (profileSummary, error) {
	account, err := s.deps.Store.SnapshotAs(ctx, p)
	if err != nil {
		return profileSummary{}, err
	}
	role, _, permissions := profileAuthorization(account, p)
	view := profileSummary{
		UserID: account.User.ID, AccountID: account.ID, Name: account.User.Name, Email: account.User.Email,
		Phone: account.User.Phone, Locale: account.User.Locale,
		ActiveOrganizationID: account.ActiveOrganizationID, ActiveProjectID: account.ActiveProjectID,
		OrganizationID: p.OrganizationID, ProjectID: p.ProjectID,
		Role: role, Permissions: permissions, MFAEnabled: account.DemoSecurity.MFAEnabled,
		Passkeys: make([]passkeyView, 0, len(account.DemoSecurity.Passkeys)), AccessTokens: accessTokenViews(account.AccessTokens),
	}
	if permissions == nil {
		view.Permissions = []string{}
	}
	if org, ok := account.Organizations[p.OrganizationID]; ok {
		view.OrganizationName = org.Name
	}
	for _, passkey := range account.DemoSecurity.Passkeys {
		view.Passkeys = append(view.Passkeys, passkeyView{ID: passkey.ID, Name: passkey.Name, CreatedAt: passkey.CreatedAt})
	}
	return view, nil
}

// UpdateProfile saves the display name and phone number. An empty name keeps
// the current name, and a nil phone keeps the current number.
func (s Service) UpdateProfile(ctx context.Context, p core.Principal, name string, phone *string) error {
	name = strings.TrimSpace(name)
	if !utf8.ValidString(name) || strings.ContainsFunc(name, unicode.IsControl) {
		return profileError("Enter a valid name.")
	}
	if utf8.RuneCountInString(name) > 128 {
		return profileError("Use a name with 128 characters or fewer.")
	}
	if phone != nil {
		trimmed := strings.TrimSpace(*phone)
		if !utf8.ValidString(trimmed) || len(trimmed) > 64 || strings.ContainsFunc(trimmed, unicode.IsControl) {
			return profileError("Enter a valid phone number.")
		}
		phone = &trimmed
	}
	return s.deps.Store.UpdateAs(ctx, p, func(account *core.Account) error {
		if name != "" {
			account.User.Name = name
		}
		if phone != nil {
			account.User.Phone = *phone
		}
		return nil
	})
}

// SetLanguage saves the interface language.
func (s Service) SetLanguage(ctx context.Context, p core.Principal, locale string) error {
	if !slices.Contains(locales, locale) {
		return profileError("Choose an available language.")
	}
	return s.deps.Store.UpdateAs(ctx, p, func(account *core.Account) error {
		account.User.Locale = locale
		return nil
	})
}

// AccessTokens lists the user's live personal access tokens, newest first.
func (s Service) AccessTokens(ctx context.Context, p core.Principal) ([]accessTokenView, error) {
	account, err := s.deps.Store.SnapshotAs(ctx, p)
	if err != nil {
		return nil, err
	}
	return accessTokenViews(account.AccessTokens), nil
}

// CreateAccessToken issues a personal access token that expires after the
// chosen number of days, returning its one-time secret.
func (s Service) CreateAccessToken(ctx context.Context, p core.Principal, name, expiresInDays string) (createdAccessToken, error) {
	lifetime, err := accessTokenLifetime(expiresInDays)
	if err != nil {
		return createdAccessToken{}, err
	}
	token, secret, err := s.deps.Store.CreateAccessToken(ctx, p, name, lifetime)
	if err != nil {
		return createdAccessToken{}, err
	}
	view := accessTokenView{ID: token.ID, Name: token.Name, LastFour: token.LastFour, CreatedAt: token.CreatedAt, ExpiresAt: token.ExpiresAt}
	return createdAccessToken{Token: view, Secret: secret}, nil
}

// RevokeAccessToken deletes one of the user's personal access tokens.
func (s Service) RevokeAccessToken(ctx context.Context, p core.Principal, id string) error {
	return s.deps.Store.RevokeAccessToken(ctx, p, id)
}

// profileError is a validation failure whose text is shown to the user as is.
type profileError string

func (e profileError) Error() string        { return string(e) }
func (e profileError) Is(target error) bool { return target == core.ErrInvalid }

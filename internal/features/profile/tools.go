package profile

import (
	"context"

	"terrascale.tech/dashboard/internal/core"
)

type updateProfileInput struct {
	Name  string  `json:"name,omitempty" jsonschema:"Display name, up to 128 characters. Omit to keep the current name."`
	Phone *string `json:"phone,omitempty" jsonschema:"Phone number, up to 64 characters. Omit to keep the current number; send an empty string to clear it."`
}

type setLanguageInput struct {
	Locale string `json:"locale" jsonschema:"Interface language: en or pt-BR."`
}

type accessTokenInput struct {
	TokenID string `json:"tokenId" jsonschema:"Access token ID from list_access_tokens."`
}

type accessTokenList struct {
	AccessTokens []accessTokenView `json:"accessTokens"`
}

type saved struct {
	Saved bool `json:"saved"`
}

type revoked struct {
	Revoked bool `json:"revoked"`
}

// mountTools exposes the profile over MCP. Credential ceremonies (password,
// email, MFA, passkeys, and creating access tokens) stay browser-only so a
// leaked token cannot take over the account or mint more tokens.
func mountTools(tools *core.Tools, service Service) {
	core.AddTool(tools, core.ToolSpec{
		Name: "get_profile", Title: "Get profile", ReadOnly: true,
		Description: "Show your user profile: name, email, phone, language, role and capabilities in the selected organization, demo MFA and passkey records, and personal access tokens.",
	}, func(ctx context.Context, p core.Principal, _ core.Scope) (profileSummary, error) {
		return service.Profile(ctx, p)
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "update_profile", Title: "Update profile", Idempotent: true,
		Description: "Update your display name and phone number.",
	}, func(ctx context.Context, p core.Principal, in updateProfileInput) (saved, error) {
		err := service.UpdateProfile(ctx, p, in.Name, in.Phone)
		return saved{Saved: err == nil}, err
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "set_language", Title: "Set language", Idempotent: true,
		Description: "Choose the dashboard interface language (en or pt-BR).",
	}, func(ctx context.Context, p core.Principal, in setLanguageInput) (saved, error) {
		err := service.SetLanguage(ctx, p, in.Locale)
		return saved{Saved: err == nil}, err
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "list_access_tokens", Title: "List access tokens", ReadOnly: true,
		Description: "List your live personal access tokens for the MCP endpoint, newest first. Secrets are never shown again after creation.",
	}, func(ctx context.Context, p core.Principal, _ struct{}) (accessTokenList, error) {
		tokens, err := service.AccessTokens(ctx, p)
		return accessTokenList{AccessTokens: tokens}, err
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "revoke_access_token", Title: "Revoke access token", Destructive: true,
		Description: "Revoke one of your personal access tokens. Assistants using it lose access immediately, including this session if it uses that token.",
	}, func(ctx context.Context, p core.Principal, in accessTokenInput) (revoked, error) {
		err := service.RevokeAccessToken(ctx, p, in.TokenID)
		return revoked{Revoked: err == nil}, err
	})
}

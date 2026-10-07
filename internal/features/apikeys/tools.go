package apikeys

import (
	"context"

	"terrascale.tech/dashboard/internal/core"
)

type listCredentialsInput struct {
	core.Scope
	Kind       string `json:"kind,omitempty" jsonschema:"Filter by kind: api-key or m2m. Omit for both."`
	Status     string `json:"status,omitempty" jsonschema:"Filter by status: active or inactive (revoked or expired)."`
	DatabaseID string `json:"databaseId,omitempty" jsonschema:"Only credentials restricted to this database ID."`
	Search     string `json:"search,omitempty" jsonschema:"Case-insensitive match on the credential name or secret prefix."`
}

type credentialList struct {
	Credentials []credential `json:"credentials"`
}

type createCredentialInput struct {
	core.Scope
	Name       string   `json:"name" jsonschema:"Display name, 1-128 characters."`
	Scopes     []string `json:"scopes" jsonschema:"1-16 permission scopes such as database:read and database:write, or * for full access."`
	DatabaseID string   `json:"databaseId,omitempty" jsonschema:"Restrict the credential to one database ID. Omit for every database in the project."`
}

type credentialInput struct {
	core.Scope
	KeyID string `json:"keyId" jsonschema:"Credential ID from list_api_keys."`
}

func mountTools(tools *core.Tools, service Service) {
	core.AddTool(tools, core.ToolSpec{
		Name: "list_api_keys", Title: "List API keys", ReadOnly: true,
		Description: "List a project's API keys and machine-to-machine (M2M) clients without their secrets.",
	}, func(ctx context.Context, p core.Principal, in listCredentialsInput) (credentialList, error) {
		if in.Kind != "" && in.Kind != kindAPIKey && in.Kind != kindM2M {
			return credentialList{}, core.Invalid("kind must be api-key or m2m")
		}
		if in.Status != "" && in.Status != "active" && in.Status != "inactive" {
			return credentialList{}, core.Invalid("status must be active or inactive")
		}
		list, err := service.List(ctx, p, credentialFilter{Kind: in.Kind, Status: in.Status, DatabaseID: in.DatabaseID, Search: in.Search})
		return credentialList{Credentials: list}, err
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "create_api_key", Title: "Create API key",
		Description: "Issue a project API key for the TerraScale SDKs. Returns the secret once; store it securely. Requires an owner or admin role.",
	}, func(ctx context.Context, p core.Principal, in createCredentialInput) (ProjectAPIKeySecret, error) {
		return service.Create(ctx, p, kindAPIKey, CreateProjectAPIKeyRequest{Name: in.Name, Scopes: in.Scopes, DatabaseID: in.DatabaseID})
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "create_m2m_client", Title: "Create M2M client",
		Description: "Issue a machine-to-machine client credential for a project. Returns the secret once; store it securely. Requires an owner or admin role.",
	}, func(ctx context.Context, p core.Principal, in createCredentialInput) (ProjectAPIKeySecret, error) {
		return service.Create(ctx, p, kindM2M, CreateProjectAPIKeyRequest{Name: in.Name, Scopes: in.Scopes, DatabaseID: in.DatabaseID})
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "rotate_api_key", Title: "Rotate API key", Destructive: true,
		Description: "Replace an active API key's or M2M client's secret. The old secret stops working immediately; the new one is returned once.",
	}, func(ctx context.Context, p core.Principal, in credentialInput) (ProjectAPIKeySecret, error) {
		return service.Rotate(ctx, p, in.KeyID, "")
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "revoke_api_key", Title: "Revoke API key", Destructive: true, Idempotent: true,
		Description: "Permanently revoke an API key or M2M client so its secret no longer authenticates.",
	}, func(ctx context.Context, p core.Principal, in credentialInput) (ProjectAPIKeyRevocation, error) {
		return service.Revoke(ctx, p, in.KeyID, "")
	})
}

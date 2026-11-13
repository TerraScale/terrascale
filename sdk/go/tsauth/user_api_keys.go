package tsauth

import (
	"context"
	"net/http"
	"terrascale.tech/sdk/internal/entityid"
	"time"
)

type UserAPIKey struct {
	ID        string     `json:"id"`
	UserID    string     `json:"user_id"`
	Name      string     `json:"name"`
	Prefix    string     `json:"prefix"`
	Scopes    []string   `json:"scopes"`
	CreatedAt time.Time  `json:"created_at"`
	ExpiresAt *time.Time `json:"expires_at"`
	Key       string     `json:"key,omitempty"`
}

func (UserAPIKey) String() string     { return "UserAPIKey{secret:[REDACTED]}" }
func (v UserAPIKey) GoString() string { return v.String() }

type CreateUserAPIKey struct {
	Name      string     `json:"name"`
	Scopes    []string   `json:"scopes"`
	ExpiresAt *time.Time `json:"expires_at,omitempty"`
}
type UserAPIKeyPage struct {
	Items []UserAPIKey `json:"items"`
}
type UserAPIKeyPolicy struct {
	AllowedScopes []string `json:"allowed_scopes"`
}
type UserAPIKeyVerification struct {
	Valid     bool       `json:"valid"`
	UserID    *string    `json:"user_id"`
	Scopes    []string   `json:"scopes"`
	KeyID     *string    `json:"key_id"`
	ExpiresAt *time.Time `json:"expires_at"`
}

func (u *User) CreateUserAPIKey(ctx context.Context, project, user string, request CreateUserAPIKey) (*UserAPIKey, error) {
	if err := entityid.Check(user); err != nil {
		return nil, err
	}
	scoped, err := u.projectDirectoryUser(project)
	if err != nil {
		return nil, err
	}
	return call[UserAPIKey](ctx, scoped, http.MethodPost, []string{"users", user, "api-keys"}, nil, request)
}
func (u *User) ListUserAPIKeys(ctx context.Context, project, user string) (*UserAPIKeyPage, error) {
	if err := entityid.Check(user); err != nil {
		return nil, err
	}
	scoped, err := u.projectDirectoryUser(project)
	if err != nil {
		return nil, err
	}
	return call[UserAPIKeyPage](ctx, scoped, http.MethodGet, []string{"users", user, "api-keys"}, nil, nil)
}
func (u *User) RevokeUserAPIKey(ctx context.Context, project, user, key string) error {
	if err := entityid.Check(user); err != nil {
		return err
	}
	if err := entityid.Check(key); err != nil {
		return err
	}
	scoped, err := u.projectDirectoryUser(project)
	if err != nil {
		return err
	}
	return scoped.none(ctx, http.MethodDelete, []string{"users", user, "api-keys", key}, nil, nil)
}
func (u *User) VerifyUserAPIKey(ctx context.Context, project, key string) (*UserAPIKeyVerification, error) {
	scoped, err := u.projectDirectoryUser(project)
	if err != nil {
		return nil, err
	}
	body := struct {
		Key string `json:"key"`
	}{Key: key}
	return call[UserAPIKeyVerification](ctx, scoped, http.MethodPost, []string{"api-keys", "verify"}, nil, body)
}
func (u *User) SetUserAPIKeyPolicy(ctx context.Context, project string, request UserAPIKeyPolicy) (*UserAPIKeyPolicy, error) {
	scoped, err := u.projectDirectoryUser(project)
	if err != nil {
		return nil, err
	}
	return call[UserAPIKeyPolicy](ctx, scoped, http.MethodPut, []string{"user-api-key-policy"}, nil, request)
}

func (*UserAPIKey) requiredAuthMembers() string             { return "id user_id name prefix scopes created_at" }
func (*UserAPIKeyPage) requiredAuthMembers() string         { return "items" }
func (*UserAPIKeyVerification) requiredAuthMembers() string { return "valid" }
func (*UserAPIKeyPolicy) requiredAuthMembers() string       { return "allowed_scopes" }

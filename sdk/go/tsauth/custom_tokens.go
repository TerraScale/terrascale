package tsauth

import (
	"context"
	"net/http"

	"terrascale.tech/sdk/internal/entityid"
)

type CustomTokenRequest struct {
	UserID   string         `json:"user_id"`
	Database string         `json:"database,omitempty"`
	Claims   map[string]any `json:"claims,omitempty"`
}

type CustomToken struct {
	AccessToken string `json:"access_token"`
	TokenType   string `json:"token_type"`
	ExpiresIn   int64  `json:"expires_in"`
}

func (CustomToken) String() string               { return "CustomToken{AccessToken:[REDACTED]}" }
func (v CustomToken) GoString() string           { return v.String() }
func (*CustomToken) requiredAuthMembers() string { return "access_token token_type expires_in" }

func (u *User) MintCustomToken(ctx context.Context, project string, request CustomTokenRequest) (*CustomToken, error) {
	if err := entityid.Check(request.UserID); err != nil {
		return nil, err
	}
	if request.Database != "" {
		if err := entityid.Check(request.Database); err != nil {
			return nil, err
		}
	}
	scoped, err := u.projectDirectoryUser(project)
	if err != nil {
		return nil, err
	}
	out, err := call[CustomToken](ctx, scoped, http.MethodPost, []string{"custom-tokens"}, nil, request)
	if err != nil {
		return nil, err
	}
	if out.AccessToken == "" || out.TokenType != "Bearer" || out.ExpiresIn < 1 || out.ExpiresIn > 300 {
		return nil, &ResponseContractError{}
	}
	return out, nil
}

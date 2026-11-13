package tsauth

import (
	"context"
	"encoding/json"
	"net/http"

	"terrascale.tech/sdk/internal/entityid"
)

type UserProfile struct {
	FirstName       *string        `json:"first_name,omitempty"`
	LastName        *string        `json:"last_name,omitempty"`
	ImageURL        *string        `json:"image_url,omitempty"`
	ExternalID      *string        `json:"external_id,omitempty"`
	PublicMetadata  map[string]any `json:"public_metadata,omitempty"`
	PrivateMetadata map[string]any `json:"private_metadata,omitempty"`
	UnsafeMetadata  map[string]any `json:"unsafe_metadata,omitempty"`
}

type CreateProjectUserRequest struct {
	UserProfile
	Email    string  `json:"email"`
	Password *string `json:"password,omitempty"`
}

type UpdateProjectUserRequest struct {
	FirstName       json.RawMessage `json:"first_name,omitempty"`
	LastName        json.RawMessage `json:"last_name,omitempty"`
	ImageURL        json.RawMessage `json:"image_url,omitempty"`
	ExternalID      json.RawMessage `json:"external_id,omitempty"`
	PublicMetadata  map[string]any  `json:"public_metadata,omitempty"`
	PrivateMetadata map[string]any  `json:"private_metadata,omitempty"`
	UnsafeMetadata  map[string]any  `json:"unsafe_metadata,omitempty"`
}

type ImportProjectUser struct {
	UserProfile
	Email         string  `json:"email"`
	EmailVerified bool    `json:"email_verified"`
	PasswordHash  *string `json:"password_hash,omitempty"`
}

type ImportProjectUsersRequest struct {
	Users []ImportProjectUser `json:"users"`
}

type ImportedProjectUser struct {
	Index int         `json:"index"`
	User  ProjectUser `json:"user"`
}

type UserImportError struct {
	Index   int    `json:"index"`
	Code    string `json:"code"`
	Message string `json:"message"`
}

type ImportProjectUsersResult struct {
	Items  []ImportedProjectUser `json:"items"`
	Errors []UserImportError     `json:"errors"`
}

func (u *User) CreateProjectUser(ctx context.Context, project string, request CreateProjectUserRequest) (*ProjectUser, error) {
	scoped, err := u.projectDirectoryUser(project)
	if err != nil {
		return nil, err
	}
	return call[ProjectUser](ctx, scoped, http.MethodPost, []string{"users"}, nil, request)
}

func (u *User) UpdateProjectUser(ctx context.Context, project, user string, request UpdateProjectUserRequest) (*ProjectUser, error) {
	if err := entityid.Check(user); err != nil {
		return nil, err
	}
	scoped, err := u.projectDirectoryUser(project)
	if err != nil {
		return nil, err
	}
	return call[ProjectUser](ctx, scoped, http.MethodPatch, []string{"users", user}, nil, request)
}

func (u *User) ImportProjectUsers(ctx context.Context, project string, request ImportProjectUsersRequest) (*ImportProjectUsersResult, error) {
	scoped, err := u.projectDirectoryUser(project)
	if err != nil {
		return nil, err
	}
	return call[ImportProjectUsersResult](ctx, scoped, http.MethodPost, []string{"users", "import"}, nil, request)
}

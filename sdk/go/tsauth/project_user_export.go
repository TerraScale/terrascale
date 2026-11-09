package tsauth

import (
	"context"
	"errors"
	"net/http"
	"time"
)

type ProjectLinkedProvider struct {
	Provider string `json:"provider"`
	ClientID string `json:"client_id"`
	Subject  string `json:"subject"`
}

type ProjectDirectoryUser struct {
	ID              string                  `json:"id" ulid:"required"`
	Email           string                  `json:"email"`
	EmailVerified   bool                    `json:"email_verified"`
	CreatedAt       time.Time               `json:"created_at"`
	Disabled        bool                    `json:"disabled"`
	LinkedProviders []ProjectLinkedProvider `json:"linked_providers"`
}

func (*ProjectDirectoryUser) requiredAuthMembers() string {
	return "id email email_verified created_at disabled linked_providers"
}

func (v *ProjectDirectoryUser) UnmarshalJSON(data []byte) error {
	type plain ProjectDirectoryUser
	var next plain
	if err := decodeProjectDirectory(data, &next, v.requiredAuthMembers(), false); err != nil {
		return err
	}
	*v = ProjectDirectoryUser(next)
	return nil
}

type ProjectUserExportPage struct {
	Items         []ProjectDirectoryUser `json:"items"`
	Next          *string                `json:"next"`
	CreatedBefore time.Time              `json:"created_before"`
}

func (*ProjectUserExportPage) requiredAuthMembers() string {
	return "items created_before"
}

func (v *ProjectUserExportPage) UnmarshalJSON(data []byte) error {
	type plain ProjectUserExportPage
	var next plain
	if err := decodeProjectDirectory(data, &next, v.requiredAuthMembers(), true); err != nil {
		return err
	}
	*v = ProjectUserExportPage(next)
	return nil
}

func (u *User) ExportProjectUsers(ctx context.Context, project string, limit int, after string) (*ProjectUserExportPage, error) {
	if len(after) > 8192 {
		return nil, errors.New("tsauth: export cursor must be at most 8192 characters")
	}
	query, err := projectDirectoryQuery(limit, after)
	if err != nil {
		return nil, err
	}
	var out ProjectUserExportPage
	err = u.projectDirectoryDo(ctx, project, http.MethodGet, []string{"users", "export"}, query, &out)
	if err != nil {
		return nil, err
	}
	return &out, nil
}

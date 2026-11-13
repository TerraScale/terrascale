package tsauth

import (
	"context"
	"net/http"
	"time"

	"terrascale.tech/sdk/internal/entityid"
)

type ProjectRestrictions struct {
	Allowlist             []string `json:"allowlist"`
	Blocklist             []string `json:"blocklist"`
	BlockDisposableEmails bool     `json:"block_disposable_emails"`
	BlockEmailAliases     bool     `json:"block_email_aliases"`
}

type ProjectInvitation struct {
	ID          string    `json:"id" ulid:"required"`
	Email       string    `json:"email"`
	RedirectURL *string   `json:"redirect_url"`
	CreatedAt   time.Time `json:"created_at"`
	ExpiresAt   time.Time `json:"expires_at"`
	Status      string    `json:"status"`
}

type CreateProjectInvitation struct {
	Email       string  `json:"email"`
	RedirectURL *string `json:"redirect_url,omitempty"`
}

type ProjectInvitationPage struct {
	Items []ProjectInvitation `json:"items"`
	Next  *string             `json:"next"`
}

type ProjectWaitlistEntry struct {
	ID        string    `json:"id" ulid:"required"`
	ProjectID string    `json:"project_id" ulid:"required"`
	Email     string    `json:"email"`
	CreatedAt time.Time `json:"created_at"`
	Status    string    `json:"status"`
}

type ProjectWaitlistPage struct {
	Items []ProjectWaitlistEntry `json:"items"`
	Next  *string                `json:"next"`
}

func (u *User) CreateProjectInvitation(ctx context.Context, project string, invitation CreateProjectInvitation) (*ProjectInvitation, error) {
	scoped, err := u.projectDirectoryUser(project)
	if err != nil {
		return nil, err
	}
	var out ProjectInvitation
	if err := scoped.do(ctx, http.MethodPost, []string{"invitations"}, nil, invitation, &out); err != nil {
		return nil, err
	}
	return &out, nil
}

func (u *User) ProjectInvitations(ctx context.Context, project string, limit int, after string) (*ProjectInvitationPage, error) {
	query, err := projectDirectoryQuery(limit, after)
	if err != nil {
		return nil, err
	}
	var out ProjectInvitationPage
	if err := u.projectDirectoryDo(ctx, project, http.MethodGet, []string{"invitations"}, query, &out); err != nil {
		return nil, err
	}
	return &out, nil
}

func (u *User) RevokeProjectInvitation(ctx context.Context, project, invitation string) error {
	if err := entityid.Check(invitation); err != nil {
		return err
	}
	scoped, err := u.projectDirectoryUser(project)
	if err != nil {
		return err
	}
	return scoped.none(ctx, http.MethodDelete, []string{"invitations", invitation}, nil, nil)
}

func (u *User) ProjectWaitlist(ctx context.Context, project string, limit int, after string) (*ProjectWaitlistPage, error) {
	query, err := projectDirectoryQuery(limit, after)
	if err != nil {
		return nil, err
	}
	var out ProjectWaitlistPage
	if err := u.projectDirectoryDo(ctx, project, http.MethodGet, []string{"waitlist"}, query, &out); err != nil {
		return nil, err
	}
	return &out, nil
}

func (u *User) ApproveProjectWaitlist(ctx context.Context, project, entry string) error {
	if err := entityid.Check(entry); err != nil {
		return err
	}
	scoped, err := u.projectDirectoryUser(project)
	if err != nil {
		return err
	}
	return scoped.none(ctx, http.MethodPost, []string{"waitlist", entry, "approve"}, nil, nil)
}

func (u *User) RejectProjectWaitlist(ctx context.Context, project, entry string) error {
	if err := entityid.Check(entry); err != nil {
		return err
	}
	scoped, err := u.projectDirectoryUser(project)
	if err != nil {
		return err
	}
	return scoped.none(ctx, http.MethodPost, []string{"waitlist", entry, "reject"}, nil, nil)
}

func (*ProjectInvitation) requiredAuthMembers() string {
	return "id email created_at expires_at status"
}

func (v *ProjectInvitation) UnmarshalJSON(data []byte) error {
	type plain ProjectInvitation
	var next plain
	if err := decodeProjectDirectory(data, &next, v.requiredAuthMembers(), false); err != nil {
		return err
	}
	*v = ProjectInvitation(next)
	return nil
}

func (*ProjectInvitationPage) requiredAuthMembers() string { return "items" }

func (v *ProjectInvitationPage) UnmarshalJSON(data []byte) error {
	type plain ProjectInvitationPage
	var next plain
	if err := decodeProjectDirectory(data, &next, v.requiredAuthMembers(), true); err != nil {
		return err
	}
	*v = ProjectInvitationPage(next)
	return nil
}

func (*ProjectWaitlistEntry) requiredAuthMembers() string {
	return "id project_id email created_at status"
}

func (v *ProjectWaitlistEntry) UnmarshalJSON(data []byte) error {
	type plain ProjectWaitlistEntry
	var next plain
	if err := decodeProjectDirectory(data, &next, v.requiredAuthMembers(), false); err != nil {
		return err
	}
	*v = ProjectWaitlistEntry(next)
	return nil
}

func (*ProjectWaitlistPage) requiredAuthMembers() string { return "items" }

func (v *ProjectWaitlistPage) UnmarshalJSON(data []byte) error {
	type plain ProjectWaitlistPage
	var next plain
	if err := decodeProjectDirectory(data, &next, v.requiredAuthMembers(), true); err != nil {
		return err
	}
	*v = ProjectWaitlistPage(next)
	return nil
}

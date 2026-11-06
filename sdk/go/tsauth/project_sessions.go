package tsauth

import (
	"context"
	"net/http"
	"time"

	"terrascale.tech/sdk/internal/entityid"
)

// ProjectSession is a live session of a project's end user: unrevoked,
// unexpired and of an enabled user at the current epoch. ExpiresAt is the
// earlier of the session's issued expiry and the project's lifetime policy.
type ProjectSession struct {
	ID        string    `json:"id" ulid:"required"`
	UserID    string    `json:"user_id" ulid:"required"`
	Email     string    `json:"email"`
	CreatedAt time.Time `json:"created_at"`
	ExpiresAt time.Time `json:"expires_at"`
}

func (*ProjectSession) requiredAuthMembers() string { return "id user_id email created_at expires_at" }

// UnmarshalJSON rejects absent required members, including expires_at:"".
func (v *ProjectSession) UnmarshalJSON(data []byte) error {
	type plain ProjectSession
	var next plain
	if err := decodeProjectDirectory(data, &next, v.requiredAuthMembers(), false); err != nil {
		return err
	}
	*v = ProjectSession(next)
	return nil
}

// ProjectSessionPage preserves the opaque next cursor; nil means no next page.
type ProjectSessionPage struct {
	Items []ProjectSession `json:"items"`
	Next  *string          `json:"next"`
}

func (*ProjectSessionPage) requiredAuthMembers() string { return "items" }

// UnmarshalJSON requires items and the nullable next member.
func (v *ProjectSessionPage) UnmarshalJSON(data []byte) error {
	type plain ProjectSessionPage
	var next plain
	if err := decodeProjectDirectory(data, &next, v.requiredAuthMembers(), true); err != nil {
		return err
	}
	*v = ProjectSessionPage(next)
	return nil
}

// ProjectSessionsOptions narrows and pages the listing. Limit is 1..100; zero
// uses the server default of 50. After is the previous page's opaque Next
// cursor. User, when set, must be a canonical end-user ULID and lists only
// that user's live sessions.
type ProjectSessionsOptions struct {
	User  string
	Limit int
	After string
}

// ProjectSessions lists live sessions of the project's end users using a
// terrascale sign-in token and the viewer project role.
func (u *User) ProjectSessions(ctx context.Context, project string, options ProjectSessionsOptions) (*ProjectSessionPage, error) {
	query, err := projectDirectoryQuery(options.Limit, options.After)
	if err != nil {
		return nil, err
	}
	if options.User != "" {
		if err := entityid.Check(options.User); err != nil {
			return nil, err
		}
		query.Set("user", options.User)
	}
	var out ProjectSessionPage
	if err := u.projectDirectoryDo(ctx, project, http.MethodGet, []string{"sessions"}, query, &out); err != nil {
		return nil, err
	}
	return &out, nil
}

// RevokeProjectSession signs one live session out using the admin project
// role. The empty 204 answer is enforced; a revoked session answers 204 again.
// Sent once: an uncertain outcome is reconciled through ProjectSessions.
func (u *User) RevokeProjectSession(ctx context.Context, project, session string) error {
	if err := entityid.Check(session); err != nil {
		return err
	}
	return u.projectDirectoryDo(ctx, project, http.MethodDelete, []string{"sessions", session}, nil, nil)
}

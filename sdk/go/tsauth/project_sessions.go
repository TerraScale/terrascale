package tsauth

import (
	"context"
	"net/http"
	"time"

	"terrascale.tech/sdk/internal/entityid"
)

// ProjectSession is one live session of the project's hosted sign-in tenant:
// unrevoked, of an enabled user, within both its issued expiry and the current
// project lifetime, with the earlier effective expiry returned. Revoked,
// expired, disabled-user and project-lifetime-passed sessions never appear.
type ProjectSession struct {
	ID        string    `json:"id" ulid:"required"`
	UserID    string    `json:"user_id" ulid:"required"`
	Email     string    `json:"email"`
	CreatedAt time.Time `json:"created_at"`
	ExpiresAt time.Time `json:"expires_at"`
}

func (*ProjectSession) requiredAuthMembers() string { return "id user_id email created_at expires_at" }

// UnmarshalJSON rejects absent required members.
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

// ProjectSessionListOptions filters live sessions. User narrows the list to
// one user's sessions; empty lists every user's. Limit is 1..100; zero uses
// the server default of 50. After is the previous page's opaque Next cursor.
type ProjectSessionListOptions struct {
	User  string
	Limit int
	After string
}

// ProjectSessions lists the project's live sessions using a terrascale
// sign-in token and viewer role (GET /v1/{project}/sessions). An invisible
// project answers 404. No project key or end-user token is accepted.
func (u *User) ProjectSessions(ctx context.Context, project string, options ProjectSessionListOptions) (*ProjectSessionPage, error) {
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

// RevokeProjectSession signs one session out (DELETE
// /v1/{project}/sessions/{session}, admin). It never retries the mutation: a
// revoked session answers 204 again, so reconcile through ProjectSessions
// before repeating an uncertain request.
func (u *User) RevokeProjectSession(ctx context.Context, project, session string) error {
	if err := entityid.Check(session); err != nil {
		return err
	}
	return u.projectDirectoryDo(ctx, project, http.MethodDelete, []string{"sessions", session}, nil, nil)
}

func (u *User) SignOutProjectUser(ctx context.Context, project, user string) error {
	if err := entityid.Check(user); err != nil {
		return err
	}
	return u.projectDirectoryDo(ctx, project, http.MethodPost, []string{"users", user, "sign-out"}, nil, nil)
}

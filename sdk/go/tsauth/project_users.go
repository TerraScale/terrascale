package tsauth

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"io"
	"mime"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"time"

	"terrascale.tech/sdk/internal/entityid"
)

// ProjectUser is an end user of a project's hosted sign-in tenant.
type ProjectUser struct {
	ID        string    `json:"id" ulid:"required"`
	Email     string    `json:"email"`
	CreatedAt time.Time `json:"created_at"`
	Disabled  bool      `json:"disabled"`
}

func (*ProjectUser) requiredAuthMembers() string { return "id email created_at disabled" }

// UnmarshalJSON rejects absent required members, including disabled:false.
func (v *ProjectUser) UnmarshalJSON(data []byte) error {
	type plain ProjectUser
	var next plain
	if err := decodeProjectDirectory(data, &next, v.requiredAuthMembers(), false); err != nil {
		return err
	}
	*v = ProjectUser(next)
	return nil
}

// ProjectUserPage preserves the opaque next cursor; nil means no next page.
type ProjectUserPage struct {
	Items []ProjectUser `json:"items"`
	Next  *string       `json:"next"`
}

func (*ProjectUserPage) requiredAuthMembers() string { return "items" }

// UnmarshalJSON requires items and the nullable next member.
func (v *ProjectUserPage) UnmarshalJSON(data []byte) error {
	type plain ProjectUserPage
	var next plain
	if err := decodeProjectDirectory(data, &next, v.requiredAuthMembers(), true); err != nil {
		return err
	}
	*v = ProjectUserPage(next)
	return nil
}

func decodeProjectDirectory(data []byte, out any, members string, page bool) error {
	invalid := errors.New("tsauth: invalid project directory response")
	var fields map[string]json.RawMessage
	if json.Unmarshal(data, &fields) != nil {
		return invalid
	}
	for _, name := range strings.Fields(members) {
		value := bytes.TrimSpace(fields[name])
		if len(value) == 0 || bytes.Equal(value, []byte("null")) {
			return invalid
		}
	}
	if page && fields["next"] == nil {
		return invalid
	}
	if json.Unmarshal(data, out) != nil || entityid.Model(out) != nil {
		return invalid
	}
	return nil
}

// ProjectUserListOptions searches email substrings. Limit is 1..100; zero uses
// the server default of 50. After is the previous page's opaque Next cursor.
type ProjectUserListOptions struct {
	Query string
	Limit int
	After string
}

// ProjectUsers lists end users using a terrascale sign-in token and viewer role.
// An invisible project answers 404. No project key or end-user token is accepted.
func (u *User) ProjectUsers(ctx context.Context, project string, options ProjectUserListOptions) (*ProjectUserPage, error) {
	query, err := projectDirectoryQuery(options.Limit, options.After)
	if err != nil {
		return nil, err
	}
	if options.Query != "" {
		query.Set("q", options.Query)
	}
	var out ProjectUserPage
	err = u.projectDirectoryDo(ctx, project, http.MethodGet, []string{"users"}, query, &out)
	if err != nil {
		return nil, err
	}
	return &out, nil
}

// ProjectUser reads one canonical end-user ULID; requires the viewer role.
func (u *User) ProjectUser(ctx context.Context, project, user string) (*ProjectUser, error) {
	if err := entityid.Check(user); err != nil {
		return nil, err
	}
	var out ProjectUser
	if err := u.projectDirectoryDo(ctx, project, http.MethodGet, []string{"users", user}, nil, &out); err != nil {
		return nil, err
	}
	return &out, nil
}

func projectDirectoryQuery(limit int, after string) (url.Values, error) {
	if limit < 0 || limit > 100 {
		return nil, errors.New("tsauth: project directory limit must be 1..100 or zero for 50")
	}
	query := url.Values{}
	if limit != 0 {
		query.Set("limit", strconv.Itoa(limit))
	}
	if after != "" {
		query.Set("after", after)
	}
	return query, nil
}

func (u *User) projectDirectoryUser(project string) (*User, error) {
	if err := entityid.Check(project); err != nil {
		return nil, err
	}
	if u == nil || u.c == nil {
		return nil, errors.New("tsauth: project directory requires a signed-in user")
	}
	client := *u.c
	client.base = client.base[:strings.LastIndex(client.base, "/")+1] + project
	return &User{c: &client, token: u.token}, nil
}

// Enforce directory success status/media in addition to shared body bounds.
// The copied client retains caller ownership and redirect policy.
type projectDirectoryTransport struct {
	base    http.RoundTripper
	success int
}

func (t projectDirectoryTransport) RoundTrip(req *http.Request) (*http.Response, error) {
	res, err := t.base.RoundTrip(req)
	if err != nil {
		return nil, err
	}
	defer res.Body.Close()
	data, err := io.ReadAll(io.LimitReader(res.Body, (8<<20)+1))
	if err != nil {
		return nil, err
	}
	invalid := errors.New("tsauth: invalid project directory response")
	if len(data) > 8<<20 {
		return nil, invalid
	}
	if res.StatusCode >= 200 && res.StatusCode <= 299 {
		media, _, e := mime.ParseMediaType(res.Header.Get("Content-Type"))
		if res.StatusCode != t.success || (t.success != 204 && (e != nil || media != "application/json")) || (t.success == 204 && len(data) != 0) {
			return nil, invalid
		}
	}
	res.Body = io.NopCloser(bytes.NewReader(data))
	return res, nil
}

func (u *User) projectDirectoryDo(ctx context.Context, project, method string, path []string, query url.Values, out any) error {
	ctx, cancel := context.WithTimeout(ctx, 30*time.Second)
	defer cancel()
	if err := ctx.Err(); err != nil {
		return err
	}
	scoped, err := u.projectDirectoryUser(project)
	if err != nil {
		return err
	}
	hc := *scoped.c.hc
	base := hc.Transport
	if base == nil {
		base = http.DefaultTransport
	}
	success := http.StatusOK
	if method == http.MethodDelete {
		success = http.StatusNoContent
	}
	hc.Transport = projectDirectoryTransport{base: base, success: success}
	scoped.c.hc = &hc
	err = scoped.do(ctx, method, path, query, nil, out)
	if ctx.Err() != nil {
		return ctx.Err()
	}
	if err == nil {
		return nil
	}
	var api *APIError
	if errors.As(err, &api) {
		safe := *api
		safe.Message = "project directory request refused"
		switch safe.Code {
		case ErrInvalidRequest, ErrUnauthorized, ErrForbiddenRole, ErrNotFound, ErrTemporarilyUnavailable, "mfa_required", "internal_error":
		default:
			safe.Code = ErrorCode("http_" + strconv.Itoa(safe.Status))
		}
		return &safe
	}
	return errors.New("tsauth: project directory request failed or response does not match the contract")
}

// DisableProjectUser requires the admin project role. Disabling stops sign-in
// and invalidates every existing session and token immediately. Sent once.
func (u *User) DisableProjectUser(ctx context.Context, project, user string) (*ProjectUser, error) {
	return u.projectUserState(ctx, project, user, "disable")
}

// EnableProjectUser requires the admin project role. New sign-ins are allowed;
// sessions invalidated by disabling remain ended. Sent once, without replay.
func (u *User) EnableProjectUser(ctx context.Context, project, user string) (*ProjectUser, error) {
	return u.projectUserState(ctx, project, user, "enable")
}

func (u *User) projectUserState(ctx context.Context, project, user, action string) (*ProjectUser, error) {
	if err := entityid.Check(user); err != nil {
		return nil, err
	}
	var out ProjectUser
	if err := u.projectDirectoryDo(ctx, project, http.MethodPost, []string{"users", user, action}, nil, &out); err != nil {
		return nil, err
	}
	return &out, nil
}

// DeleteProjectUser requires the admin project role, releases the user's email,
// and invalidates its sessions and tokens. A successful response is empty 204.
// Sent once: reconcile an uncertain outcome before manually trying again.
func (u *User) DeleteProjectUser(ctx context.Context, project, user string) error {
	if err := entityid.Check(user); err != nil {
		return err
	}
	return u.projectDirectoryDo(ctx, project, http.MethodDelete, []string{"users", user}, nil, nil)
}

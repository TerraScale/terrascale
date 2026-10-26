package tsauth

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"mime"
	"net/http"
	"strconv"
	"strings"
	"time"
	"unicode"

	"terrascale.tech/sdk/internal/entityid"
)

// ProjectSettings reads a project's auth tenant using the platform user's
// sign-in token and viewer role. A project the caller cannot see answers 404.
func (u *User) ProjectSettings(ctx context.Context, project string) (*ProjectAuthSettings, error) {
	return u.projectAuthRequest(ctx, project, http.MethodGet, "settings", nil)
}

// UpdateProjectSettings requires the admin project role. Nil sections/methods
// stay unchanged; sent branding/configuration replace those sections. Validation
// of branding, redirect policy and remaining sign-in methods belongs to the server.
// The request is sent once, without automatic mutation replay.
func (u *User) UpdateProjectSettings(ctx context.Context, project string, patch ProjectAuthSettingsPatch) (*ProjectAuthSettings, error) {
	return u.projectAuthRequest(ctx, project, http.MethodPut, "settings", patch)
}

// projectAuthRequest is private to settings/providers. In particular provider
// client_id/team_id/key_id are vendor values, not entity IDs; do/sendJSON's
// generic request-ID validator is inappropriate for this contract.
func (u *User) projectAuthRequest(ctx context.Context, project, method, suffix string, in any) (*ProjectAuthSettings, error) {
	ctx, cancel := context.WithTimeout(ctx, 30*time.Second)
	defer cancel()
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	if err := entityid.Check(project); err != nil {
		return nil, fmt.Errorf("tsauth: project: %w", err)
	}
	if u == nil || u.c == nil || u.token == "" || strings.ContainsFunc(u.token, func(r rune) bool { return unicode.IsSpace(r) || unicode.IsControl(r) }) {
		return nil, errors.New("tsauth: project auth requires a sign-in access token without whitespace")
	}
	var body io.Reader
	if in != nil {
		data, err := json.Marshal(in)
		if err != nil {
			return nil, errors.New("tsauth: invalid project auth request")
		}
		if len(data) > 16<<10 {
			return nil, errors.New("tsauth: project auth request exceeds the 16 KiB producer limit")
		}
		body = bytes.NewReader(data)
	}
	// Replace the configured tenant while retaining any reverse-proxy prefix.
	target := u.c.base[:strings.LastIndex(u.c.base, "/")+1] + project + "/" + suffix
	req, err := http.NewRequestWithContext(ctx, method, target, body)
	if err != nil {
		return nil, errors.New("tsauth: invalid project auth URL")
	}
	req.Header.Set("Authorization", "Bearer "+u.token)
	req.Header.Set("Accept", "application/json")
	if in != nil {
		req.Header.Set("Content-Type", "application/json")
	}
	res, err := u.c.hc.Do(req)
	if err != nil {
		if ctx.Err() != nil {
			return nil, ctx.Err()
		}
		// Transports and server diagnostics may echo write-only provider secrets.
		return nil, errors.New("tsauth: project auth transport failed")
	}
	defer res.Body.Close()
	data, err := io.ReadAll(io.LimitReader(res.Body, (8<<20)+1))
	if ctx.Err() != nil {
		return nil, ctx.Err()
	}
	if err != nil {
		return nil, errors.New("tsauth: could not read project auth response")
	}
	if res.StatusCode < 200 || res.StatusCode > 299 {
		e := &APIError{Status: res.StatusCode, Code: ErrorCode("http_" + strconv.Itoa(res.StatusCode)), Message: "project auth request refused", RetryAfter: retryAfter(res.Header.Get("Retry-After"))}
		var env struct {
			Error struct {
				Code ErrorCode `json:"code"`
			} `json:"error"`
		}
		if len(data) <= 8<<20 && json.Unmarshal(data, &env) == nil {
			switch env.Error.Code {
			case ErrInvalidRequest, ErrUnauthorized, ErrForbiddenRole, ErrNotFound, ErrTemporarilyUnavailable, "mfa_required", "internal_error":
				e.Code = env.Error.Code
			}
		}
		return nil, e
	}
	invalid := errors.New("tsauth: invalid project auth settings response")
	media, _, err := mime.ParseMediaType(res.Header.Get("Content-Type"))
	if err != nil || media != "application/json" || res.StatusCode != http.StatusOK || len(data) > 8<<20 {
		return nil, invalid
	}
	var result ProjectAuthSettings
	if json.Unmarshal(data, &result) != nil || result.ProjectID != project {
		return nil, invalid
	}
	return &result, nil
}

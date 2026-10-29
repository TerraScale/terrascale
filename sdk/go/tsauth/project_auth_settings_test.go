package tsauth_test

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"strings"
	"testing"
	"time"

	"terrascale.tech/sdk/tsauth"
)

const projectAuthID = "01ARZ3NDEKTSV4RRFFQ69G5FAV"

func projectAuthUser(t *testing.T, rt roundTripFunc) *tsauth.User {
	t.Helper()
	c, err := tsauth.New(tsauth.Config{Issuer: "https://auth.example.test/proxy/terrascale", HTTPClient: &http.Client{Transport: rt}})
	if err != nil {
		t.Fatal(err)
	}
	return c.WithToken("platform-sign-in-token")
}

func projectAuthResponse(status int, body string) *http.Response {
	return &http.Response{StatusCode: status, Header: http.Header{"Content-Type": []string{"application/json; charset=utf-8"}}, Body: io.NopCloser(strings.NewReader(body))}
}

func TestProjectAuthSettingsRequests(t *testing.T) {
	fixture := string(projectAuthFixture(t))
	calls := 0
	u := projectAuthUser(t, func(r *http.Request) (*http.Response, error) {
		calls++
		if r.URL.Path != "/proxy/v1/"+projectAuthID+"/settings" || r.URL.RawQuery != "" || r.Header.Get("Authorization") != "Bearer platform-sign-in-token" || r.Header.Get("Accept") != "application/json" {
			t.Errorf("wrong project route/authorization: %s", r.URL.Path)
		}
		deadline, ok := r.Context().Deadline()
		if !ok || time.Until(deadline) <= 0 || time.Until(deadline) > 30*time.Second {
			t.Error("missing fixed deadline")
		}
		if calls == 1 {
			if r.Method != http.MethodGet || r.Body != nil {
				t.Error("invalid settings GET")
			}
		} else {
			if r.Method != http.MethodPut || r.Header.Get("Content-Type") != "application/json" {
				t.Error("invalid settings PUT")
			}
			data, _ := io.ReadAll(r.Body)
			var m map[string]json.RawMessage
			if json.Unmarshal(data, &m) != nil || string(m["methods"]) != `{"password":false}` || !strings.Contains(string(m["branding"]), `"name":null`) || string(m["configuration"]) != `{"redirect_urls":[],"session_lifetime_seconds":1209600}` {
				t.Errorf("incorrect patch: %s", data)
			}
		}
		return projectAuthResponse(200, fixture), nil
	})
	if _, err := u.ProjectSettings(t.Context(), projectAuthID); err != nil {
		t.Fatal(err)
	}
	off := false
	result, err := u.UpdateProjectSettings(t.Context(), projectAuthID, tsauth.ProjectAuthSettingsPatch{Methods: &tsauth.ProjectAuthMethodsPatch{Password: &off}, Branding: &tsauth.ProjectAuthBranding{}, Configuration: &tsauth.ProjectAuthConfiguration{RedirectURLs: []string{}, SessionLifetimeSeconds: 1209600}})
	if err != nil || result == nil || result.ProjectID != projectAuthID || calls != 2 {
		t.Fatalf("settings roundtrip: %v calls=%d", err, calls)
	}
}

func TestProjectAuthSettingsRefusalsAreNotReplayed(t *testing.T) {
	for _, code := range []tsauth.ErrorCode{tsauth.ErrUnauthorized, tsauth.ErrForbiddenRole, tsauth.ErrNotFound, tsauth.ErrTemporarilyUnavailable, "mfa_required", "internal_error"} {
		u := projectAuthUser(t, func(r *http.Request) (*http.Response, error) {
			return projectAuthResponse(403, `{"error":{"code":"`+string(code)+`","message":"redacted"}}`), nil
		})
		_, err := u.ProjectSettings(t.Context(), projectAuthID)
		if !errors.Is(err, code) {
			t.Fatalf("lost producer error code %s: %v", code, err)
		}
	}

	for _, status := range []int{400, 401, 403, 404, 429, 503, 307} {
		calls := 0
		u := projectAuthUser(t, func(r *http.Request) (*http.Response, error) {
			calls++
			res := projectAuthResponse(status, `{"error":{"code":"forbidden_role","message":"untrusted diagnostics"}}`)
			res.Header.Set("Retry-After", "7")
			return res, nil
		})
		_, err := u.UpdateProjectSettings(t.Context(), projectAuthID, tsauth.ProjectAuthSettingsPatch{})
		var api *tsauth.APIError
		if !errors.As(err, &api) || api.Status != status || !errors.Is(err, tsauth.ErrForbiddenRole) || api.RetryAfter != 7*time.Second || calls != 1 || strings.Contains(err.Error(), "untrusted") {
			t.Fatalf("status %d: err=%v calls=%d", status, err, calls)
		}
	}
}

func TestProjectAuthSettingsProducerRequestBound(t *testing.T) {
	name := ""
	patch := tsauth.ProjectAuthSettingsPatch{Branding: &tsauth.ProjectAuthBranding{Name: &name}}
	empty, err := json.Marshal(patch)
	if err != nil {
		t.Fatal(err)
	}
	calls := 0
	u := projectAuthUser(t, func(r *http.Request) (*http.Response, error) {
		calls++
		data, err := io.ReadAll(r.Body)
		if err != nil || len(data) != 16<<10 {
			t.Errorf("request bound: bytes=%d err=%v", len(data), err)
		}
		return projectAuthResponse(200, string(projectAuthFixture(t))), nil
	})
	name = strings.Repeat("x", (16<<10)-len(empty))
	if _, err := u.UpdateProjectSettings(t.Context(), projectAuthID, patch); err != nil {
		t.Fatal(err)
	}
	name += "x"
	if _, err := u.UpdateProjectSettings(t.Context(), projectAuthID, patch); err == nil || calls != 1 {
		t.Fatalf("oversize body sent: calls=%d err=%v", calls, err)
	}
}

type projectAuthBody struct {
	io.Reader
	closed bool
	read   int
}

func (b *projectAuthBody) Read(p []byte) (int, error) {
	n, e := b.Reader.Read(p)
	b.read += n
	return n, e
}
func (b *projectAuthBody) Close() error { b.closed = true; return nil }

func TestProjectAuthSettingsResultBounds(t *testing.T) {
	fixture := string(projectAuthFixture(t))
	for _, tc := range []struct {
		body, media string
		status      int
		valid       bool
	}{
		{fixture, "application/json", 200, true},
		{fixture + strings.Repeat(" ", (8<<20)-len(fixture)), "application/json", 200, true},
		{fixture + strings.Repeat(" ", (8<<20)+1-len(fixture)), "application/json", 200, false},
		{fixture, "text/html", 200, false},
		{fixture, "application/json", 201, false},
		{strings.ReplaceAll(fixture, projectAuthID, "01ARZ3NDEKTSV4RRFFQ69G5FAW"), "application/json", 200, false},
		{`null`, "application/json", 200, false},
		{`{"methods":{"passcode":false}}`, "application/json", 200, false},
	} {
		body := &projectAuthBody{Reader: strings.NewReader(tc.body)}
		u := projectAuthUser(t, func(r *http.Request) (*http.Response, error) {
			res := projectAuthResponse(tc.status, "")
			res.Header.Set("Content-Type", tc.media)
			res.Body = body
			return res, nil
		})
		_, err := u.ProjectSettings(t.Context(), projectAuthID)
		if (err == nil) != tc.valid || !body.closed || body.read > (8<<20)+1 {
			t.Fatalf("valid=%v err=%v closed=%v read=%d", tc.valid, err, body.closed, body.read)
		}
	}
}

func TestProjectAuthSettingsValidationAndCancellation(t *testing.T) {
	calls := 0
	u := projectAuthUser(t, func(r *http.Request) (*http.Response, error) { calls++; return nil, errors.New("unexpected transport") })
	for _, id := range []string{"legacy", strings.ToLower(projectAuthID), projectAuthID + "/settings"} {
		if _, err := u.ProjectSettings(t.Context(), id); err == nil {
			t.Fatal("accepted invalid project ID")
		}
	}
	ctx, cancel := context.WithCancel(t.Context())
	cancel()
	if _, err := u.ProjectSettings(ctx, projectAuthID); !errors.Is(err, context.Canceled) {
		t.Fatalf("pre-cancellation: %v", err)
	}
	if calls != 0 {
		t.Fatal("invalid/canceled work sent a request")
	}
	started := make(chan struct{})
	u = projectAuthUser(t, func(r *http.Request) (*http.Response, error) {
		close(started)
		<-r.Context().Done()
		return nil, r.Context().Err()
	})
	ctx, cancel = context.WithCancel(t.Context())
	finished := make(chan error, 1)
	go func() { _, err := u.ProjectSettings(ctx, projectAuthID); finished <- err }()
	<-started
	cancel()
	select {
	case err := <-finished:
		if !errors.Is(err, context.Canceled) {
			t.Fatalf("in-flight cancellation: %v", err)
		}
	case <-time.After(time.Second):
		t.Fatal("cancellation blocked")
	}
	c, err := tsauth.New(tsauth.Config{})
	if err != nil {
		t.Fatal(err)
	}
	for _, token := range []string{"", "bad token", "bad\nvalue"} {
		if _, err := c.WithToken(token).ProjectSettings(t.Context(), projectAuthID); err == nil {
			t.Fatal("accepted invalid bearer")
		}
	}
}

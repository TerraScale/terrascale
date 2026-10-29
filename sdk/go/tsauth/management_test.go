package tsauth_test

// The management client against ts-auth's pinned platform contract,
// testdata/platform-api.openapi.json: docs/platform-api.openapi.yaml of ts-auth
// a57af854 (branch cos-hold/ts-598-identity-owner, PR #46) as JSON. ts-auth
// serves none of it yet, so a contract server answers each request: it
// matches the operation, validates the JSON body against the request schema,
// and answers a value generated from the response schema. Every operation
// but the token endpoint must be called.

import (
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"slices"
	"strings"
	"sync"
	"testing"
	"time"

	"terrascale.tech/sdk/tsauth"
)

type spec struct {
	Paths      map[string]map[string]operation `json:"-"`
	Components struct {
		Schemas map[string]schema `json:"schemas"`
	} `json:"components"`
}

type operation struct {
	OperationID string `json:"operationId"`
	RequestBody *struct {
		Content map[string]struct {
			Schema schema `json:"schema"`
		} `json:"content"`
	} `json:"requestBody"`
	Responses map[string]struct {
		Content map[string]struct {
			Schema schema `json:"schema"`
		} `json:"content"`
	} `json:"responses"`
}

type schema struct {
	Ref                  string            `json:"$ref"`
	Type                 any               `json:"type"`
	Enum                 []any             `json:"enum"`
	Const                any               `json:"const"`
	Required             []string          `json:"required"`
	Properties           map[string]schema `json:"properties"`
	Items                *schema           `json:"items"`
	OneOf                []schema          `json:"oneOf"`
	AdditionalProperties *bool             `json:"additionalProperties"`
}

func loadSpec(t *testing.T) *spec {
	data, err := os.ReadFile("testdata/platform-api.openapi.json")
	if err != nil {
		t.Fatal(err)
	}
	var s spec
	var raw struct {
		Paths map[string]map[string]json.RawMessage `json:"paths"`
	}
	if err := errors.Join(json.Unmarshal(data, &s), json.Unmarshal(data, &raw)); err != nil {
		t.Fatal(err)
	}
	s.Paths = map[string]map[string]operation{}
	for path, items := range raw.Paths {
		s.Paths[path] = map[string]operation{}
		for method, item := range items {
			if method == "parameters" {
				continue
			}
			var op operation
			if err := json.Unmarshal(item, &op); err != nil {
				t.Fatal(err)
			}
			s.Paths[path][method] = op
		}
	}
	return &s
}

func (s *spec) resolve(sc schema) schema {
	for sc.Ref != "" {
		sc = s.Components.Schemas[strings.TrimPrefix(sc.Ref, "#/components/schemas/")]
	}
	return sc
}

func types(sc schema) []string {
	switch v := sc.Type.(type) {
	case string:
		return []string{v}
	case []any:
		var out []string
		for _, t := range v {
			out = append(out, t.(string))
		}
		return out
	}
	return nil
}

// generate returns a value of sc with every required property.
func (s *spec) generate(sc schema, name string) any {
	sc = s.resolve(sc)
	switch {
	case sc.Const != nil:
		return sc.Const
	case len(sc.Enum) > 0:
		return sc.Enum[0]
	case len(sc.OneOf) > 0:
		for _, o := range sc.OneOf {
			if !slices.Contains(types(o), "null") {
				return s.generate(o, name)
			}
		}
	}
	t := types(sc)
	switch {
	case slices.Contains(t, "object") || sc.Properties != nil:
		obj := map[string]any{}
		for _, p := range sc.Required {
			obj[p] = s.generate(sc.Properties[p], p)
		}
		return obj
	case slices.Contains(t, "array"):
		return []any{s.generate(*sc.Items, name)}
	case slices.Contains(t, "integer"):
		return 1
	case slices.Contains(t, "boolean"):
		return true
	}
	switch {
	case strings.HasSuffix(name, "_at"):
		return "2026-10-06T12:00:00Z"
	case name == "id" || strings.HasSuffix(name, "_id") || name == "created_by" || name == "invited_by" || name == "actor":
		return "01K6SY8000E008000000000001"
	}
	return name + "-value"
}

// validate reports where v does not satisfy sc.
func (s *spec) validate(sc schema, v any, at string) error {
	sc = s.resolve(sc)
	if len(sc.OneOf) > 0 {
		for _, o := range sc.OneOf {
			if s.validate(o, v, at) == nil {
				return nil
			}
		}
		return fmt.Errorf("%s: %v matches no oneOf branch", at, v)
	}
	if len(sc.Enum) > 0 && !slices.Contains(sc.Enum, v) {
		return fmt.Errorf("%s: %v is not one of %v", at, v, sc.Enum)
	}
	t := types(sc)
	switch v := v.(type) {
	case nil:
		if len(t) > 0 && !slices.Contains(t, "null") {
			return fmt.Errorf("%s: null", at)
		}
	case map[string]any:
		for _, p := range sc.Required {
			if _, ok := v[p]; !ok {
				return fmt.Errorf("%s: missing %s", at, p)
			}
		}
		for k, pv := range v {
			ps, ok := sc.Properties[k]
			if !ok {
				return fmt.Errorf("%s: unknown property %s", at, k)
			}
			if err := s.validate(ps, pv, at+"."+k); err != nil {
				return err
			}
		}
	case []any:
		for i, e := range v {
			if err := s.validate(*sc.Items, e, fmt.Sprintf("%s[%d]", at, i)); err != nil {
				return err
			}
		}
	case string:
		if len(t) > 0 && !slices.Contains(t, "string") {
			return fmt.Errorf("%s: string %q", at, v)
		}
	case float64:
		if len(t) > 0 && !slices.Contains(t, "integer") && !slices.Contains(t, "number") {
			return fmt.Errorf("%s: number %v", at, v)
		}
	}
	return nil
}

// match finds the operation of a request path, with {param} segments.
func (s *spec) match(method, path string) (string, *operation) {
	for tmpl, ops := range s.Paths {
		op, ok := ops[strings.ToLower(method)]
		ts, ps := strings.Split(tmpl, "/"), strings.Split(path, "/")
		if !ok || len(ts) != len(ps) {
			continue
		}
		matches := true
		for i := range ts {
			if !(strings.HasPrefix(ts[i], "{") && ps[i] != "") && ts[i] != ps[i] {
				matches = false
			}
		}
		if matches {
			return tmpl, &op
		}
	}
	return "", nil
}

func contractServer(t *testing.T, s *spec) (*httptest.Server, func() []string) {
	var mu sync.Mutex
	var called []string
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		_, op := s.match(r.Method, r.URL.EscapedPath())
		if op == nil {
			t.Errorf("no operation for %s %s", r.Method, r.URL.Path)
			w.WriteHeader(http.StatusNotFound)
			return
		}
		mu.Lock()
		called = append(called, op.OperationID)
		mu.Unlock()
		if r.Header.Get("Authorization") != "Bearer sign-in-token" {
			t.Errorf("%s: Authorization %q", op.OperationID, r.Header.Get("Authorization"))
		}
		body, _ := io.ReadAll(r.Body)
		if op.RequestBody != nil {
			var v any
			if err := json.Unmarshal(body, &v); err != nil {
				t.Errorf("%s: body %q: %v", op.OperationID, body, err)
			} else if err := s.validate(op.RequestBody.Content["application/json"].Schema, v, op.OperationID); err != nil {
				t.Errorf("request %v", err)
			}
		} else if len(body) > 0 {
			t.Errorf("%s: unexpected body %q", op.OperationID, body)
		}
		for _, code := range []string{"200", "201", "204"} {
			res, ok := op.Responses[code]
			if !ok {
				continue
			}
			var status int
			fmt.Sscan(code, &status)
			if c, ok := res.Content["application/json"]; ok {
				w.Header().Set("Content-Type", "application/json")
				w.WriteHeader(status)
				json.NewEncoder(w).Encode(s.generate(c.Schema, ""))
			} else {
				w.WriteHeader(status)
			}
			return
		}
		t.Errorf("%s: no success response", op.OperationID)
	}))
	t.Cleanup(srv.Close)
	return srv, func() []string {
		mu.Lock()
		defer mu.Unlock()
		return slices.Clone(called)
	}
}

func TestManagementContract(t *testing.T) {
	s := loadSpec(t)
	srv, called := contractServer(t, s)
	c, err := tsauth.New(tsauth.Config{Issuer: srv.URL + "/terrascale"})
	if err != nil {
		t.Fatal(err)
	}
	u := c.WithToken("sign-in-token")
	ctx := t.Context()
	const org, project, user = "01K6SY8000E008000000000001", "01K6SY8PVAFJ6SW3RT5CY4TQKF", "2F3GPKWPKB9HYRX7RA3CP3TKJZ"
	const key = "01ARZ3NDEKTSV4RRFFQ69G5FA8"
	expires := time.Date(2027, 1, 1, 0, 0, 0, 0, time.UTC)
	page := tsauth.PageOptions{Limit: 10, After: "cursor"}
	check := func(name string, ok bool, err error) {
		t.Helper()
		if err != nil || !ok {
			t.Errorf("%s: ok=%t err=%v", name, ok, err)
		}
	}
	errOnly := func(name string, err error) { t.Helper(); check(name, true, err) }

	o, err := u.CreateOrganization(ctx, "Acme")
	check("CreateOrganization", err == nil && o.ID != "" && !o.CreatedAt.IsZero() && o.Role == tsauth.RoleViewer, err)
	os, err := u.Organizations(ctx, page)
	check("Organizations", err == nil && len(os.Items) == 1 && os.Next != "", err)
	_, err = u.Organization(ctx, org)
	errOnly("Organization", err)
	_, err = u.RenameOrganization(ctx, org, "Acme Inc")
	errOnly("RenameOrganization", err)
	errOnly("DeleteOrganization", u.DeleteOrganization(ctx, org))

	ms, err := u.Members(ctx, org, page)
	check("Members", err == nil && ms.Items[0].Email != "" && !ms.Items[0].JoinedAt.IsZero(), err)
	_, err = u.SetMemberRole(ctx, org, user, tsauth.RoleAdmin)
	errOnly("SetMemberRole", err)
	errOnly("RemoveMember", u.RemoveMember(ctx, org, user))
	errOnly("Leave", u.Leave(ctx, org))

	inv, err := u.CreateInvitation(ctx, org, "", "ana@example.com", tsauth.RoleMember)
	check("CreateInvitation", err == nil && inv.Token != "" && inv.Invitation.Status == tsauth.InvitationPending, err)
	_, err = u.CreateInvitation(ctx, org, project, "bia@example.com", tsauth.RoleViewer)
	errOnly("CreateInvitation for a project", err)
	_, err = u.Invitations(ctx, org, project, page)
	errOnly("Invitations", err)
	errOnly("RevokeInvitation", u.RevokeInvitation(ctx, org, org))
	look, err := u.LookupInvitation(ctx, "tsi_token")
	check("LookupInvitation", err == nil && look.Organization.ID != "" && look.Organization.Name != "", err)
	acc, err := u.AcceptInvitation(ctx, "tsi_token")
	check("AcceptInvitation", err == nil && acc.OrganizationID != "", err)
	errOnly("DeclineInvitation", u.DeclineInvitation(ctx, "tsi_token"))

	_, err = u.StartOwnershipTransfer(ctx, org, user)
	errOnly("StartOwnershipTransfer", err)
	tr, err := u.OwnershipTransfer(ctx, org)
	check("OwnershipTransfer", err == nil && tr.ToUserID != "", err)
	errOnly("CancelOwnershipTransfer", u.CancelOwnershipTransfer(ctx, org))
	_, err = u.ConfirmOwnershipTransfer(ctx, org)
	errOnly("ConfirmOwnershipTransfer", err)

	p, err := u.CreateProject(ctx, org, "Production")
	check("CreateProject", err == nil && p.OrganizationID != "" && p.Issuer != "", err)
	_, err = u.Projects(ctx, org, page)
	errOnly("Projects", err)
	_, err = u.Project(ctx, org, project)
	errOnly("Project", err)
	_, err = u.RenameProject(ctx, org, project, "Prod")
	errOnly("RenameProject", err)
	errOnly("DeleteProject", u.DeleteProject(ctx, org, project))
	pm, err := u.ProjectMembers(ctx, org, project, page)
	check("ProjectMembers", err == nil && !pm.Items[0].GrantedAt.IsZero(), err)
	_, err = u.SetProjectMemberRole(ctx, org, project, user, tsauth.RoleAdmin)
	errOnly("SetProjectMemberRole", err)
	errOnly("RemoveProjectMember", u.RemoveProjectMember(ctx, org, project, user))

	for _, scope := range []string{"", project} {
		created, err := u.CreateAPIKey(ctx, org, scope, tsauth.APIKeyRequest{Name: "ci", Scopes: []string{tsauth.ScopeDocumentsRead}, ExpiresAt: &expires})
		check("CreateAPIKey "+scope, err == nil && created.Key != "" && created.APIKey.ID != "" && !created.APIKey.CreatedAt.IsZero(), err)
		_, err = u.APIKeys(ctx, org, scope, page)
		errOnly("APIKeys "+scope, err)
		_, err = u.APIKey(ctx, org, scope, key)
		errOnly("APIKey "+scope, err)
		_, err = u.RotateAPIKey(ctx, org, scope, key, time.Hour)
		errOnly("RotateAPIKey "+scope, err)
		errOnly("RevokeAPIKey "+scope, u.RevokeAPIKey(ctx, org, scope, key))
	}
	_, err = u.CreateAPIKey(ctx, org, project, tsauth.APIKeyRequest{Name: "db", Scopes: []string{tsauth.ScopeDocumentsWrite}, Database: "main"})
	errOnly("CreateAPIKey with a database", err)
	ev, err := u.AuditEvents(ctx, org, page)
	check("AuditEvents", err == nil && ev.Items[0].Action != "", err)

	got := called()
	for _, ops := range s.Paths {
		for _, op := range ops {
			if op.OperationID != "oauth_token" && !slices.Contains(got, op.OperationID) {
				t.Errorf("operation never called: %s", op.OperationID)
			}
		}
	}
}

func TestManagementErrors(t *testing.T) {
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(http.StatusConflict)
		io.WriteString(w, `{"error":{"code":"last_owner","message":"the last owner cannot leave"}}`)
	}))
	t.Cleanup(srv.Close)
	c, err := tsauth.New(tsauth.Config{Issuer: srv.URL + "/terrascale"})
	if err != nil {
		t.Fatal(err)
	}
	err = c.WithToken("t").Leave(t.Context(), "01ARZ3NDEKTSV4RRFFQ69G5FA6")
	var apiErr *tsauth.APIError
	if !errors.Is(err, tsauth.ErrLastOwner) || !errors.As(err, &apiErr) || apiErr.Status != http.StatusConflict || apiErr.Message == "" {
		t.Fatalf("error: %v", err)
	}
}

func TestManagementDecodeErrorsHideValues(t *testing.T) {
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusCreated)
		io.WriteString(w, `{"api_key":{"id":"01ARZ3NDEKTSV4RRFFQ69G5FA8"},"key":12345678901234567890}`)
	}))
	t.Cleanup(srv.Close)
	c, err := tsauth.New(tsauth.Config{Issuer: srv.URL + "/terrascale"})
	if err != nil {
		t.Fatal(err)
	}
	_, err = c.WithToken("t").CreateAPIKey(t.Context(), "01ARZ3NDEKTSV4RRFFQ69G5FA6", "", tsauth.APIKeyRequest{Name: "k", Scopes: []string{tsauth.ScopeDocumentsRead}})
	if err == nil || strings.Contains(err.Error(), "12345678901234567890") || errors.Unwrap(err) != nil {
		t.Fatalf("decode error: %v", err)
	}
}

func TestManagementRejectsLocally(t *testing.T) {
	c, err := tsauth.New(tsauth.Config{Issuer: "https://auth.example/terrascale", HTTPClient: &http.Client{Transport: roundTripFunc(func(r *http.Request) (*http.Response, error) {
		t.Errorf("request sent: %s", r.URL)
		return nil, errors.New("unexpected")
	})}})
	if err != nil {
		t.Fatal(err)
	}
	ctx := t.Context()
	for name, err := range map[string]error{
		"body project ID":  second(c.WithToken("t").CreateInvitation(ctx, "01ARZ3NDEKTSV4RRFFQ69G5FA6", "prj_legacy", "user@example.com", tsauth.RoleMember)),
		"body user ID":     second(c.WithToken("t").StartOwnershipTransfer(ctx, "01ARZ3NDEKTSV4RRFFQ69G5FA6", "usr_legacy")),
		"query project ID": second(c.WithToken("t").Invitations(ctx, "01ARZ3NDEKTSV4RRFFQ69G5FA6", "prj_legacy", tsauth.PageOptions{})),
		"empty token":      c.WithToken("").Leave(ctx, "01ARZ3NDEKTSV4RRFFQ69G5FA6"),
		"token with space": c.WithToken("a b").Leave(ctx, "01ARZ3NDEKTSV4RRFFQ69G5FA6"),
		"slash":            c.WithToken("t").Leave(ctx, "a/b"),
		"dot":              c.WithToken("t").Leave(ctx, ".."),
		"empty":            c.WithToken("t").Leave(ctx, ""),
		"org key database": second(c.WithToken("t").CreateAPIKey(ctx, "01ARZ3NDEKTSV4RRFFQ69G5FA6", "", tsauth.APIKeyRequest{Name: "k", Database: "main"})),
		"negative grace":   second(c.WithToken("t").RotateAPIKey(ctx, "01ARZ3NDEKTSV4RRFFQ69G5FA6", "", "01ARZ3NDEKTSV4RRFFQ69G5FA8", -500*time.Millisecond)),
		"grace over a day": second(c.WithToken("t").RotateAPIKey(ctx, "01ARZ3NDEKTSV4RRFFQ69G5FA6", "", "01ARZ3NDEKTSV4RRFFQ69G5FA8", 24*time.Hour+time.Second)),
	} {
		if err == nil {
			t.Errorf("%s: no error", name)
		}
	}
	for _, issuer := range []string{"https://auth.example", "https://auth.example/", "https://auth.example/terrascale?"} {
		if _, err := tsauth.New(tsauth.Config{Issuer: issuer}); err == nil {
			t.Errorf("issuer %q accepted", issuer)
		}
	}
}

type roundTripFunc func(*http.Request) (*http.Response, error)

func (f roundTripFunc) RoundTrip(r *http.Request) (*http.Response, error) { return f(r) }

func second[T any](_ T, err error) error { return err }

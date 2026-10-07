package v1_test

// Integration tests: every v1 client call against the pinned TerraBase
// release executable, started by terrabasetest (never a fake). One
// server serves every subtest; a recording transport checks the real traffic:
// every request is a route of the pinned OpenAPI and every client route of it
// is called, no request carries Idempotency-Key, and X-TerraBase-User-Id never
// accompanies an access token.
// Retries are tested by injecting faults in that transport in front of the
// real server.
//
// testdata/openapi.json is the pinned executable's /openapi/v1.json on the
// client listener; TestIntegration/PinnedOpenAPI fails when they differ. To
// re-pin after moving the pin in terrabasetest, run the test with
// TERRABASE_REPIN=1.

import (
	"bytes"
	"context"
	"crypto/rand"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"maps"
	"net/http"
	"os"
	"slices"
	"strings"
	"sync"
	"testing"
	"time"

	"terrascale.tech/sdk/terrabasetest"
	v1 "terrascale.tech/sdk/v1"
)

func TestIntegration(t *testing.T) {
	tb := terrabasetest.Start(t)
	rec := &recorder{base: tb.Client.Transport}
	hc := &http.Client{Transport: rec}
	t.Run("PinnedOpenAPI", func(t *testing.T) { testPinnedOpenAPI(t, tb) })
	t.Run("Management", func(t *testing.T) { testManagement(t, tb, hc) })
	t.Run("Documents", func(t *testing.T) { testDocuments(t, tb, hc) })
	t.Run("Retries", func(t *testing.T) { testRetries(t, tb, hc, rec) })
	rec.mu.Lock()
	defer rec.mu.Unlock()
	if len(rec.bad) > 0 {
		t.Errorf("bad requests:\n%s", strings.Join(rec.bad, "\n"))
	}
	routes := pinnedRoutes(loadOpenAPI(t))
	for _, route := range routes {
		called := slices.ContainsFunc(rec.seen, func(r string) bool { return routeMatches(route, r) })
		if missing := slices.Contains(notInClient, route); called == missing {
			t.Errorf("pinned route called=%t, but listed as not in the client=%t: %s", called, missing, route)
		}
	}
	for _, r := range rec.seen {
		if !slices.ContainsFunc(append(routes, adminRoutes...), func(route string) bool { return routeMatches(route, r) }) {
			t.Errorf("request outside the pinned OpenAPI: %s", r)
		}
	}
}

// recorder is the client's transport to the real server. It records each
// request as "METHOD /escaped/path", checks its headers, and injects an armed
// fault into one request.
type recorder struct {
	base http.RoundTripper
	mu   sync.Mutex
	seen []string
	bad  []string
	f    *fault
}

// fault answers the first armed-method request with status, body and extra
// header. With after, the request first reaches the server (a lost response).
type fault struct {
	method string
	status int
	body   string
	header http.Header
	err    error
	after  bool
	hits   int
}

func (r *recorder) RoundTrip(req *http.Request) (*http.Response, error) {
	route := req.Method + " " + req.URL.EscapedPath()
	r.mu.Lock()
	r.seen = append(r.seen, route)
	if req.Header.Get("Idempotency-Key") != "" {
		r.bad = append(r.bad, route+": Idempotency-Key")
	}
	// WithToken users never name a user; the access token does.
	if strings.Count(req.Header.Get("Authorization"), ".") == 2 && req.Header.Get("X-TerraBase-User-Id") != "" {
		r.bad = append(r.bad, route+": X-TerraBase-User-Id with an access token")
	}
	f := r.f
	inject := f != nil && f.method == req.Method
	if inject {
		f.hits++
		inject = f.hits == 1
	}
	r.mu.Unlock()
	if inject && f.after {
		res, err := r.base.RoundTrip(req)
		if err != nil {
			return nil, err
		}
		res.Body.Close()
	}
	if inject {
		if f.err != nil {
			return nil, f.err
		}
		header := http.Header{"Content-Type": {"application/json"}, "Retry-After": {"0"}}
		maps.Copy(header, f.header)
		return &http.Response{
			StatusCode: f.status, Status: http.StatusText(f.status), Proto: "HTTP/2.0", ProtoMajor: 2,
			Header:  header,
			Body:    io.NopCloser(strings.NewReader(f.body)),
			Request: req,
		}, nil
	}
	return r.base.RoundTrip(req)
}

// arm injects a fault into the next request with method and returns a func
// reporting how many requests with that method were sent since.
func (r *recorder) arm(f fault) func() int {
	r.mu.Lock()
	defer r.mu.Unlock()
	r.f = &f
	return func() int {
		r.mu.Lock()
		defer r.mu.Unlock()
		r.f = nil
		return f.hits
	}
}

func management(t *testing.T, tb *terrabasetest.Server, hc *http.Client) *v1.Management {
	t.Helper()
	m, err := v1.NewManagement(v1.Config{Resolver: v1.StaticResolver(tb.URL), HTTPClient: hc})
	if err != nil {
		t.Fatal(err)
	}
	return m
}

// user acts as the ts-auth user id, whose verified email is email (none when
// empty), with an access token of the server's issuer.
func user(t *testing.T, tb *terrabasetest.Server, m *v1.Management, id, email string) *v1.ManagementUser {
	t.Helper()
	return m.WithToken(tb.Token(t, id, email))
}

// must(f())(t) returns f's value and fails t on its error.
func must[T any](v T, err error) func(*testing.T) T {
	return func(t *testing.T) T {
		t.Helper()
		if err != nil {
			t.Fatal(err)
		}
		return v
	}
}

func wantCode(t *testing.T, err error, code v1.ErrorCode) {
	t.Helper()
	if !errors.Is(err, code) {
		t.Fatalf("got %v, want %s", err, code)
	}
}

func testPinnedOpenAPI(t *testing.T, tb *terrabasetest.Server) {
	res, err := tb.Client.Get(tb.URL + "/openapi/v1.json")
	if err != nil {
		t.Fatal(err)
	}
	defer res.Body.Close()
	raw, err := io.ReadAll(res.Body)
	if err != nil {
		t.Fatal(err)
	}
	// The TsAuth scheme names the issuer, whose port changes per server.
	raw = bytes.ReplaceAll(raw, []byte(tb.Issuer), []byte("https://issuer.test"))
	if os.Getenv("TERRABASE_REPIN") == "1" {
		if err := os.WriteFile("testdata/openapi.json", raw, 0o644); err != nil {
			t.Fatal(err)
		}
	}
	var live, pinned map[string]any
	if err := json.Unmarshal(raw, &live); err != nil {
		t.Fatal(err)
	}
	data, err := os.ReadFile("testdata/openapi.json")
	if err != nil {
		t.Fatal(err)
	}
	if err := json.Unmarshal(data, &pinned); err != nil {
		t.Fatal(err)
	}
	// servers names the listener's port; everything else must match.
	delete(live, "servers")
	delete(pinned, "servers")
	a, _ := json.Marshal(live)
	b, _ := json.Marshal(pinned)
	if !bytes.Equal(a, b) {
		t.Fatal("testdata/openapi.json differs from the pinned executable's /openapi/v1.json; re-pin it")
	}
}

func testManagement(t *testing.T, tb *terrabasetest.Server, hc *http.Client) {
	ctx := t.Context()
	m := management(t, tb, hc)
	ana, bia := user(t, tb, m, "user-ana", "ana@example.com"), user(t, tb, m, "user-bia", "bia@example.com")

	org := must(ana.CreateOrganization(ctx, "Acme"))(t)
	if org.Name != "Acme" || org.CreatedBy != "user-ana" || org.Plan != v1.PlanHobby || org.CreatedAt.IsZero() {
		t.Fatalf("organization: %+v", org)
	}
	_, err := ana.CreateOrganization(ctx, "acme")
	wantCode(t, err, v1.ErrConflict)
	if orgs := must(ana.Organizations(ctx, v1.PageOptions{Limit: 10}))(t); len(orgs.Items) != 1 || orgs.Next != "" {
		t.Fatalf("organizations: %+v", orgs)
	}
	_, err = bia.Organization(ctx, org.ID)
	wantCode(t, err, v1.ErrUnknownRoute) // a non-member cannot see it
	if got := must(ana.Organization(ctx, org.ID))(t); got.ID != org.ID {
		t.Fatalf("organization: %+v", got)
	}
	if got := must(ana.RenameOrganization(ctx, org.ID, "Acme Inc"))(t); got.Name != "Acme Inc" {
		t.Fatalf("rename: %+v", got)
	}
	if got := must(ana.SetPlan(ctx, org.ID, v1.PlanPro))(t); got.Plan != v1.PlanPro {
		t.Fatalf("plan: %+v", got)
	}
	_, err = ana.CurrentOrganization(ctx)
	wantCode(t, err, v1.ErrUnknownRoute)
	must(ana.SetCurrentOrganization(ctx, org.ID))(t)
	if got := must(ana.CurrentOrganization(ctx))(t); got.ID != org.ID {
		t.Fatalf("current: %+v", got)
	}

	project := must(ana.CreateProject(ctx, org.ID, "Production"))(t)
	if project.OrganizationID != org.ID {
		t.Fatalf("project: %+v", project)
	}
	if got := must(ana.Projects(ctx, org.ID, v1.PageOptions{}))(t); len(got.Items) != 1 {
		t.Fatalf("projects: %+v", got)
	}
	must(ana.Project(ctx, org.ID, project.ID))(t)
	if got := must(ana.RenameProject(ctx, org.ID, project.ID, "Prod"))(t); got.Name != "Prod" {
		t.Fatalf("rename project: %+v", got)
	}

	db := must(ana.CreateDatabase(ctx, org.ID, project.ID, "main"))(t)
	if db.ProjectID != project.ID || db.BranchedFrom != nil || db.Region == "" {
		t.Fatalf("database: %+v", db)
	}
	branch := must(ana.BranchDatabase(ctx, org.ID, project.ID, db.ID, "main-copy"))(t)
	if branch.BranchedFrom == nil || *branch.BranchedFrom != db.ID {
		t.Fatalf("branch: %+v", branch)
	}
	if got := must(ana.Databases(ctx, org.ID, project.ID, v1.PageOptions{Limit: 1}))(t); len(got.Items) != 1 || got.Next == "" {
		t.Fatalf("databases page 1: %+v", got)
	} else if got2 := must(ana.Databases(ctx, org.ID, project.ID, v1.PageOptions{After: got.Next}))(t); len(got2.Items) != 1 || got2.Items[0].ID == got.Items[0].ID {
		t.Fatalf("databases page 2: %+v", got2)
	}
	must(ana.Database(ctx, org.ID, project.ID, db.ID))(t)
	wantCode(t, ana.DeleteProject(ctx, org.ID, project.ID), v1.ErrConflict)
	if err := ana.DeleteDatabase(ctx, org.ID, project.ID, branch.ID); err != nil {
		t.Fatal(err)
	}
	_, err = ana.Database(ctx, org.ID, project.ID, branch.ID)
	wantCode(t, err, v1.ErrUnknownRoute)

	// Invitations, members and roles.
	inv := must(ana.CreateInvitation(ctx, org.ID, "bia@example.com", v1.RoleAdmin))(t)
	if inv.Token == "" || inv.Invitation.Role != v1.RoleAdmin || inv.Invitation.InvitedBy != "user-ana" {
		t.Fatalf("invitation: %+v", inv)
	}
	if got := must(ana.Invitations(ctx, org.ID, v1.PageOptions{}))(t); len(got.Items) != 1 {
		t.Fatalf("invitations: %+v", got)
	}
	_, err = user(t, tb, m, "user-bia", "someone@example.com").AcceptInvitation(ctx, inv.Token)
	wantCode(t, err, v1.ErrForbidden)
	if member := must(bia.AcceptInvitation(ctx, inv.Token))(t); member.UserID != "user-bia" || member.Role != v1.RoleAdmin {
		t.Fatalf("accept: %+v", member)
	}
	cara := must(ana.CreateInvitation(ctx, org.ID, "cara@example.com", ""))(t)
	if cara.Invitation.Role != v1.RoleMember {
		t.Fatalf("default role: %+v", cara.Invitation)
	}
	if err := ana.RevokeInvitation(ctx, org.ID, cara.Invitation.ID); err != nil {
		t.Fatal(err)
	}
	if got := must(ana.Invitations(ctx, org.ID, v1.PageOptions{}))(t); len(got.Items) != 2 || got.Items[0].Status != v1.InvitationAccepted || got.Items[1].Status != v1.InvitationRevoked {
		t.Fatalf("invitation history: %+v", got)
	}
	_, err = user(t, tb, m, "user-cara", "cara@example.com").AcceptInvitation(ctx, cara.Token)
	wantCode(t, err, v1.ErrConflict) // no longer pending
	if got := must(bia.Members(ctx, org.ID, v1.PageOptions{}))(t); len(got.Items) != 2 {
		t.Fatalf("members: %+v", got)
	}
	_, err = bia.SetPlan(ctx, org.ID, v1.PlanHobby)
	wantCode(t, err, v1.ErrForbidden) // admin, not owner
	if got := must(ana.SetMemberRole(ctx, org.ID, "user-bia", v1.RoleMember))(t); got.Role != v1.RoleMember {
		t.Fatalf("role: %+v", got)
	}

	// Ownership transfer: start, cancel, start again, confirm.
	_, err = ana.OwnershipTransfer(ctx, org.ID)
	wantCode(t, err, v1.ErrUnknownRoute)
	tr := must(ana.StartOwnershipTransfer(ctx, org.ID, "user-bia"))(t)
	if tr.FromUserID != "user-ana" || tr.ToUserID != "user-bia" {
		t.Fatalf("transfer: %+v", tr)
	}
	must(bia.OwnershipTransfer(ctx, org.ID))(t)
	if err := ana.CancelOwnershipTransfer(ctx, org.ID); err != nil {
		t.Fatal(err)
	}
	must(ana.StartOwnershipTransfer(ctx, org.ID, "user-bia"))(t)
	if got := must(bia.ConfirmOwnershipTransfer(ctx, org.ID))(t); got.Role != v1.RoleOwner {
		t.Fatalf("confirm: %+v", got)
	}

	// Audit.
	if days := must(bia.AuditRetention(ctx, org.ID))(t); days != 90 {
		t.Fatalf("retention: %d", days)
	}
	_, err = bia.SetAuditRetention(ctx, org.ID, 0)
	wantCode(t, err, v1.ErrInvalidRequest)
	if days := must(bia.SetAuditRetention(ctx, org.ID, 30))(t); days != 30 {
		t.Fatalf("set retention: %d", days)
	}
	events := must(bia.AuditEvents(ctx, org.ID, v1.PageOptions{Limit: 2}))(t)
	if len(events.Items) != 2 || events.Next == "" || events.Items[0].Action != "settings.audit_retention_updated" || events.Items[0].Actor != "user-bia" {
		t.Fatalf("audit events: %+v", events)
	}
	must(bia.AuditEvents(ctx, org.ID, v1.PageOptions{After: events.Next}))(t)

	// Leaving, removing and deleting.
	wantCode(t, bia.Leave(ctx, org.ID), v1.ErrConflict) // the last owner
	if err := ana.Leave(ctx, org.ID); err != nil {
		t.Fatal(err)
	}
	must(ana.CreateOrganization(ctx, "Other"))(t) // ana still acts elsewhere
	inv2 := must(bia.CreateInvitation(ctx, org.ID, "ana@example.com", ""))(t)
	must(ana.AcceptInvitation(ctx, inv2.Token))(t)
	// An opaque user id is escaped as one segment; a slash or dot segment
	// fails locally, since TerraBase matches %2F literally.
	wantCode(t, bia.RemoveMember(ctx, org.ID, "no such user %"), v1.ErrUnknownRoute)
	for _, id := range []string{"a/b", ".."} {
		if err := bia.RemoveMember(ctx, org.ID, id); err == nil || errors.As(err, new(*v1.APIError)) {
			t.Fatalf("RemoveMember(%q) = %v, want a local error", id, err)
		}
	}
	if err := bia.RemoveMember(ctx, org.ID, "user-ana"); err != nil {
		t.Fatal(err)
	}
	if err := bia.DeleteDatabase(ctx, org.ID, project.ID, db.ID); err != nil {
		t.Fatal(err)
	}
	if err := bia.DeleteProject(ctx, org.ID, project.ID); err != nil {
		t.Fatal(err)
	}
	if err := bia.DeleteOrganization(ctx, org.ID); err != nil {
		t.Fatal(err)
	}
	_, err = bia.Organization(ctx, org.ID)
	wantCode(t, err, v1.ErrUnknownRoute)

	// The service credential acts As a user on the admin listener only; an
	// access token works on the client listener only.
	admin, err := v1.NewManagement(v1.Config{BaseURL: tb.AdminURL, Token: tb.Credential, HTTPClient: hc})
	if err != nil {
		t.Fatal(err)
	}
	boot := admin.As("user-boot")
	bootOrg := must(boot.CreateOrganization(ctx, "Bootstrap"))(t)
	for range 2 { // idempotent
		if got := must(boot.AddMember(ctx, bootOrg.ID, "user-ana", "ana@example.com"))(t); got.UserID != "user-ana" || got.Role != v1.RoleMember {
			t.Fatalf("add member: %+v", got)
		}
	}
	must(ana.Organization(ctx, bootOrg.ID))(t)
	_, err = ana.AddMember(ctx, bootOrg.ID, "user-bia", "bia@example.com")
	wantCode(t, err, v1.ErrUnknownRoute) // admin listener only
	_, err = admin.WithToken(tb.Token(t, "user-ana", "")).Organizations(ctx, v1.PageOptions{})
	wantCode(t, err, v1.ErrInvalidKey)
	onClient, err := v1.NewManagement(v1.Config{BaseURL: tb.URL, Token: tb.Credential, HTTPClient: hc})
	if err != nil {
		t.Fatal(err)
	}
	_, err = onClient.As("user-boot").Organizations(ctx, v1.PageOptions{})
	wantCode(t, err, v1.ErrInvalidKey)
	wrong, err := v1.NewManagement(v1.Config{BaseURL: tb.AdminURL, Token: rand.Text() + rand.Text(), HTTPClient: hc})
	if err != nil {
		t.Fatal(err)
	}
	_, err = wrong.As("user-boot").Organizations(ctx, v1.PageOptions{})
	wantCode(t, err, v1.ErrInvalidKey)
	_, err = m.WithToken("not-a-token").Organizations(ctx, v1.PageOptions{})
	wantCode(t, err, v1.ErrInvalidKey)
}

func testDocuments(t *testing.T, tb *terrabasetest.Server, hc *http.Client) {
	ctx := t.Context()
	u := user(t, tb, management(t, tb, hc), "user-dora", "")
	org := must(u.CreateOrganization(ctx, "Acme"))(t)
	project := must(u.CreateProject(ctx, org.ID, "Production"))(t)
	c, err := v1.New(v1.Config{BaseURL: tb.URL, Token: tb.APIKey(t, org.ID, project.ID, "documents:read", "documents:write"), HTTPClient: hc})
	if err != nil {
		t.Fatal(err)
	}

	// Databases and collections.
	db := c.Database(project.ID, "main")
	info := must(db.Create(ctx))(t)
	if !info.Created || info.ProjectID != project.ID || info.Name != "main" {
		t.Fatalf("create database: %+v", info)
	}
	if again := must(db.Create(ctx))(t); again.Created || again.ID != info.ID {
		t.Fatalf("create database again: %+v", again)
	}
	if got := must(u.Database(ctx, org.ID, project.ID, info.ID))(t); got.ID != info.ID {
		t.Fatalf("management sees %+v", got)
	}
	for _, name := range []string{"users", "audit", "tmp"} {
		col := must(db.CreateCollection(ctx, name))(t)
		if col.Name != name || col.CreatedAt == 0 {
			t.Fatalf("collection: %+v", col)
		}
	}
	_, err = db.CreateCollection(ctx, "users")
	wantCode(t, err, v1.ErrNameTaken)
	page := must(db.Collections(ctx, v1.ListOptions{Limit: 2}))(t)
	if len(page.Items) != 2 || page.Items[0].Name != "audit" || page.NextCursor == "" {
		t.Fatalf("collections: %+v", page)
	}
	if rest := must(db.Collections(ctx, v1.ListOptions{Cursor: page.NextCursor}))(t); len(rest.Items) != 1 || rest.NextCursor != "" {
		t.Fatalf("collections page 2: %+v", rest)
	}
	if got := must(db.Collection("tmp").Info(ctx))(t); got.Name != "tmp" {
		t.Fatalf("info: %+v", got)
	}
	if got := must(db.Collection("tmp").Drop(ctx))(t); got.Name != "tmp" {
		t.Fatalf("drop: %+v", got)
	}
	_, err = db.Collection("tmp").Info(ctx)
	wantCode(t, err, v1.ErrUnknownCollection)
	_, err = c.Database(project.ID, "missing").Collection("users").Get(ctx, v1.DocumentIDFor("users", "x"))
	wantCode(t, err, v1.ErrUnknownDatabase)

	// Documents with ETag/If-Match.
	users := db.Collection("users")
	created := must(users.Create(ctx, map[string]any{"email": "ana@example.com", "logins": 0}))(t)
	if !created.Created || created.Location == "" || created.Durability != v1.LocalDurable {
		t.Fatalf("create: %+v", created)
	}
	doc := must(users.Get(ctx, created.ID))(t)
	if doc.Version != created.Version || doc.State != v1.StateValue {
		t.Fatalf("get: %+v", doc)
	}
	_, err = users.Get(ctx, created.ID, v1.IfNoneMatch(doc.Version))
	wantCode(t, err, v1.ErrNotModified)
	var body map[string]any
	if err := doc.Decode(&body); err != nil || body["logins"] != json.Number("0") {
		t.Fatalf("decode: %v %v", body, err)
	}

	// Management reads the same data as the signed-in user.
	stats := must(u.Collections(ctx, org.ID, project.ID, info.ID))(t)
	if len(stats) != 2 || stats[0].Name != "audit" || stats[0].Documents != 0 || stats[1].Name != "users" || stats[1].Documents != 1 || stats[1].Bytes <= 0 {
		t.Fatalf("collection stats: %+v", stats)
	}
	rows := must(u.SQL(ctx, org.ID, project.ID, info.ID, "SELECT * FROM users ORDER BY _id LIMIT 10"))(t)
	if rows.Matched != 1 || rows.Truncated || len(rows.Rows) != 1 || rows.Rows[0].ID != created.ID || !strings.Contains(string(rows.Rows[0].Value), "ana@example.com") {
		t.Fatalf("sql: %+v", rows)
	}
	_, err = u.SQL(ctx, org.ID, project.ID, info.ID, "DELETE FROM users")
	wantCode(t, err, v1.ErrInvalidRequest)
	usage := must(u.DatabaseUsage(ctx, org.ID, project.ID, info.ID))(t)
	if usage.DatabaseID != info.ID || usage.Collections != 2 || usage.Documents != 1 || usage.StorageBytes <= 0 {
		t.Fatalf("usage: %+v", usage)
	}

	id := v1.DocumentIDFor("users", "bia@example.com")
	put := must(users.Put(ctx, id, map[string]any{"email": "bia@example.com"}, v1.IfNoneMatchAny()))(t)
	if !put.Created {
		t.Fatalf("put: %+v", put)
	}
	_, err = users.Put(ctx, id, map[string]any{"email": "x"}, v1.IfNoneMatchAny())
	wantCode(t, err, v1.ErrPreconditionFailed)
	put = must(users.Put(ctx, id, map[string]any{"email": "bia@example.com", "tags": []string{}}, v1.IfMatch(put.Version)))(t)
	patched := must(users.Patch(ctx, id, v1.Ops(v1.Inc("/logins", 2), v1.Append("/tags", "beta"), v1.Set("/name", "Bia")), v1.IfMatch(put.Version)))(t)
	_, err = users.Patch(ctx, id, v1.MergePatch(map[string]any{"name": nil}), v1.IfMatch(put.Version))
	wantCode(t, err, v1.ErrPreconditionFailed)
	must(users.Patch(ctx, id, v1.MergePatch(map[string]any{"name": nil}), v1.IfMatch(patched.Version)))(t)
	must(users.Patch(ctx, id, v1.Ops(v1.Unset("/tags"))))(t)
	got := must(users.Get(ctx, id))(t)
	body = nil
	if err := got.Decode(&body); err != nil || body["logins"] != json.Number("2") || body["name"] != nil || body["tags"] != nil {
		t.Fatalf("patched: %v %v", body, err)
	}
	deleted := must(users.Delete(ctx, id, v1.IfMatch(got.Version)))(t)
	_, err = users.Get(ctx, id)
	wantCode(t, err, v1.ErrNotFound)
	var apiErr *v1.APIError
	if !errors.As(err, &apiErr) || apiErr.ETag != deleted.Version {
		t.Fatalf("tombstone etag: %+v", apiErr)
	}
	must(users.Put(ctx, id, map[string]any{"email": "bia@example.com"}, v1.IfMatch(deleted.Version)))(t)
	other := v1.DocumentIDFor("users", "cara@example.com")
	must(users.Put(ctx, other, map[string]any{"email": "cara@example.com"}))(t)
	must(users.Delete(ctx, other))(t)

	// List and query.
	list := must(users.List(ctx, v1.ListOptions{Limit: 1}))(t)
	if len(list.Items) != 1 || list.NextCursor == "" {
		t.Fatalf("list: %+v", list)
	}
	if rest := must(users.List(ctx, v1.ListOptions{Cursor: list.NextCursor}))(t); len(rest.Items) != 1 || rest.NextCursor != "" {
		t.Fatalf("list page 2: %+v", rest)
	}
	q := v1.Query{Filter: v1.Prefix("/email", "bia"), Sort: []v1.SortKey{v1.Desc("/email")}, Project: []string{"/email"}, Limit: 10, Count: true}
	res := must(users.Query(ctx, q))(t)
	if len(res.Items) != 1 || res.Count == nil || res.Count.Value != 1 || !res.Count.Exact {
		t.Fatalf("query: %+v", res)
	}
	n := 0
	for _, err := range users.Items(ctx, v1.Query{Limit: 1}) {
		if err != nil {
			t.Fatal(err)
		}
		n++
	}
	if n != 2 {
		t.Fatalf("items: %d", n)
	}
	// A non-UUID id reaches the server, which addresses only existing legacy ids.
	_, err = users.Get(ctx, "legacy id")
	wantCode(t, err, v1.ErrInvalidRequest)
	// C# counts UTF-16 units, rather than UTF-8 bytes, in legacy identities.
	for _, id := range []string{strings.Repeat("界", 256), strings.Repeat("😀", 128)} {
		_, err := users.Get(ctx, id)
		wantCode(t, err, v1.ErrInvalidRequest) // absent non-UUID, rejected by the server
	}
	_, err = users.Query(ctx, v1.Query{Cursor: "garbage"})
	wantCode(t, err, v1.ErrInvalidCursor)

	// Transactions.
	two := uint64(2)
	tx := must(db.Tx().
		Create("audit", "", map[string]any{"event": "signup"}).
		Patch("users", id, v1.Ops(v1.Inc("/logins", 1))).
		Assert("users", created.ID, v1.Eq("/email", "ana@example.com")).
		AssertAbsent("users", other).
		AssertCount("users", v1.Exists("/email"), v1.CountBounds{Lte: &two}).
		Commit(ctx))(t)
	if len(tx.Results) != 5 || tx.Results[0].ID == "" || !tx.Results[2].Asserted || tx.Results[4].Count == nil || *tx.Results[4].Count != 2 {
		t.Fatalf("tx: %+v", tx)
	}
	_, err = db.Tx().Put("users", created.ID, map[string]any{}, v1.IfMatch(deleted.Version)).Commit(ctx)
	wantCode(t, err, v1.ErrPreconditionFailed)
	if !errors.As(err, &apiErr) {
		t.Fatal(err)
	} else if op, ok := apiErr.OpIndex(); !ok || op != 0 {
		t.Fatalf("op index: %v", apiErr)
	}

	auditID := v1.DocumentIDFor("audit", "tagged")
	tx2 := must(db.Tx().
		Put("audit", auditID, map[string]any{"tags": []string{"beta"}}, v1.IfNoneMatchAny()).
		Delete("audit", tx.Results[0].ID).
		Commit(ctx))(t)
	if len(tx2.Results) != 2 || tx2.Results[0].ID != auditID || tx2.Results[1].Version == "" {
		t.Fatalf("tx2: %+v", tx2)
	}

	// Every filter constructor; users holds ana (logins 0) and bia (logins 1).
	for name, tc := range map[string]struct {
		col    string
		filter v1.Filter
		want   int
	}{
		"or":       {"users", v1.Or(v1.Eq("/email", "ana@example.com"), v1.Eq("/email", "bia@example.com")), 2},
		"and not":  {"users", v1.And(v1.Exists("/logins"), v1.Not(v1.Ne("/logins", 0))), 1},
		"ne":       {"users", v1.Ne("/logins", 0), 1},
		"lt":       {"users", v1.Lt("/logins", 1), 1},
		"lte":      {"users", v1.Lte("/logins", 0), 1},
		"gt":       {"users", v1.Gt("/logins", -1), 2},
		"gte":      {"users", v1.Gte("/logins", 1), 1},
		"in":       {"users", v1.In("/email", "ana@example.com", "x@example.com"), 1},
		"nin":      {"users", v1.Nin("/email", "ana@example.com"), 1},
		"id":       {"users", v1.Eq(v1.IDPath, created.ID), 1},
		"contains": {"audit", v1.Contains("/tags", "beta"), 1},
	} {
		res := must(db.Collection(tc.col).Query(ctx, v1.Query{Filter: tc.filter, Sort: []v1.SortKey{v1.Asc(v1.IDPath)}}))(t)
		if len(res.Items) != tc.want {
			t.Errorf("%s: %d items, want %d", name, len(res.Items), tc.want)
		}
	}

	// Scopes: a read-only key cannot write; a key for another project sees nothing.
	reader, err := v1.New(v1.Config{BaseURL: tb.URL, Token: tb.APIKey(t, org.ID, project.ID, "documents:read"), HTTPClient: hc})
	if err != nil {
		t.Fatal(err)
	}
	must(reader.Database(project.ID, "main").Collection("users").Get(ctx, created.ID))(t)
	_, err = reader.Database(project.ID, "main").Collection("users").Create(ctx, map[string]any{})
	wantCode(t, err, v1.ErrForbiddenScope)
	elsewhere := must(u.CreateProject(ctx, org.ID, "Staging"))(t)
	_, err = c.Database(elsewhere.ID, "main").Create(ctx)
	wantCode(t, err, v1.ErrUnknownDatabase)
	revoked, err := v1.New(v1.Config{BaseURL: tb.URL, Token: "tb_" + fmt.Sprintf("%043d", 0), HTTPClient: hc})
	if err != nil {
		t.Fatal(err)
	}
	_, err = revoked.Database(project.ID, "main").Collections(ctx, v1.ListOptions{})
	wantCode(t, err, v1.ErrInvalidKey)

	ctx2, cancel := context.WithTimeout(ctx, time.Nanosecond)
	defer cancel()
	<-ctx2.Done()
	if _, err := users.Get(ctx2, created.ID); !errors.Is(err, context.DeadlineExceeded) {
		t.Fatalf("expired context: %v", err)
	}
}

func testRetries(t *testing.T, tb *terrabasetest.Server, hc *http.Client, rec *recorder) {
	ctx := t.Context()
	u := user(t, tb, management(t, tb, hc), "user-eve", "")
	org := must(u.CreateOrganization(ctx, "Retries"))(t)
	project := must(u.CreateProject(ctx, org.ID, "Retries"))(t)
	resolver := &testResolver{url: tb.URL}
	c, err := v1.New(v1.Config{Resolver: resolver, Token: tb.APIKey(t, org.ID, project.ID, "documents:read", "documents:write"), HTTPClient: hc, MaxRetries: 2})
	if err != nil {
		t.Fatal(err)
	}
	db := c.Database(project.ID, "main")
	info := must(db.Create(ctx))(t)
	must(db.CreateCollection(ctx, "items"))(t)
	items := db.Collection("items")
	id := v1.DocumentIDFor("items", "a")
	must(items.Put(ctx, id, map[string]any{"n": 0}))(t)
	version := func() string { return must(items.Get(ctx, id))(t).Version }

	one := uint64(1)
	const capacity = `{"error":{"code":"capacity"}}`
	const limited = `{"error":{"code":"rate_limited"}}`
	const internal = `{"error":{"code":"internal_error"}}`
	const uncertain = `{"error":{"code":"commit_uncertain"}}`
	lost := errors.New("injected lost response")
	put := func(pre ...v1.WriteOption) error {
		_, err := items.Put(ctx, id, map[string]any{"n": 1}, pre...)
		return err
	}
	for _, tc := range []struct {
		name   string
		f      fault
		call   func() error
		tries  int
		wantOK bool
	}{
		{"get 503", fault{method: "GET", status: 503, body: capacity}, func() error { _, err := items.Get(ctx, id); return err }, 2, true},
		{"get transport", fault{method: "GET", err: lost, after: true}, func() error { _, err := items.Get(ctx, id); return err }, 2, true},
		{"get commit uncertain", fault{method: "GET", status: 503, body: uncertain}, func() error { _, err := items.Get(ctx, id); return err }, 2, true},
		{"get 429", fault{method: "GET", status: 429, body: limited}, func() error { _, err := items.Get(ctx, id); return err }, 2, true},
		{"get 500", fault{method: "GET", status: 500, body: internal}, func() error { _, err := items.Get(ctx, id); return err }, 1, false},
		{"query 503", fault{method: "POST", status: 503, body: capacity}, func() error { _, err := items.Query(ctx, v1.Query{}); return err }, 2, true},
		{"query transport", fault{method: "POST", err: lost, after: true}, func() error { _, err := items.Query(ctx, v1.Query{}); return err }, 2, true},
		{"query commit uncertain", fault{method: "POST", status: 503, body: uncertain}, func() error { _, err := items.Query(ctx, v1.Query{}); return err }, 2, true},
		{"put if-match 503", fault{method: "PUT", status: 503, body: capacity}, func() error { return put(v1.IfMatch(version())) }, 2, true},
		{"put if-match 429", fault{method: "PUT", status: 429, body: limited}, func() error { return put(v1.IfMatch(version())) }, 2, true},
		{"put 503", fault{method: "PUT", status: 503, body: capacity}, func() error { return put() }, 1, false},
		{"put 429", fault{method: "PUT", status: 429, body: limited}, func() error { return put() }, 1, false},
		{"put if-none-match 503", fault{method: "PUT", status: 503, body: capacity}, func() error { return put(v1.IfNoneMatchAny()) }, 1, false},
		{"create 503", fault{method: "POST", status: 503, body: capacity}, func() error { _, err := items.Create(ctx, map[string]any{}); return err }, 1, false},
		{"patch 503", fault{method: "PATCH", status: 503, body: capacity}, func() error { _, err := items.Patch(ctx, id, v1.Ops(v1.Inc("/n", 1))); return err }, 1, false},
		{"patch if-match 503", fault{method: "PATCH", status: 503, body: capacity}, func() error {
			_, err := items.Patch(ctx, id, v1.Ops(v1.Inc("/n", 1)), v1.IfMatch(version()))
			return err
		}, 2, true},
		{"tx if-match 503", fault{method: "POST", status: 503, body: capacity}, func() error {
			_, err := db.Tx().Put("items", id, map[string]any{"n": 2}, v1.IfMatch(version())).AssertCount("items", v1.Exists("/n"), v1.CountBounds{Gte: &one}).Commit(ctx)
			return err
		}, 2, true},
		{"tx create 503", fault{method: "POST", status: 503, body: capacity}, func() error {
			_, err := db.Tx().Create("items", "", map[string]any{}).Commit(ctx)
			return err
		}, 1, false},
		{"management get 503", fault{method: "GET", status: 503, body: capacity}, func() error { _, err := u.Organization(ctx, org.ID); return err }, 2, true},
		{"management create 503", fault{method: "POST", status: 503, body: capacity}, func() error { _, err := u.CreateProject(ctx, org.ID, "x"); return err }, 1, false},
	} {
		t.Run(tc.name, func(t *testing.T) {
			hits := rec.arm(tc.f)
			err := tc.call()
			if n := hits(); n != tc.tries || (err == nil) != tc.wantOK {
				t.Fatalf("%d requests (want %d), err %v", n, tc.tries, err)
			}
		})
	}
	// A region outside the database's placement answers 421 with where it
	// is; the client returns it, never retrying elsewhere.
	wrongRegion := fault{method: "GET", status: http.StatusMisdirectedRequest,
		body: `{"error":{"code":"wrong_region","message":"database is not placed in iad-1"}}`,
		header: http.Header{
			"X-Terrabase-Database-Home":    {"gru-1"},
			"X-Terrabase-Database-Regions": {"gru-1,sfo-1"},
			"X-Terrabase-Region":           {"iad-1"},
		}}
	for name, call := range map[string]func() error{
		"documents":  func() error { _, err := items.Get(ctx, id); return err },
		"management": func() error { _, err := u.Database(ctx, org.ID, project.ID, info.ID); return err },
	} {
		t.Run("wrong region "+name, func(t *testing.T) {
			hits := rec.arm(wrongRegion)
			err := call()
			var apiErr *v1.APIError
			if n := hits(); n != 1 || !errors.Is(err, v1.ErrWrongRegion) || !errors.As(err, &apiErr) ||
				apiErr.Status != 421 || apiErr.HomeRegion != "gru-1" || !slices.Equal(apiErr.Regions, []string{"gru-1", "sfo-1"}) {
				t.Fatalf("%d requests, err %v (%+v)", n, err, apiErr)
			}
		})
	}
	t.Run("resolve once across retries", func(t *testing.T) {
		before := resolver.calls
		hits := rec.arm(fault{method: "GET", status: 503, body: capacity})
		_, err := items.Get(ctx, id)
		if tries := hits(); err != nil || tries != 2 || resolver.calls != before+1 {
			t.Fatalf("resolution calls=%d, HTTP attempts=%d, error=%v", resolver.calls-before, tries, err)
		}
	})

	// A lost response must remain uncertain, never become a replay's 412.
	for _, op := range []struct {
		name, method string
		call         func(string) error
	}{
		{"put", "PUT", func(v string) error { return put(v1.IfMatch(v)) }},
		{"patch", "PATCH", func(v string) error {
			_, err := items.Patch(ctx, id, v1.Ops(v1.Inc("/n", 1)), v1.IfMatch(v))
			return err
		}},
		{"delete", "DELETE", func(v string) error { _, err := items.Delete(ctx, id, v1.IfMatch(v)); return err }},
		{"transaction", "POST", func(v string) error {
			_, err := db.Tx().Patch("items", id, v1.Ops(v1.Inc("/n", 1)), v1.IfMatch(v)).Commit(ctx)
			return err
		}},
	} {
		for _, transport := range []bool{false, true} {
			t.Run(op.name+" lost response/transport="+fmt.Sprint(transport), func(t *testing.T) {
				before := must(items.Put(ctx, id, map[string]any{"n": 0}))(t).Version
				f := fault{method: op.method, status: 503, body: uncertain, after: true}
				if transport {
					f.err = lost
				}
				hits := rec.arm(f)
				err := op.call(before)
				if n := hits(); n != 1 || !errors.Is(err, v1.ErrCommitUncertain) || errors.Is(err, v1.ErrPreconditionFailed) {
					t.Fatalf("%d requests, err %v", n, err)
				}
				if transport {
					if !errors.Is(err, lost) {
						t.Fatalf("lost transport cause: %v", err)
					}
				} else {
					var apiErr *v1.APIError
					if !errors.As(err, &apiErr) || apiErr.Status != 503 {
						t.Fatalf("lost API response: %v", err)
					}
				}
				got, err := items.Get(ctx, id)
				if op.name == "delete" {
					wantCode(t, err, v1.ErrNotFound)
				} else {
					if err != nil {
						t.Fatal(err)
					}
					var doc struct{ N int }
					if err := got.Decode(&doc); err != nil || doc.N != 1 || got.Version == before {
						t.Fatalf("committed document = %s, error=%v", got.Document, err)
					}
				}
				_, conflict := items.Put(ctx, id, map[string]any{"n": 2}, v1.IfMatch(before))
				if !errors.Is(conflict, v1.ErrPreconditionFailed) || errors.Is(conflict, v1.ErrCommitUncertain) {
					t.Fatalf("genuine precondition failure: %v", conflict)
				}
			})
		}
	}
}

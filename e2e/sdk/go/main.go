package main

import (
	"context"
	"database/sql"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"net/netip"
	"net/url"
	"os"
	"path/filepath"
	"reflect"
	"strings"
	"time"

	"terrascale.tech/sdk/tsauth"
	v1 "terrascale.tech/sdk/v1"
)

type operation struct {
	ID            string         `json:"id"`
	Group         string         `json:"group"`
	Needs         string         `json:"needs"`
	Setup         string         `json:"setup"`
	SQL           string         `json:"sql"`
	Args          []any          `json:"args"`
	Named         map[string]any `json:"named"`
	RowsAffected  int64          `json:"rowsAffected"`
	Rows          [][]any        `json:"rows"`
	Objects       []item         `json:"objects"`
	Statements    []string       `json:"statements"`
	LastRows      [][]any        `json:"lastRows"`
	Failing       []string       `json:"failing"`
	Code          string         `json:"code"`
	Statement     int            `json:"statement"`
	Check         string         `json:"check"`
	CheckRows     [][]any        `json:"checkRows"`
	SQLState      string         `json:"sqlstate"`
	Status        int            `json:"status"`
	Values        map[string]any `json:"values"`
	Select        string         `json:"select"`
	StoredFlag    int64          `json:"storedFlag"`
	Query         string         `json:"query"`
	Message       string         `json:"message"`
	Invalid       string         `json:"invalid"`
	Name          string         `json:"name"`
	TTLSeconds    int64          `json:"ttlSeconds"`
	Access        string         `json:"access"`
	MintStatus    int            `json:"mintStatus"`
	DeletedStatus int            `json:"deletedStatus"`
	Cases         string         `json:"cases"`
}

type item struct {
	ID string `json:"id"`
	N  int64  `json:"n"`
}

type runner struct {
	ctx     context.Context
	client  *v1.Client
	project string
	table   string
	run     string
	db      *v1.Database
	session *v1.SQLSession
	agent   *v1.ManagedDatabase
}

func (r *runner) fill(text string) string {
	return strings.NewReplacer("{table}", r.table, "{sdk}", "go", "{run}", r.run).Replace(text)
}

func (r *runner) fillAll(texts []string) []v1.Statement {
	out := make([]v1.Statement, len(texts))
	for i, text := range texts {
		out[i] = v1.Statement{SQL: r.fill(text)}
	}
	return out
}

func same(actual, expected any, what string) error {
	if !reflect.DeepEqual(actual, expected) {
		return fmt.Errorf("%s: expected %v, got %v", what, expected, actual)
	}
	return nil
}

func normal(value any) any {
	switch v := value.(type) {
	case float64:
		if v == float64(int64(v)) {
			return int64(v)
		}
		return v
	case int:
		return int64(v)
	case []any:
		out := make([]any, len(v))
		for i, x := range v {
			out[i] = normal(x)
		}
		return out
	}
	return value
}

func grid(rows [][]any) [][]any {
	out := make([][]any, len(rows))
	for i, row := range rows {
		out[i] = normal(row).([]any)
	}
	return out
}

func cells(rows []v1.Row) [][]any {
	out := make([][]any, len(rows))
	for i, row := range rows {
		out[i] = normal(append([]any(nil), row.Values()...)).([]any)
	}
	return out
}

func status(err error) int {
	var api *v1.APIError
	var sqlErr *v1.SQLError
	switch {
	case errors.As(err, &sqlErr):
		return sqlErr.Status
	case errors.As(err, &api):
		return api.Status
	}
	return 0
}

func code(err error) string {
	var api *v1.APIError
	var sqlErr *v1.SQLError
	switch {
	case errors.As(err, &sqlErr):
		return string(sqlErr.Code)
	case errors.As(err, &api):
		return string(api.Code)
	}
	return ""
}

func refused(err error) error {
	if err == nil {
		return errors.New("the call was accepted")
	}
	return nil
}

func (r *runner) count(check string, expected [][]any, what string) error {
	rows, err := r.db.Query(r.ctx, r.fill(check))
	if err != nil {
		return err
	}
	return same(cells(rows), grid(expected), what)
}

func newClient(key string) (*v1.Client, error) {
	return v1.New(v1.Config{APIKey: key})
}

func (r *runner) do(op operation) error {
	if strings.HasPrefix(op.ID, "ws.") && op.ID != "ws.connect" && r.session == nil {
		return errors.New("the session is not connected")
	}
	ctx := r.ctx
	switch op.ID {
	case "auth.organizations":
		auth, err := tsauth.New(tsauth.Config{})
		if err != nil {
			return err
		}
		read := auth.WithToken(os.Getenv("E2E_AUTH_READ_KEY"))
		write := auth.WithToken(os.Getenv("E2E_AUTH_WRITE_KEY"))
		user := os.Getenv("E2E_AUTH_USER")
		created, err := write.CreateProjectOrganization(ctx, r.project, tsauth.ProjectOrganizationRequest{Name: "SDK organization", Slug: strings.ReplaceAll(r.table, "_", "-"), AdminUserId: &user, DomainJoin: "off"})
		if err != nil {
			return err
		}
		defer write.DeleteProjectOrganization(ctx, r.project, created.Id)
		found, err := read.GetProjectOrganization(ctx, r.project, created.Id)
		if err != nil {
			return err
		}
		if err = same(found.Id, created.Id, "organization read"); err != nil {
			return err
		}
		listed, err := read.ListProjectOrganizations(ctx, r.project, 100, "")
		if err != nil {
			return err
		}
		seen := false
		for _, item := range listed.Items {
			if item.Id == created.Id {
				seen = true
			}
		}
		if err = same(seen, true, "organization listed"); err != nil {
			return err
		}
		members, err := read.ListProjectOrganizationMembers(ctx, r.project, created.Id, 100, "")
		if err != nil {
			return err
		}
		if err = same(members.Items[0].Role, "admin", "initial admin"); err != nil {
			return err
		}
		_, err = write.CreateProjectOrganizationInvitation(ctx, r.project, created.Id, tsauth.ProjectOrganizationInvitationRequest{Email: "org-" + r.run + "@example.com", Role: "member"})
		if err != nil {
			return err
		}
		invites, err := read.ListProjectOrganizationInvitations(ctx, r.project, created.Id, 100, "")
		if err != nil {
			return err
		}
		return same(invites.Items[0].Status, "pending", "invitation listed")
	case "auth.manage_user":
		auth, err := tsauth.New(tsauth.Config{})
		if err != nil {
			return err
		}
		user := os.Getenv("E2E_AUTH_USER")
		read := auth.WithToken(os.Getenv("E2E_AUTH_READ_KEY"))
		listed, err := read.ProjectUsers(r.ctx, r.project, tsauth.ProjectUserListOptions{})
		if err != nil {
			return err
		}
		found := false
		for _, item := range listed.Items {
			found = found || item.ID == user
		}
		if !found {
			return errors.New("read key did not list the user")
		}
		_, err = read.DisableProjectUser(r.ctx, r.project, user)
		var refusal *tsauth.APIError
		if !errors.As(err, &refusal) || refusal.Status != 403 {
			return errors.New("read key was not refused")
		}
		write := auth.WithToken(os.Getenv("E2E_AUTH_WRITE_KEY"))
		disabled, err := write.DisableProjectUser(r.ctx, r.project, user)
		if err != nil {
			return err
		}
		if !disabled.Disabled {
			return errors.New("write key did not disable")
		}
		enabled, err := write.EnableProjectUser(r.ctx, r.project, user)
		if err != nil {
			return err
		}
		return same(enabled.Disabled, false, "write key enables")
	case "sql.execute.positional":
		if _, err := r.db.Execute(ctx, r.fill(op.Setup)); err != nil {
			return err
		}
		result, err := r.db.Execute(ctx, r.fill(op.SQL), op.Args...)
		if err != nil {
			return err
		}
		return same(result.RowsAffected, op.RowsAffected, "rows affected")
	case "sql.execute.named":
		args := make([]any, 0, len(op.Named))
		for name, value := range op.Named {
			args = append(args, sql.Named(name, value))
		}
		result, err := r.db.Execute(ctx, r.fill(op.SQL), args...)
		if err != nil {
			return err
		}
		return same(result.RowsAffected, op.RowsAffected, "rows affected")
	case "sql.execute.statement":
		result, err := r.db.ExecuteStatement(ctx, v1.Statement{SQL: r.fill(op.SQL), Args: op.Args})
		if err != nil {
			return err
		}
		if err := same(result.RowsAffected, op.RowsAffected, "rows affected"); err != nil {
			return err
		}
		return r.count(op.Check, op.CheckRows, "stored row")
	case "sql.query":
		rows, err := r.db.Query(ctx, r.fill(op.SQL))
		if err != nil {
			return err
		}
		return same(cells(rows), grid(op.Rows), "rows")
	case "sql.query.typed":
		typed, err := v1.QueryAs[item](ctx, r.db, r.fill(op.SQL), op.Args...)
		if err != nil {
			return err
		}
		return same(typed, op.Objects, "typed rows")
	case "sql.batch":
		results, err := r.db.Batch(ctx, r.fillAll(op.Statements))
		if err != nil {
			return err
		}
		if len(results) != len(op.Statements) {
			return fmt.Errorf("batch answered %d results for %d statements", len(results), len(op.Statements))
		}
		if err := same(cells(results[len(results)-1].Rows), grid(op.LastRows), "last rows"); err != nil {
			return err
		}
		_, err = r.db.Batch(ctx, r.fillAll(op.Failing))
		var sqlErr *v1.SQLError
		if !errors.As(err, &sqlErr) {
			return fmt.Errorf("batch refusal: %v", err)
		}
		if err := same([]any{string(sqlErr.Code), sqlErr.Statement}, []any{op.Code, op.Statement}, "batch refusal"); err != nil {
			return err
		}
		return r.count(op.Check, op.CheckRows, "rolled back")
	case "sql.refused":
		_, err := r.db.Execute(ctx, r.fill(op.SQL))
		var sqlErr *v1.SQLError
		if !errors.As(err, &sqlErr) {
			return fmt.Errorf("refusal: %v", err)
		}
		return same([]any{string(sqlErr.Code), sqlErr.Statement, sqlErr.SQLState, sqlErr.Status}, []any{op.Code, op.Statement, op.SQLState, op.Status}, "SqlError")
	case "values.roundtrip":
		v := op.Values
		if _, err := r.db.Execute(ctx, r.fill(op.Setup)); err != nil {
			return err
		}
		blob, err := base64.StdEncoding.DecodeString(v["b"].(string))
		if err != nil {
			return err
		}
		var big int64
		if _, err := fmt.Sscan(v["big"].(string), &big); err != nil {
			return err
		}
		if _, err := r.db.Execute(ctx, r.fill(op.SQL), v["k"], int64(v["n"].(float64)), big, v["r"], v["flag"], v["t"], blob, v["absent"]); err != nil {
			return err
		}
		rows, err := r.db.Query(ctx, r.fill(op.Select))
		if err != nil {
			return err
		}
		got := rows[0].Values()
		actual := []any{got[0], got[1], fmt.Sprint(got[2]), got[3], got[4], got[5], base64.StdEncoding.EncodeToString(got[6].([]byte)), got[7]}
		expected := []any{v["k"], int64(42), v["big"], v["r"], op.StoredFlag, v["t"], v["b"], v["absent"]}
		return same(normal(actual), normal(expected), "values")
	case "ws.connect":
		session, err := r.db.Connect(ctx)
		r.session = session
		return err
	case "ws.statements":
		result, err := r.session.Execute(ctx, r.fill(op.SQL))
		if err != nil {
			return err
		}
		if err := same(result.RowsAffected, int64(1), "rows affected"); err != nil {
			return err
		}
		rows, err := r.session.Query(ctx, r.fill(op.Query))
		if err != nil {
			return err
		}
		if err := same(cells(rows), grid(op.Rows), "rows"); err != nil {
			return err
		}
		results, err := r.session.Batch(ctx, r.fillAll(op.Statements))
		if err != nil {
			return err
		}
		if len(results) != len(op.Statements) {
			return fmt.Errorf("batch answered %d results for %d statements", len(results), len(op.Statements))
		}
		return same(cells(results[len(results)-1].Rows), grid(op.LastRows), "last rows")
	case "ws.begin_commit", "ws.rollback":
		if err := r.session.Begin(ctx); err != nil {
			return err
		}
		if _, err := r.session.Execute(ctx, r.fill(op.SQL)); err != nil {
			return err
		}
		end := r.session.Commit
		if op.ID == "ws.rollback" {
			end = r.session.Rollback
		}
		if err := end(ctx); err != nil {
			return err
		}
		return r.count(op.Check, op.CheckRows, "rows after the transaction")
	case "ws.transaction.commit":
		if err := r.session.Transaction(ctx, func(s *v1.SQLSession) error {
			_, err := s.Execute(ctx, r.fill(op.SQL))
			return err
		}); err != nil {
			return err
		}
		return r.count(op.Check, op.CheckRows, "committed rows")
	case "ws.transaction.rethrow":
		thrown := errors.New(op.Message)
		err := r.session.Transaction(ctx, func(s *v1.SQLSession) error {
			if _, err := s.Execute(ctx, r.fill(op.SQL)); err != nil {
				return err
			}
			return thrown
		})
		if !errors.Is(err, thrown) {
			return fmt.Errorf("the transaction returned %v", err)
		}
		return r.count(op.Check, op.CheckRows, "rolled back rows")
	case "ws.in_transaction":
		if err := r.session.Begin(ctx); err != nil {
			return err
		}
		inside := r.session.InTransaction()
		if err := r.session.Commit(ctx); err != nil {
			return err
		}
		return same([]bool{inside, r.session.InTransaction()}, []bool{true, false}, "inTransaction")
	case "ws.close":
		if err := r.session.Close(); err != nil {
			return err
		}
		_, err := r.session.Execute(ctx, op.SQL)
		return refused(err)
	case "conflicts.query":
		now := time.Now().UTC()
		rows, err := r.db.Query(ctx, op.SQL, r.table, now.Add(-time.Hour).Format(time.RFC3339), now.Add(time.Hour).Format(time.RFC3339))
		if err != nil {
			return err
		}
		return same(cells(rows), grid(op.Rows), "conflict rows")
	case "ids.new_validate":
		return same([]bool{v1.ValidateID(v1.NewID()) == nil, v1.ValidateID(op.Invalid) == nil}, []bool{true, false}, "validation")
	case "placement.create":
		return r.placementCreate()
	case "placement.set":
		return r.placementSet()
	case "origin.rule":
		return originRule(ctx, r.project, op.Cases)
	case "agent.create_ttl":
		ttl := op.TTLSeconds
		before := time.Now().UTC()
		created, err := r.client.CreateDatabase(ctx, r.project, r.fill(op.Name), v1.CreateDatabaseOptions{TTLSeconds: &ttl})
		after := time.Now().UTC()
		if err != nil {
			return err
		}
		if created.ExpiresAt == nil {
			return errors.New("the database has no expiry")
		}
		earliest, latest := before.Add(time.Duration(ttl)*time.Second), after.Add(time.Duration(ttl+5)*time.Second)
		if created.ExpiresAt.Before(earliest) || created.ExpiresAt.After(latest) {
			return fmt.Errorf("expiry the server reports, %s, is outside %s..%s", created.ExpiresAt.Format(time.RFC3339), earliest.Format(time.RFC3339), latest.Format(time.RFC3339))
		}
		r.agent = created
		return same(created.Name, r.fill(op.Name), "name")
	case "agent.set_ttl":
		updated, err := r.client.SetDatabaseTTL(ctx, r.project, r.agent.ID, op.TTLSeconds)
		if err != nil {
			return err
		}
		if updated.ExpiresAt == nil || !updated.ExpiresAt.After(*r.agent.ExpiresAt) {
			return errors.New("the expiry did not move later")
		}
		return nil
	case "agent.clear_ttl":
		cleared, err := r.client.ClearDatabaseTTL(ctx, r.project, r.agent.ID)
		if err != nil {
			return err
		}
		return same(cleared.ExpiresAt == nil, true, "no expiry")
	case "agent.scoped_key":
		ttl := op.TTLSeconds
		scoped, err := r.client.CreateScopedKey(ctx, v1.ScopedKeyRequest{Project: r.project, Database: r.agent.ID, Access: v1.ScopedKeyAccess(op.Access), TTLSeconds: &ttl})
		if err != nil {
			return err
		}
		if err := os.WriteFile(filepath.Join(os.Getenv("E2E_SECRETS"), "scoped-key-go"), []byte(scoped.Key), 0o600); err != nil {
			return err
		}
		narrow, err := newClient(scoped.Key)
		if err != nil {
			return err
		}
		if _, err := narrow.Database(r.project, r.agent.ID).Execute(ctx, op.SQL); err != nil {
			return err
		}
		_, mint := narrow.CreateScopedKey(ctx, v1.ScopedKeyRequest{Project: r.project, Database: r.agent.ID, Access: v1.ScopedKeyRead})
		return same(status(mint), op.MintStatus, "minting with a scoped key")
	case "agent.fork":
		if _, err := r.db.Execute(ctx, r.fill(op.Setup)); err != nil {
			return err
		}
		if _, err := r.db.Execute(ctx, r.fill(op.SQL)); err != nil {
			return err
		}
		forked, err := r.client.ForkDatabase(ctx, r.project, os.Getenv("TERRASCALE_DATABASE_NAME"), r.fill(op.Name), v1.ForkDatabaseOptions{})
		if err != nil {
			return err
		}
		lineage := ""
		if forked.BranchedFrom != nil {
			lineage = *forked.BranchedFrom
		}
		if err := same([]string{forked.Name, lineage}, []string{r.fill(op.Name), os.Getenv("E2E_DATABASE_ID")}, "fork"); err != nil {
			return err
		}
		rows, err := r.client.Database(r.project, forked.ID).Query(ctx, r.fill(op.Check))
		if err != nil {
			return err
		}
		if err := same(cells(rows), grid(op.CheckRows), "rows of the fork"); err != nil {
			return err
		}
		return r.client.DeleteDatabase(ctx, r.project, forked.ID)
	case "agent.scoped_key_outside":
		ttl := op.TTLSeconds
		source := os.Getenv("E2E_DATABASE_ID")
		scoped, err := r.client.CreateScopedKey(ctx, v1.ScopedKeyRequest{Project: r.project, Database: source, Access: v1.ScopedKeyAccess(op.Access), TTLSeconds: &ttl})
		if err != nil {
			return err
		}
		if err := os.WriteFile(filepath.Join(os.Getenv("E2E_SECRETS"), "scoped-key-go-outside"), []byte(scoped.Key), 0o600); err != nil {
			return err
		}
		narrow, err := newClient(scoped.Key)
		if err != nil {
			return err
		}
		if _, err := narrow.Database(r.project, source).Query(ctx, op.SQL); err != nil {
			return err
		}
		_, err = narrow.Database(r.project, os.Getenv("E2E_OTHER_DATABASE")).Query(ctx, op.SQL)
		if err := refused(err); err != nil {
			return err
		}
		return same([]any{status(err), code(err)}, []any{op.Status, op.Code}, "the other database")
	case "agent.delete":
		if err := r.client.DeleteDatabase(ctx, r.project, r.agent.ID); err != nil {
			return err
		}
		_, err := r.client.Database(r.project, r.agent.ID).Execute(ctx, op.SQL)
		return same(status(err), op.DeletedStatus, "the deleted database")
	}
	return fmt.Errorf("no implementation for %s", op.ID)
}

type recorder struct{ sent *url.URL }

func (r *recorder) RoundTrip(req *http.Request) (*http.Response, error) {
	r.sent = req.URL
	return nil, errors.New("recorded, not sent")
}

func originRule(ctx context.Context, project, file string) error {
	raw, err := os.ReadFile(filepath.Join(filepath.Dir(os.Getenv("E2E_OPERATIONS")), file))
	if err != nil {
		return err
	}
	var spec struct {
		Cases [][3]*string `json:"cases"`
	}
	if err := json.Unmarshal(raw, &spec); err != nil {
		return err
	}
	var wrong []string
	for i, c := range spec.Cases {
		api, auth := &recorder{}, &recorder{}
		if client, err := v1.New(v1.Config{BaseURL: *c[0], Token: "eyJhbGciOiJFUzI1NiJ9.eyJzdWIiOiJ4In0.c2ln", HTTPClient: &http.Client{Transport: api}}); err == nil {
			_, _ = client.Database(project, "probe").Create(ctx)
		}
		key := "01JA0000000000000000000000_0123456789abcdefghjkmnpqrstvwxyz0123456789abcdefghjk"
		if client, err := v1.New(v1.Config{APIKey: key, TSAuthBaseURL: *c[0], HTTPClient: &http.Client{Transport: auth}}); err == nil {
			_, _ = client.CreateScopedKey(ctx, v1.ScopedKeyRequest{Database: v1.NewID(), Access: v1.ScopedKeyRead})
		}
		for column, sent := range []*url.URL{api.sent, auth.sent} {
			if !sameOrigin(sent, c[column+1]) {
				expected, got := "refused", "refused"
				if c[column+1] != nil {
					expected = *c[column+1]
				}
				if sent != nil {
					got = sent.Scheme + "://" + sent.Host
				}
				wrong = append(wrong, fmt.Sprintf("#%d %q %s: expected %s, got %s", i, (*c[0])[:min(len(*c[0]), 60)], []string{"api", "auth"}[column], expected, got))
			}
		}
	}
	if len(wrong) > 0 {
		return fmt.Errorf("origin rule: %d of %d checks differ: %s", len(wrong), 2*len(spec.Cases), strings.Join(wrong, "; "))
	}
	return nil
}

func sameOrigin(sent *url.URL, want *string) bool {
	if sent == nil || want == nil {
		return sent == nil && want == nil
	}
	expected, err := url.Parse(*want)
	if err != nil || sent.Scheme != expected.Scheme || sent.Port() != expected.Port() {
		return false
	}
	got, gotErr := netip.ParseAddr(sent.Hostname())
	wanted, wantErr := netip.ParseAddr(expected.Hostname())
	if gotErr == nil && wantErr == nil {
		return got == wanted
	}
	return sent.Hostname() == expected.Hostname()
}

func main() {
	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Minute)
	defer cancel()
	raw, err := os.ReadFile(os.Getenv("E2E_OPERATIONS"))
	if err != nil {
		panic(err)
	}
	var spec struct {
		Table      string      `json:"table"`
		Operations []operation `json:"operations"`
	}
	if err := json.Unmarshal(raw, &spec); err != nil {
		panic(err)
	}
	project := os.Getenv("TERRASCALE_PROJECT_ID")
	client, err := newClient(os.Getenv("TERRASCALE_API_KEY"))
	if err != nil {
		panic(err)
	}
	run := os.Getenv("E2E_RUN_ID")
	r := &runner{ctx: ctx, client: client, project: project, run: run, table: strings.NewReplacer("{sdk}", "go", "{run}", run).Replace(spec.Table)}
	group := os.Getenv("E2E_GROUP")
	if group == "a" || group == "c" {
		r.db = client.Database(project, os.Getenv("TERRASCALE_DATABASE_NAME"))
	}
	failed := false
	for _, op := range spec.Operations {
		if op.Group != group || op.Needs != "" && !(os.Getenv("E2E_REGIONS") == "2" && strings.HasPrefix(op.ID, "placement.")) {
			continue
		}
		started := time.Now()
		line := map[string]any{"id": op.ID, "ok": true}
		if err := r.do(op); err != nil {
			failed = true
			line = map[string]any{"id": op.ID, "ok": false, "error": err.Error()}
		}
		line["ms"] = time.Since(started).Milliseconds()
		out, _ := json.Marshal(line)
		fmt.Println(string(out))
	}
	if failed {
		os.Exit(1)
	}
}

func (r *runner) placementCreate() (result error) {
	created, err := r.client.CreateDatabase(r.ctx, r.project, "e2e-go-"+r.run+"-placed", v1.CreateDatabaseOptions{Regions: []string{"gru", "fra"}})
	if err != nil {
		return err
	}
	defer func() { result = errors.Join(result, r.client.DeleteDatabase(r.ctx, r.project, created.ID)) }()
	if err := same(created.Regions, []string{"fra", "gru"}, "created regions"); err != nil {
		return err
	}
	repeated, err := r.client.SetDatabasePlacement(r.ctx, r.project, created.ID, v1.DatabasePlacement{Regions: []string{"fra", "gru"}})
	if err != nil {
		return err
	}
	return same(repeated.PlacementVersion, created.PlacementVersion, "unchanged placement version")
}

func (r *runner) placementSet() (result error) {
	created, err := r.client.CreateDatabase(r.ctx, r.project, "e2e-go-"+r.run+"-move", v1.CreateDatabaseOptions{Regions: []string{"gru"}})
	if err != nil {
		return err
	}
	placed := r.client
	defer func() { result = errors.Join(result, placed.DeleteDatabase(r.ctx, r.project, created.ID)) }()
	db := r.client.Database(r.project, created.ID)
	if _, err := db.Execute(r.ctx, "CREATE TABLE placement_rows (value INTEGER PRIMARY KEY)"); err != nil {
		return err
	}
	if _, err := db.Execute(r.ctx, "INSERT INTO placement_rows VALUES (1)"); err != nil {
		return err
	}
	added, err := r.client.SetDatabasePlacement(r.ctx, r.project, created.ID, v1.DatabasePlacement{Regions: []string{"gru", "fra"}})
	if err != nil {
		return err
	}
	if err := same(added.Regions, []string{"fra", "gru"}, "added region"); err != nil {
		return err
	}
	if added.PlacementVersion <= created.PlacementVersion {
		return errors.New("membership did not raise placement version")
	}
	progress, err := db.Regions(r.ctx)
	if err != nil {
		return err
	}
	if len(progress.Regions) != 2 || progress.Regions[0].Region != "fra" || progress.Regions[1].Region != "gru" || progress.Regions[1].Role != "writer" {
		return fmt.Errorf("wrong status regions: %+v", progress.Regions)
	}
	ctx, cancel := context.WithTimeout(r.ctx, 90*time.Second)
	defer cancel()
	var updated *v1.ManagedDatabase
	for {
		updated, err = r.client.SetDatabasePlacement(ctx, r.project, created.ID, v1.DatabasePlacement{Regions: []string{"fra"}})
		if err == nil {
			break
		}
		if !errors.Is(err, v1.ErrReplicaNotReady) {
			return err
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(time.Second):
		}
	}
	placed, err = v1.New(v1.Config{APIKey: os.Getenv("TERRASCALE_API_KEY"), BaseURL: "https://fra.api.terrascale.tech", TSAuthBaseURL: "https://auth.terrascale.tech"})
	if err != nil {
		return err
	}
	if err := same(updated.Regions, []string{"fra"}, "dropped writer"); err != nil {
		return err
	}
	if updated.PlacementVersion <= added.PlacementVersion {
		return errors.New("writer did not raise placement version")
	}
	after, err := placed.Database(r.project, created.ID).Regions(r.ctx)
	if err != nil {
		return err
	}
	if len(after.Regions) != 1 || after.Regions[0].Region != "fra" || after.Regions[0].Role != "writer" {
		return errors.New("wrong moved status")
	}
	_, err = db.Regions(r.ctx)
	if !errors.Is(err, v1.ErrWrongRegion) {
		return fmt.Errorf("nonmember endpoint should return wrong_region, got %v", err)
	}
	rows, err := placed.Database(r.project, created.ID).Query(r.ctx, "SELECT value FROM placement_rows")
	if err != nil {
		return err
	}
	if err := same(cells(rows), [][]any{{int64(1)}}, "data survived placement"); err != nil {
		return err
	}
	fmt.Println("go placement: create [gru,fra], add fra, repeat drop gru -> 200, status fra writer, 421 wrong_region")
	return nil
}

package main

import (
	"context"
	"database/sql"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"reflect"
	"strings"
	"time"

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
	Outside       string         `json:"outside"`
	OutsideStatus int            `json:"outsideStatus"`
	MintStatus    int            `json:"mintStatus"`
	DeletedStatus int            `json:"deletedStatus"`
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
	ctx := r.ctx
	switch op.ID {
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
		return errors.New("v1.Database has no execute for a Statement value; sdk/SQL-API.md lists execute(statement)")
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
	case "agent.create_ttl":
		ttl := op.TTLSeconds
		created, err := r.client.CreateDatabase(ctx, r.project, r.fill(op.Name), v1.CreateDatabaseOptions{TTLSeconds: &ttl})
		if err != nil {
			return err
		}
		if created.ExpiresAt == nil {
			return errors.New("the database has no expiry")
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
		narrow, err := newClient(scoped.Key)
		if err != nil {
			return err
		}
		if _, err := narrow.Database(r.project, r.agent.ID).Execute(ctx, op.SQL); err != nil {
			return err
		}
		_, outside := narrow.Database(r.project, r.fill(op.Outside)).Execute(ctx, op.SQL)
		if err := same(status(outside), op.OutsideStatus, "outside its database"); err != nil {
			return err
		}
		_, mint := narrow.CreateScopedKey(ctx, v1.ScopedKeyRequest{Project: r.project, Database: r.agent.ID, Access: v1.ScopedKeyRead})
		return same(status(mint), op.MintStatus, "minting with a scoped key")
	case "agent.delete":
		if err := r.client.DeleteDatabase(ctx, r.project, r.agent.ID); err != nil {
			return err
		}
		_, err := r.client.Database(r.project, r.agent.ID).Execute(ctx, op.SQL)
		return same(status(err), op.DeletedStatus, "the deleted database")
	}
	return fmt.Errorf("no implementation for %s", op.ID)
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
	if group == "a" {
		r.db = client.Database(project, os.Getenv("TERRASCALE_DATABASE_NAME"))
	}
	failed := false
	for _, op := range spec.Operations {
		if op.Group != group || op.Needs != "" {
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

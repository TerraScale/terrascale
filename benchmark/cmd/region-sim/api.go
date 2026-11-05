package main

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"time"
)

type sqlAPI struct {
	hc      *http.Client
	auth    *authStandIn
	org     string
	project string
	db      string

	token  string
	minted time.Time
}

type stmt struct {
	SQL  string `json:"sql"`
	Args []any  `json:"args,omitempty"`
}

type stmtResult struct {
	Columns      []json.RawMessage `json:"columns"`
	Rows         [][]any           `json:"rows"`
	RowsAffected int64             `json:"rows_affected"`
}

type apiReply struct {
	status  int
	code    string
	results []stmtResult
	body    string
	timing  string
}

func newSQLAPI(auth *authStandIn, org, project, db string) *sqlAPI {
	return &sqlAPI{hc: &http.Client{Timeout: 30 * time.Second, Transport: &http.Transport{MaxIdleConnsPerHost: 8}},
		auth: auth, org: org, project: project, db: db}
}

func (s *sqlAPI) bearer() string {
	if s.token == "" || time.Since(s.minted) > 30*time.Minute {
		s.token, s.minted = s.auth.projectToken(s.org, s.project), time.Now()
	}
	return s.token
}

func (s *sqlAPI) exec(ctx context.Context, n *node, statements ...stmt) (apiReply, error) {
	return s.execDurable(ctx, n, "local", statements...)
}

func (s *sqlAPI) execDurable(ctx context.Context, n *node, durability string, statements ...stmt) (apiReply, error) {
	body, _ := json.Marshal(map[string]any{"statements": statements, "durability": durability})
	url := fmt.Sprintf("%s/v1/projects/%s/databases/%s/sql/execute", n.base(), s.project, s.db)
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, url, bytes.NewReader(body))
	if err != nil {
		return apiReply{}, err
	}
	req.Host = apiHost
	req.Header.Set("Authorization", "Bearer "+s.bearer())
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("Accept", "application/json")
	res, err := s.hc.Do(req)
	if err != nil {
		return apiReply{}, err
	}
	defer res.Body.Close()
	data, err := io.ReadAll(io.LimitReader(res.Body, 64<<20))
	if err != nil {
		return apiReply{}, err
	}
	r := apiReply{status: res.StatusCode, body: snippet(data), timing: res.Header.Get("Server-Timing") + " route=" + res.Header.Get("TerraScale-Write-Route")}
	if res.StatusCode/100 == 2 {
		var out struct {
			Results []stmtResult `json:"results"`
		}
		d := json.NewDecoder(bytes.NewReader(data))
		d.UseNumber()
		if err := d.Decode(&out); err != nil {
			return r, fmt.Errorf("decode SQL response: %w", err)
		}
		r.results = out.Results
		return r, nil
	}
	var env struct {
		Error struct {
			Code string `json:"code"`
		} `json:"error"`
	}
	if json.Unmarshal(data, &env) == nil {
		r.code = env.Error.Code
	}
	return r, nil
}

func (s *sqlAPI) query(ctx context.Context, n *node, sql string, args ...any) (string, apiReply, error) {
	r, err := s.exec(ctx, n, stmt{SQL: sql, Args: args})
	if err != nil || r.status != http.StatusOK || len(r.results) != 1 {
		return "", r, err
	}
	b, _ := json.Marshal(r.results[0].Rows)
	return string(b), r, nil
}

func (s *sqlAPI) mustExec(ctx context.Context, n *node, statements ...stmt) error {
	ctx, cancel := context.WithTimeout(ctx, 5*time.Second)
	defer cancel()
	var last error
	err := pollEvery(ctx, 5*time.Second, 20*time.Millisecond, func() (bool, error) {
		r, err := s.exec(ctx, n, statements...)
		if err != nil {
			return false, err
		}
		if r.status == http.StatusOK {
			return true, nil
		}
		last = fmt.Errorf("%s answered %d %s: %s", n.name, r.status, r.code, r.body)
		if r.status == http.StatusConflict && r.code == "serialization_conflict" {
			return false, nil
		}
		return false, last
	})
	if err != nil && last != nil {
		return fmt.Errorf("%w; last refusal: %v", err, last)
	}
	return err
}

func (s *sqlAPI) waitFor(ctx context.Context, n *node, timeout, interval time.Duration, want, sql string, args ...any) (time.Time, error) {
	var last string
	var at time.Time
	err := pollEvery(ctx, timeout, interval, func() (bool, error) {
		got, r, err := s.query(ctx, n, sql, args...)
		switch {
		case err != nil:
			last = err.Error()
		case r.status != http.StatusOK:
			last = fmt.Sprintf("%d %s %s", r.status, r.code, r.body)
		case got != want:
			last = "rows " + truncate(got, 160)
		default:
			at = time.Now()
			return true, nil
		}
		return false, nil
	})
	if err != nil {
		return at, fmt.Errorf("%s did not show %s within %s; last: %s", n.name, truncate(want, 80), timeout, last)
	}
	return at, nil
}

func snippet(b []byte) string { return truncate(string(b), 300) }

func truncate(s string, n int) string {
	if len(s) <= n {
		return s
	}
	return s[:n] + "…"
}
